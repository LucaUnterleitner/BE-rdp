# Architecture

## Overview

```
BearingPoint.RemoteDesktop.exe            one executable, two modes
├─ app mode (WPF)                         window, tabs, dialogs, tray, jump list
│   RdpManager.App ──► RdpManager.Infrastructure ──► RdpManager.Core
└─ --session-host mode (WinForms)         one child process per session tab, hosts the RDP ActiveX control
```

| Project | Responsibility | Depends on |
|---|---|---|
| `RdpManager.Core` (`net10.0`) | Domain models (JSON-compatible with the Electron files), validation (`ConnectionNormalizer`), `.rdp` build/parse and mstsc arguments, disconnect reasons, RDCMan/mRemoteNG import, address parsing, signed central list (Ed25519, BouncyCastle), policy parsing, search/filter/status, mstsc session state machine, source-generated JSON | BouncyCastle |
| `RdpManager.Infrastructure` (`net10.0-windows`) | Paths, atomic JSON store with background writes, audit log, Electron data migration, policy file trust check, AD group check, Credential Manager (P/Invoke), TCP reachability probe, RDP client event log, session manager (mstsc, SSH, embedded), launchers, central list download, Entra ID (MSAL), MSIX package info, update check, start with Windows, diagnostic log | Core, MSAL, System.Diagnostics.EventLog |
| `RdpManager.App` (WPF) | Startup and composition (Microsoft.Extensions.DependencyInjection), view models (MVVM), views, themes, session tabs and windows, session host mode, tray, jump list, single instance | Core, Infrastructure |
| `RdpManager.Tests` | xUnit tests | all |

## Key design decisions

**WPF, not WinUI 3.** The in-app RDP tabs need the Remote Desktop ActiveX control (`mstscax.dll`). WPF/WinForms can host ActiveX; WinUI 3 cannot. WPF also brings jump lists, UI virtualization and no extra runtime. See MIGRATION.md.

**One session host process per tab.** As in the Electron version, each RDP tab runs the ActiveX control in its own process (`--session-host`). A crash of the control affects only that tab, and the control gets its own message loop with full keyboard handling. The app and the host talk over stdin/stdout, one JSON object per line. The host accepts a fixed command set (`connect`, `attach`, `resize`, `focus`, `disconnect`), never a password, and attaches only to windows owned by its parent process.

**Native slot instead of coordinates.** Each tab window contains a `SessionSlot` (an `HwndHost` around a plain child window). The session window of the host process becomes a child of this slot (`SetParent`), so WPF layout positions and clips it. Before a slot can disappear (tab moved, window closed, app quitting), the app moves the session window into a hidden parking window, because Windows destroys child windows together with their parent.

**Startup order.** The main window is created and shown first. Migration, policy, data and group check run on a background thread; the services are composed when they finish. Tray, jump list, reachability checks, central list, Entra ID and the update check start only when the list is on screen and the dispatcher is idle.

**Data on the UI thread, files in the background.** `DataStore` changes memory immediately and writes files on a serialized background queue (temp file + rename, retries for antivirus locks). The UI never waits for disk I/O; write failures are reported to the user. The audit log uses its own background queue.

**Lists as virtualized rows.** Dashboard, systems, favorites and recent sessions are one `ItemsControl` with a recycling `VirtualizingStackPanel` and heterogeneous rows (section headers, card rows, list rows, history rows). Only visible rows have elements. Search is debounced (120 ms) and matches prebuilt lower-case search keys. Status changes update bound properties of `ConnectionItem` and do not rebuild lists (except when a status filter is active).

**Same data formats as Electron.** `connections.json`, `settings.json`, `central-state.json` and `audit.log` keep the Electron schema; unknown properties are preserved (`JsonExtensionData`). The native app uses its own folder (`%APPDATA%\BearingPoint\RemoteDesktop`) and imports the Electron folder once.

**Security boundaries.** The UI passes connection ids; hosts, credentials and launch parameters are resolved and validated in `AppController`/`ConnectionNormalizer`. Processes are started with argument lists (no shell), except the SSH terminal, which needs `cmd.exe` and is protected by strict validation plus a second character check.

## Main flows

- **Connect** (`AppController.ConnectAsync`): validate and apply policy → TCP probe (gateway when configured) → credentials check → tab (session host) or separate window (`mstsc` with temporary or signed `.rdp` file, or direct arguments) → audit. `UiFlows.RunConnectAsync` shows the step-by-step progress dialog.
- **Session state**: tabs from ActiveX events; separate windows from the process lifetime and the RDP client event log (events 1024/1026/1027) through `MstscSessionStateMachine`.
- **Migration** (`ElectronDataMigrator`): backup → validate each record → merge (never overwrite) → verify by reading back → marker. Repeatable from Settings.

## Deliberate changes

| Area | Electron | Native |
|---|---|---|
| Installer | MSI (electron-builder) | MSIX |
| Data folder | `%APPDATA%\BearingPoint\RdpClient` | `%APPDATA%\BearingPoint\RemoteDesktop` (Electron data imported once) |
| Session host | separate `BpRdpHost.exe` (.NET Framework, compiled with in-box csc) | same exe in `--session-host` mode (.NET 10) |
| Event log fallback | PowerShell | not needed (in-process `EventLogReader`) |
| Group check | `whoami /groups` | Windows logon token |
| View preferences | browser localStorage | `settings.json` |
| Header and search bar | scroll with the page | stay fixed while the list scrolls |
| Window size and position | not remembered | remembered per computer |
| New | – | Entra ID sign-in, update notice for `.appinstaller` installs, start with Windows through the MSIX startup task |
