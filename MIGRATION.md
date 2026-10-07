# Migration: Electron → native Windows app (WPF, .NET 10)

This document records the inventory of the Electron app (v0.4.0), the mapping to the native app in this
repository, and how user data is migrated. The Electron app is the functional and visual reference and the
fallback: its source is in the Git history of this repository (last Electron commit `8160f03`, release v0.3.0
and the v0.4.0 MSI) and in a separate local folder (`BE-Rdp-Electron`).

## 1. Technology decision

| Option | Result |
|---|---|
| **WPF on .NET 10 (LTS)** | **Chosen.** Hosts the RDP ActiveX control (`mstscax.dll`) through WinForms `AxHost` exactly like the existing helper, has native `JumpList`, UI virtualization, no extra runtime (Windows App SDK) and the shortest path from the existing C# helper. |
| WinUI 3 | Rejected. No supported way to host ActiveX controls or WinForms in WinUI 3, so the in-app RDP tabs (the main feature since v0.4.0) would need a separate top-level window per session anyway. Adds the Windows App SDK runtime as a deployment dependency. No requirement in this project benefits from WinUI 3. |

No embedded browser (WebView2/Chromium) is used anywhere.

## 2. Migration matrix

Status: ✅ migrated · 🔁 migrated with a deliberate change · ⏸ migrated but switched off (as in Electron) · ➕ new in the native app

