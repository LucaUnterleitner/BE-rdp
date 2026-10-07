# Release

## Build a release

```powershell
.\build.ps1 -DevCertificate                       # test build, signed with a local development certificate
.\build.ps1 -Version 1.0.0 -CertificateThumbprint <sha1>   # production certificate from the certificate store
```

The script runs, in this order (it stops at the first error):

1. `dotnet test` (Release).
2. `dotnet publish` of the app: self-contained, `win-x64`, ReadyToRun. `.pdb` files are moved to a separate symbols folder, so they are not in the user package.
3. MSIX layout: publish output, `packaging/AppxManifest.xml` (version and publisher filled in), logo assets generated from `src/RdpManager.App/Assets/icon.png`, `resources.pri`.
4. `makeappx pack`.
5. Signing (see below).
6. `release/<version>/`:

| File | Content |
|---|---|
| `BearingPoint.RemoteDesktop_<v>.0_x64.msix` | Installable package |
| `BearingPoint.RemoteDesktop_<v>_win-x64_portable.zip` | The same x64 build without installer (runs from any folder) |
| `BearingPoint.RemoteDesktop_<v>_symbols.zip` | Debug symbols (`.pdb`), kept separate from the user package |
| `Install-DevBuild.ps1` | Helper to install a development-signed build on a test computer |
| `release-info.json` | Version, publisher, signature type, runtime, build time, commit |
| `SHA256SUMS.txt` | SHA-256 checksums of all files above |

`release/` and `artifacts/` are not committed. Attach the files to a GitHub release or put them on the software distribution share.

## Versions

- The version is set once in `Directory.Build.props` (`<Version>`), or with `-Version` on the command line.
- The MSIX version is `<Major>.<Minor>.<Patch>.0`. Every release needs a higher version than the previous one, otherwise Windows and Intune do not treat it as an upgrade.
- **Never change** the package identity name `BearingPoint.RemoteDesktop` or the publisher of production builds. A different name or publisher makes Windows install a second, separate app.

## Code signing

MSIX packages must be signed. The manifest `Publisher` must exactly match the certificate subject; `build.ps1` checks this.

| Option | Use |
|---|---|
| `-CertificateThumbprint <sha1>` | Production. Certificate with private key in `Cert:\CurrentUser\My` or `Cert:\LocalMachine\My` (internal CA, smart card or HSM through its CSP). Timestamped. |
| `-PfxPath <file>` and `$env:BP_SIGN_PFX_PASSWORD` | Production in CI. The password comes from a secret variable, never from a file or a parameter in the repository. |
| `-DevCertificate` | Development and testing only. Creates `CN=BearingPoint Remote Desktop Development` in `Cert:\CurrentUser\My` (private key stays in the store). **Packages signed this way are not production-signed and must not be distributed.** |

No private keys, PFX files or passwords belong in the repository. `.gitignore` excludes `artifacts/` and `release/`.

The production certificate needs the Code Signing extended key usage (1.3.6.1.5.5.7.3.3). Target computers must trust its chain (an internal CA usually already is). For Azure Trusted Signing, sign the finished MSIX with `signtool` and the Trusted Signing dlib (`/dlib`, `/dmdf`) instead of `-CertificateThumbprint`.

## Installing a development build on a test computer

```powershell
# elevated PowerShell, in release\<version>
.\Install-DevBuild.ps1
```

The script trusts the package signer for app installs on that computer (`LocalMachine\TrustedPeople`) and runs `Add-AppxPackage`.

## CI/CD (outline)

A Windows runner with the .NET 10 SDK:

```yaml
- run: dotnet --info
- run: .\build.ps1 -Version ${{ env.VERSION }} -PfxPath $env:RUNNER_TEMP\codesign.pfx
  shell: pwsh
  env:
    BP_SIGN_PFX_PASSWORD: ${{ secrets.CODESIGN_PFX_PASSWORD }}
- uses: actions/upload-artifact@v4
  with: { name: release, path: release/** }
```

Write the PFX from a secret (base64) to `$env:RUNNER_TEMP` in a step before the build and delete it afterwards. Publish the test results from `artifacts/test-results`.

## Checklist before a release

1. Version increased in `Directory.Build.props`.
2. `.\build.ps1` succeeds; all tests pass.
3. Production certificate used (`release-info.json` → `signature`).
4. Install test on a clean computer: install, start, existing data taken over, connect, upgrade from the previous version, uninstall (see DEPLOYMENT.md).
5. Checksums published together with the files.
