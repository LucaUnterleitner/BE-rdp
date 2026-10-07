<#
.SYNOPSIS
  Creates (or reuses) a self-signed DEVELOPMENT code-signing certificate for test installs of the MSIX.

.DESCRIPTION
  The certificate is created in Cert:\CurrentUser\My (private key stays in the Windows certificate store,
  nothing is written to the repository). With -Trust (elevated), its public part is added to
  Cert:\LocalMachine\TrustedPeople so that Add-AppxPackage accepts packages signed with it on THIS computer.
  Packages signed with this certificate are NOT production-signed and must not be distributed.

.OUTPUTS
  The certificate thumbprint.
#>
param(
    [string]$Subject = 'CN=BearingPoint Remote Desktop Development',
    [switch]$Trust
)
$ErrorActionPreference = 'Stop'

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(7) -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Subject -KeyUsage DigitalSignature -FriendlyName 'BearingPoint Remote Desktop (development signing, not for distribution)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "Created development certificate $($cert.Thumbprint)"
}

if ($Trust) {
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint
    if (-not $trusted) {
        $tmp = Join-Path $env:TEMP "bp-rdp-dev-$($cert.Thumbprint).cer"
        Export-Certificate -Cert $cert -FilePath $tmp | Out-Null
        Import-Certificate -FilePath $tmp -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        Remove-Item $tmp
        Write-Host 'Trusted the development certificate for package installs on this computer (LocalMachine\TrustedPeople).'
    }
}
$cert.Thumbprint
