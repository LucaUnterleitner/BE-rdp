# Build

## Prerequisites

- Windows 10 2004 (build 19041) or later, x64. Windows Server 2019+ works.
- .NET SDK 10.0 (tested with 10.0.401). Visual Studio is not required.
- For packaging: internet access to nuget.org on the first run (the script downloads `Microsoft.Windows.SDK.BuildTools`, which contains `makeappx`, `makepri` and `signtool`).
- Optional: Node.js, only to regenerate `src/RdpManager.App/Themes/Icons.xaml` from an SVG icon set (`tools/make-icons-xaml.js`).

## Repository layout

```
RdpManager.slnx                  solution
Directory.Build.props            shared settings, product version
src/RdpManager.Core              models, validation, .rdp files, importers, filters (no Windows APIs)
src/RdpManager.Infrastructure    storage, migration, policy, credentials, sessions, Entra ID, updates
src/RdpManager.App               WPF app (views, view models, tabs) and the session host mode
tests/RdpManager.Tests           xUnit tests
packaging/                       MSIX manifest and signing helpers
build.ps1                        test → publish → MSIX → sign → release folder
tools/perf/                      startup and memory measurement
```

## Local development

```powershell
dotnet build RdpManager.slnx
dotnet run --project src\RdpManager.App -- --data-dir C:\temp\bp-rdp-dev
```

Command-line switches (only honored when the app runs **unpackaged**):

| Switch | Effect |
|---|---|
| `--data-dir <folder>` | Uses `<folder>\data`, `<folder>\local` and reads Electron data from `<folder>\electron`. Keeps test data away from real data. |
| `--perf-report <file>` | Runs the scripted performance measurement and writes JSON (see PERFORMANCE.md). |

Switches that always work: `--connect=<system id>` (used by the jump list and tray), `--hidden` (start in the notification area).

The app log is in `%LOCALAPPDATA%\BearingPoint\RemoteDesktop\logs\app.log` (or `<data-dir>\local\logs`).

## Tests

```powershell
dotnet test RdpManager.slnx
```

67 tests: all test cases of the Electron version (ported), plus migration, persistence, policy, Credential Manager round trip, reachability probe, view models, icons and regression tests. The Credential Manager and probe tests use the real Windows APIs (a temporary `TERMSRV/rdpmanager-test-*.invalid` entry and a local TCP listener).

## Release build

See [RELEASE.md](RELEASE.md).
