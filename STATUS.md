# Status (2026-10-07)

Version 0.9.0: native WPF/.NET 10 app. It replaces the Electron version, which is kept in the Git history (commit `8160f03`) and as a local copy.

## Verified

Automated: 67 xUnit tests pass. They cover all ported Electron test cases plus migration, persistence, policy, Credential Manager, probe, view models, icons and regressions.

Manual, actually run on this machine:

- **Debug and Release builds**
  - Start, dashboard and the other pages.
  - Add a system, search, open the details, edit, save settings.
  - Real RDP session to a test server in a tab, signed in with a saved credential.
  - Move the tab to its own window and back to the main window, close the tab with a confirmation.
  - Separate Remote Desktop window (mstsc, `.rdp` file): connection detected through the event log, temporary file removed, disconnect from the app.
  - Second start with `--connect` forwarded to the running app.
  - Electron data taken over, with a backup.
- **MSIX package** (signed with the development certificate)
  - Install.
  - Start with package identity.
  - User data in the real AppData folders.
  - Real Electron data taken over.

## Open

- **Performance**
  - Startup is still slower than Electron (see PERFORMANCE.md).
  - `--perf-report` has not been run yet.
- **Manual checks still to do**
  - Full screen and the session bar.
  - Dragging a tab with the mouse (the context menu path is tested).
  - Import and export dialogs.
  - Tray menu, jump list, start with Windows.
  - Upgrade from 0.9.0 to a higher version, and uninstall.
- **Needs input from IT or the user**
  - Real brand colors and icon.
  - Production code-signing certificate.
  - Entra app registration (tenant and client id) for a real sign-in test.