| Electron component | C# component | Status | Technical risk | Adjustments |
|---|---|---|---|---|
| `src/main/main.js` (app lifecycle, IPC, single instance, `--connect=`, `--hidden`) | `RdpManager.App/Program.cs`, `App.xaml.cs`, `Services/SingleInstance.cs`, `ViewModels/MainViewModel.cs` | ✅ | Low | IPC is replaced by direct calls between view models and services. Second instances forward `--connect=<id>` over a per-user named pipe. |
| `src/main/preload.js` (`window.rdp` bridge, channel allow-list) | – (not needed) | 🔁 | – | No renderer/process boundary any more; the security role of the bridge (only ids cross it, the host is resolved in the trusted process) is kept: dialogs pass connection ids, services resolve hosts. |
| `src/main/store.js` (connections.json, settings.json, central-state.json, audit.log, normalizeConnection, policy, group check) | `RdpManager.Core/Validation/ConnectionNormalizer.cs`, `Core/Models/*`, `Infrastructure/Storage/*`, `Infrastructure/Policy/PolicyReader.cs`, `Infrastructure/Security/GroupAccessChecker.cs` | ✅ | Medium (data compatibility) | Same JSON schema (System.Text.Json source generation). Atomic writes with retry kept. Group check uses the Windows token (`WindowsIdentity.Groups`) instead of `whoami.exe`. |
| `src/main/rdpfile.js` (.rdp build/parse, direct args, policy locks, address parsing) | `RdpManager.Core/Rdp/RdpFile.cs` | ✅ | Low | 1:1 port, same tests. |
| `src/main/errors.js` (disconnect reasons, probe explanations) | `RdpManager.Core/Rdp/DisconnectReasons.cs` | ✅ | Low | 1:1 port. |
| `src/main/importers.js` (RDCMan .rdg, mRemoteNG confCons.xml) | `RdpManager.Core/Import/*` | ✅ | Medium (XML edge cases) | `XmlReader` with `DtdProcessing.Prohibit` replaces fast-xml-parser. Same inheritance rules and secret dropping. |
| `src/main/central.js` (signed central system list, Ed25519, rollback protection, offline cache) | `Core/Central/CentralListDocument.cs`, `Infrastructure/Central/CentralListService.cs` | ✅ | Medium (crypto) | Ed25519 via BouncyCastle (no Ed25519 in .NET 10 BCL). Windows Integrated Authentication only for the list's origin (`CredentialCache`). |
| `src/main/sessions.js` (mstsc start, temp .rdp files, rdpsign, event log state machine, polling) | `Core/Sessions/MstscSessionStateMachine.cs`, `Infrastructure/Rdp/MstscSessionLauncher.cs`, `Infrastructure/Rdp/RdpClientEventLog.cs` | ✅ | Medium | Event log read with `EventLogReader` (XPath, in-process) instead of koffi/wevtapi; PowerShell fallback dropped (not needed in-process). |
| `src/main/probe.js` (TCP reachability) | `Infrastructure/Network/ReachabilityProbe.cs` | ✅ | Low | `Socket.ConnectAsync` with timeout and `SocketError` mapping. |
| `src/main/win32.js` (Credential Manager, window focus, owner check, console start) | `Infrastructure/Credentials/WindowsCredentialStore.cs`, `Infrastructure/Windows/NativeMethods.cs`, `Policy/PolicyReader.cs` | ✅ | Low | P/Invoke (`LibraryImport`) instead of koffi. Same `TERMSRV/<host>` targets, so saved passwords keep working without migration. |
| `src/main/launchers.js` (SSH via OpenSSH, web URLs) | `Infrastructure/Launchers/SshLauncher.cs`, `WebLauncher.cs` | ⏸ | Low | Implemented, switched off through `Protocols.Enabled` (same as `ENABLED_PROTOCOLS`). |
| `src/main/embedded.js` + `native/BpRdpHost.cs` (one helper process per RDP tab, JSON over stdio) | `RdpManager.App/SessionHost/*` (runs as `BearingPoint.RemoteDesktop.exe --session-host`), `App/Sessions/EmbeddedSession.cs` | 🔁 | Medium (ActiveX, input focus) | The helper is now the same executable in a separate mode (one binary, one runtime, ReadyToRun). Same command set, same parent-window ownership check, System.Text.Json instead of JavaScriptSerializer. |
| `src/main/tabs.js` (tab hosts, drag-out, dock, top-edge full screen, full-screen bar) | `App/Sessions/SessionTabManager.cs`, `Views/SessionWindow.xaml`, `Views/PinBarWindow.xaml`, `Controls/SessionSlot.cs` | 🔁 | Medium | Sessions are attached to a native child "slot" (`HwndHost`) instead of absolute coordinates over a Chromium page, so no DirectComposition workaround is needed. |
| `src/main/samples.js` (mock systems) | `Core/Samples/SampleSystems.cs` | ✅ | Low | Flagged `sample: true`, `.invalid` hosts. |
| `src/main/ps.js` (PowerShell runner) | – | 🔁 removed | – | Only used as event-log fallback; the in-process reader makes it unnecessary (fewer EDR alerts). |
| Renderer `app.js` (shell, dashboard, systems, favorites, recent, details tabs, settings, help, access denied, error pages, notifications, toasts, quick connect, keyboard shortcuts) | `App/Views/*`, `App/ViewModels/*` | ✅ | Medium (UI parity) | Native WPF controls. Lists are virtualized (row model), search is debounced (120 ms) and filters run on prebuilt search keys. Header and search bar stay fixed while the list scrolls. |
| Renderer `dialogs.js` (connect with options, offer password, progress, add/edit, import preview, credentials) | `App/Views/Dialogs/*` | ✅ | Low | Modal WPF windows; they are top-level windows, so the RDP child window never hides them. |
| Renderer `tabstrip.js`, `pinbar.js`, `host.js` | `App/Controls/TabStrip.xaml`, `Views/PinBarWindow.xaml`, `Views/SessionWindow.xaml` | 🔁 | Medium | Drag-out follows the cursor natively; drop targets are other windows' tab strips. |
| `targets.js` (address parsing, enabled protocols) | `Core/Targets/AddressParser.cs`, `Core/Models/Protocols.cs` | ✅ | Low | 1:1 port. |
| `ui.js` helpers (toasts, focus trap, menus, tooltips, formatting, DOM patching) | WPF built-ins (`ContextMenu`, `ToolTip`, focus scopes), `App/Services/ToastService.cs`, `Core/Formatting/*` | 🔁 | Low | DOM patching is unnecessary: bindings update only changed properties. |
| `theme.css` / `app.css` (BearingPoint tokens, placeholders) | `App/Themes/Tokens.xaml`, `Controls.xaml`, `Icons.xaml` | ✅ | Low | Same tokens and placeholder brand colors; Lucide icon paths converted to WPF geometries. |
| Tray icon, jump list, start with Windows | `App/Services/TrayService.cs` (WinForms `NotifyIcon`), `JumpListService.cs`, `Infrastructure/Windows/StartupRegistration.cs` | 🔁 | Medium (MSIX) | Start with Windows uses the MSIX `StartupTask` when packaged and the `Run` key otherwise. |
| `docs/policy.example.json`, policy file `%ProgramData%\BearingPoint\RdpClient\policy.json` | `Infrastructure/Policy/PolicyReader.cs` | ✅ ➕ | Low | Same path and keys; new optional `entra` section. Owner check kept (Administrators/SYSTEM only). |
| electron-builder MSI (`package.json` → `build`) | `packaging/*` (MSIX), `build.ps1` | 🔁 | Medium | MSIX replaces the MSI (see DEPLOYMENT.md). The MSI is no longer needed: Intune deploys MSIX natively, and MSIX gives clean upgrades and uninstalls. |
| Electron fuses, CSP, sandbox | – | 🔁 | – | Not applicable: no web content, no script engine. |
| Tests (`test/*.test.js`, node:test) | `tests/RdpManager.Tests` (xUnit) | ✅ ➕ | Low | All JS test cases ported, plus migration, persistence, view model and filter tests. |
| No sign-in | `Infrastructure/Auth/EntraAuthService.cs` (MSAL.NET, WAM broker) | ➕ | Medium (tenant config) | Optional, configured through the policy file; switched off without configuration. |
| No updater | `Infrastructure/Updates/UpdateService.cs` | ➕ | Low | Shows "update available" when the MSIX was installed through an `.appinstaller` file; Intune-managed installs update through Intune. |
| No window placement memory | `App/Services/WindowPlacementService.cs` | ➕ | Low | Size, position and maximized state per computer, validated against current monitors. |

