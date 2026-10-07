<#
.SYNOPSIS
  Installs a development-signed MSIX on a test computer (run elevated).
.DESCRIPTION
  Trusts the signer of the package for app installs on this computer (LocalMachine\TrustedPeople), then
  installs or upgrades the package. Only for development builds; production packages are signed with a
  certificate the computers already trust, and Intune installs them.
#>
param([string]$Msix = (Get-ChildItem $PSScriptRoot -Filter *.msix | Select-Object -First 1).FullName)
$ErrorActionPreference = 'Stop'
$signature = Get-AuthenticodeSignature $Msix
if (-not $signature.SignerCertificate) { throw 'The package is not signed.' }
$cert = $signature.SignerCertificate
if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint)) {
    $tmp = Join-Path $env:TEMP "$($cert.Thumbprint).cer"
    [IO.File]::WriteAllBytes($tmp, $cert.Export('Cert'))
    Import-Certificate -FilePath $tmp -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Remove-Item $tmp
    Write-Host "Trusted $($cert.Subject) for app installs on this computer."
}
Add-AppxPackage -Path $Msix -ForceUpdateFromAnyVersion:$false
Get-AppxPackage BearingPoint.RemoteDesktop | Select-Object Name, Version, Publisher, InstallLocation
