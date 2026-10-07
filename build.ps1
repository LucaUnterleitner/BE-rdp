<#
.SYNOPSIS
  Builds, tests, publishes and packages BearingPoint Remote Desktop (x64) and writes the release artifacts.

.DESCRIPTION
  1. dotnet test (all automated tests)
  2. dotnet publish: self-contained win-x64, ReadyToRun (fast startup); .pdb files go to a separate symbols folder
  3. MSIX layout: publish output + AppxManifest.xml + generated logo assets + resources.pri
  4. makeappx pack (tools from the NuGet package Microsoft.Windows.SDK.BuildTools, no Visual Studio needed)
  5. Signing, one of:
       -CertificateThumbprint <sha1>   certificate in Cert:\CurrentUser\My or LocalMachine\My (production: internal CA / HSM)
       -PfxPath <file> + env BP_SIGN_PFX_PASSWORD   (the password is never a parameter or a file in the repository)
       -DevCertificate                 self-signed development certificate (packaging\New-DevCertificate.ps1), NOT for distribution
       (none)                          unsigned package (Intune and Add-AppxPackage require a signature)
  6. release\<version>\: MSIX, portable x64 zip, symbols zip, SHA256SUMS.txt, release-info.json

.EXAMPLE
  .\build.ps1 -Version 0.9.0 -DevCertificate
.EXAMPLE
  $env:BP_SIGN_PFX_PASSWORD = '...'; .\build.ps1 -Version 1.0.0 -PfxPath C:\secure\codesign.pfx -Publisher 'CN=BearingPoint GmbH, O=BearingPoint, C=DE'
#>
param(
    [string]$Version = '',
    [string]$Publisher = '',
    [string]$CertificateThumbprint = '',
    [string]$PfxPath = '',
    [switch]$DevCertificate,
    [switch]$SkipTests,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

# ── Version ──────────────────────────────────────────────
if (-not $Version) {
    $Version = [regex]::Match((Get-Content (Join-Path $root 'Directory.Build.props') -Raw), '<Version[^>]*>([^<]+)</Version>').Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be Major.Minor.Patch, got '$Version'." }
$msixVersion = "$Version.0"   # MSIX needs four parts; the revision stays 0
Write-Host "== BearingPoint Remote Desktop $Version" -ForegroundColor Cyan

$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$symbols = Join-Path $artifacts 'symbols'
$layout = Join-Path $artifacts 'msix-layout'
$release = Join-Path $root "release\$Version"
foreach ($d in @($publish, $symbols, $layout, $release)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force }; New-Item -ItemType Directory $d -Force | Out-Null }

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE" }
}

# ── 1. Tests ─────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Host '== Tests' -ForegroundColor Cyan
    Invoke-Checked $dotnet @('test', (Join-Path $root 'RdpManager.slnx'), '-c', 'Release', '--nologo', "-p:Version=$Version", '--logger', "trx;LogFileName=test-results.trx", '--results-directory', (Join-Path $artifacts 'test-results'))
}

# ── 2. Publish ───────────────────────────────────────────
Write-Host '== Publish (self-contained, win-x64, ReadyToRun)' -ForegroundColor Cyan
Invoke-Checked $dotnet @('publish', (Join-Path $root 'src\RdpManager.App\RdpManager.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishReadyToRun=true', "-p:Version=$Version", '-p:DebugType=portable', '--nologo', '-o', $publish)
Get-ChildItem $publish -Filter *.pdb | Move-Item -Destination $symbols
if (-not (Test-Path (Join-Path $publish 'BearingPoint.RemoteDesktop.exe'))) { throw 'Publish output is missing the executable.' }

# ── 3. Build tools (makeappx, makepri, signtool) from NuGet ─
$toolsVersion = '10.0.28000.2705'
$toolsRoot = Join-Path $artifacts "tools\Microsoft.Windows.SDK.BuildTools.$toolsVersion"
if (-not (Test-Path $toolsRoot)) {
    Write-Host "== Downloading Microsoft.Windows.SDK.BuildTools $toolsVersion" -ForegroundColor Cyan
    $nupkg = "$toolsRoot.zip"
    New-Item -ItemType Directory (Split-Path $nupkg) -Force | Out-Null
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools/$toolsVersion" -OutFile $nupkg -UseBasicParsing
    Expand-Archive $nupkg $toolsRoot
    Remove-Item $nupkg
}
$bin = Get-ChildItem (Join-Path $toolsRoot 'bin') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$x64 = Join-Path $bin.FullName 'x64'
$makeappx = Join-Path $x64 'makeappx.exe'
$makepri = Join-Path $x64 'makepri.exe'
$signtool = Join-Path $x64 'signtool.exe'

# ── Signing identity (decides the manifest Publisher) ────
$signCert = $null
if ($DevCertificate) {
    $thumb = & (Join-Path $root 'packaging\New-DevCertificate.ps1') | Select-Object -Last 1
    $signCert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $thumb
} elseif ($CertificateThumbprint) {
    $signCert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object Thumbprint -eq $CertificateThumbprint | Select-Object -First 1
    if (-not $signCert) { throw "Certificate $CertificateThumbprint not found in CurrentUser\My or LocalMachine\My." }
} elseif ($PfxPath) {
    if (-not $env:BP_SIGN_PFX_PASSWORD) { throw 'Set the PFX password in the environment variable BP_SIGN_PFX_PASSWORD.' }
    $signCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $env:BP_SIGN_PFX_PASSWORD)
}
if (-not $Publisher) { $Publisher = if ($signCert) { $signCert.Subject } else { 'CN=BearingPoint Remote Desktop Development' } }
if ($signCert -and $signCert.Subject -ne $Publisher) { throw "Publisher '$Publisher' does not match the certificate subject '$($signCert.Subject)'." }