## 3. User data migration

| Item | Electron location | Native app location |
|---|---|---|
| Systems | `%APPDATA%\BearingPoint\RdpClient\connections.json` | `%APPDATA%\BearingPoint\RemoteDesktop\connections.json` |
| Settings | `…\RdpClient\settings.json` | `…\RemoteDesktop\settings.json` |
| Favorites/last use of IT systems | `…\RdpClient\central-state.json` | `…\RemoteDesktop\central-state.json` |
| Audit log | `…\RdpClient\audit.log` (+ `audit.1.log`) | `…\RemoteDesktop\audit.log` |
| Saved passwords | Windows Credential Manager `TERMSRV/<host>` | unchanged (same entries) |
| IT policy | `%ProgramData%\BearingPoint\RdpClient\policy.json` | unchanged (same file) |
| Central list cache | `%LOCALAPPDATA%\BearingPoint\RdpClient\central-cache` | `%LOCALAPPDATA%\BearingPoint\RemoteDesktop\central-cache` (re-downloaded and re-verified) |

Procedure (automatic on first start, repeatable from Settings → Data):

1. **Detect**: the Electron folder contains `connections.json` and the native folder has no completed migration marker.
2. **Back up**: all Electron files are copied to `%APPDATA%\BearingPoint\RemoteDesktop\migration-backup\<timestamp>\` before anything is read. The Electron folder itself is never modified.
3. **Convert and validate**: every record runs through the same validation as a saved system. Invalid records are not dropped silently: they are written to `migration-rejected.json` with the reason.
4. **Write** atomically (temp file + rename). Existing native data is merged by id, never overwritten blindly.
5. **Mark** completion in `migration.json` (source, counts, time). Only this last step makes the migration "done", so an interrupted migration simply runs again.
6. **Report** in the app (banner and Settings → Data). A damaged source file produces a clear message with the backup path; the app then starts with an empty list.

**Rollback:** the Electron app and its data folder are untouched. Uninstall the MSIX and reinstall the Electron MSI (or keep both installed in parallel); it continues with its own data as of the migration time. Systems added later in the native app can be exported as `.rdp` files.

## 4. Deliberate changes

See the final section of [ARCHITECTURE.md](ARCHITECTURE.md#deliberate-changes) and the known limitations in [README.md](README.md).
