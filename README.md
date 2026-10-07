# BearingPoint Remote Desktop

Internal Windows app for managing remote systems and connecting to them over RDP. Native WPF app on .NET 10 (x64), distributed as MSIX. It replaces the Electron version 0.4.0 (last Electron commit: `8160f03`).

## Features

- Sessions as tabs inside the app (Remote Desktop ActiveX control built into Windows). Drag a tab out to its own window, dock it back, use full screen (Ctrl+Alt+Break). Separate Remote Desktop windows (`mstsc.exe`) are available as an option.
- Save systems, groups, favorites, tags, search and filters, card and list views, recent sessions with results.
- Quick connect (Ctrl+K) for a host name or IP address without saving it.
- Import `.rdp`, RDCMan (`.rdg`) and mRemoteNG (`confCons.xml`) files. Export `.rdp` files.
- Passwords are stored only in Windows Credential Manager (`TERMSRV/<host>`), never by the app.
- IT control through a policy file: group restriction, device redirection locks, signed central system list, optional Microsoft Entra ID sign-in.
- Takes over the data of the Electron version automatically on first start, with a backup.

## Documentation

| Document | Content |
|---|---|
| [BUILD.md](BUILD.md) | Prerequisites, local development, build, tests |
| [RELEASE.md](RELEASE.md) | Release build, MSIX, code signing, checksums |
| [DEPLOYMENT.md](DEPLOYMENT.md) | Intune distribution, updates, IT policy, Entra ID configuration |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Projects, components, design decisions |
| [MIGRATION.md](MIGRATION.md) | Electron → native migration matrix and user data migration |
| [SECURITY.md](SECURITY.md) | Security model and controls |
| [PERFORMANCE.md](PERFORMANCE.md) | Measurements and method |

## Quick start (development)

```powershell
dotnet test RdpManager.slnx
dotnet run --project src\RdpManager.App -- --data-dir C:\temp\bp-rdp-dev
```

`--data-dir` keeps development data separate from your real data (it is ignored in the installed app).

## Known limitations

- Brand colors (`src/RdpManager.App/Themes/Tokens.xaml`) and the app icon are placeholders.
- No production code-signing certificate yet: release builds are signed with a self-signed development certificate (see RELEASE.md).
- SSH and web connections are implemented but switched off (`Protocols.Enabled` in `src/RdpManager.Core/Models/Protocols.cs`), as in the Electron version.
- Entra ID sign-in is implemented but not tested against a real tenant (no app registration available yet).
- Startup time is still slower than the Electron version (about 1.1 s versus 0.6 s to a visible window); see PERFORMANCE.md.