# ── 3b. MSIX layout ──────────────────────────────────────
Write-Host '== MSIX layout' -ForegroundColor Cyan
Copy-Item (Join-Path $publish '*') $layout -Recurse
$manifest = (Get-Content (Join-Path $root 'packaging\AppxManifest.xml') -Raw).Replace('$VERSION$', $msixVersion).Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($Publisher))
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))

# Logo assets from the 256 px app icon: base names plus scale and target-size variants (unplated for the taskbar).
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory $assets -Force | Out-Null
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'src\RdpManager.App\Assets\icon.png'))
function Save-Png([string]$name, [int]$size, [int]$canvas = 0) {
    if ($canvas -le 0) { $canvas = $size }
    $bmp = New-Object System.Drawing.Bitmap $canvas, $canvas
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $offset = [int](($canvas - $size) / 2)
    $g.DrawImage($icon, $offset, $offset, $size, $size)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
foreach ($scale in 100, 200, 400) {
    $f = $scale / 100
    Save-Png "Square44x44Logo.scale-$scale.png" ([int](44 * $f))
    Save-Png "Square150x150Logo.scale-$scale.png" ([int](100 * $f)) ([int](150 * $f))
    Save-Png "StoreLogo.scale-$scale.png" ([int](50 * $f))
}
foreach ($t in 16, 24, 32, 48, 256) {
    Save-Png "Square44x44Logo.targetsize-$t.png" $t
    Save-Png "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t
}
$icon.Dispose()

# resources.pri maps the qualified file names (scale, target size) to the names used in the manifest.
Push-Location $layout
try {
    Invoke-Checked $makepri @('createconfig', '/cf', (Join-Path $artifacts 'priconfig.xml'), '/dq', 'en-US', '/o')
    Invoke-Checked $makepri @('new', '/pr', $layout, '/cf', (Join-Path $artifacts 'priconfig.xml'), '/mn', (Join-Path $layout 'AppxManifest.xml'), '/of', (Join-Path $layout 'resources.pri'), '/o')
} finally { Pop-Location }

# ── 4. Pack ──────────────────────────────────────────────
$msixName = "BearingPoint.RemoteDesktop_${msixVersion}_x64.msix"
$msix = Join-Path $release $msixName
Write-Host "== makeappx pack -> $msixName" -ForegroundColor Cyan
Invoke-Checked $makeappx @('pack', '/d', $layout, '/p', $msix, '/h', 'SHA256', '/o')

# ── 5. Sign ──────────────────────────────────────────────
$signed = 'unsigned'
if ($signCert) {
    Write-Host '== Signing' -ForegroundColor Cyan
    if ($PfxPath) {
        Invoke-Checked $signtool @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $env:BP_SIGN_PFX_PASSWORD, '/tr', $TimestampUrl, '/td', 'SHA256', $msix)
    } else {
        $store = if (Test-Path "Cert:\CurrentUser\My\$($signCert.Thumbprint)") { @() } else { @('/sm') }
        $signArgs = @('sign', '/fd', 'SHA256', '/sha1', $signCert.Thumbprint) + $store
        if (-not $DevCertificate) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        Invoke-Checked $signtool ($signArgs + @($msix))
    }
    $signed = if ($DevCertificate) { "development certificate ($($signCert.Subject), self-signed, NOT for distribution)" } else { "production certificate ($($signCert.Subject))" }
}

# ── 6. Other release artifacts ───────────────────────────
Write-Host '== Release artifacts' -ForegroundColor Cyan
$portable = Join-Path $release "BearingPoint.RemoteDesktop_${Version}_win-x64_portable.zip"
Compress-Archive (Join-Path $publish '*') $portable
$symbolsZip = Join-Path $release "BearingPoint.RemoteDesktop_${Version}_symbols.zip"
Compress-Archive (Join-Path $symbols '*') $symbolsZip
Copy-Item (Join-Path $root 'packaging\Install-DevBuild.ps1') $release -ErrorAction SilentlyContinue

$info = [ordered]@{
    product = 'BearingPoint Remote Desktop'
    version = $Version
    msixVersion = $msixVersion
    publisher = $Publisher
    signature = $signed
    architecture = 'x64'
    runtime = 'self-contained .NET ' + (& $dotnet --version)
    built = (Get-Date).ToString('o')
    commit = (git -C $root rev-parse --short HEAD 2>$null)
}
$info | ConvertTo-Json | Set-Content (Join-Path $release 'release-info.json') -Encoding UTF8

$sums = Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "== Done: $release" -ForegroundColor Green
$sums
