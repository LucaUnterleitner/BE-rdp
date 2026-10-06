# BearingPoint Remote Desktop

An internal Windows app for managing remote systems and connecting to them over RDP. Sessions open as tabs inside the app, using the Remote Desktop component built into Windows. Separate Remote Desktop windows (`mstsc.exe`) are available as an option.

## Features

- Sessions as tabs: drag a tab out to get its own window, drag it to the top edge of the screen for full screen, and drag it back onto a tab bar to dock it. Ctrl+Alt+Break toggles full screen.
- Save systems, mark favorites, search and filter.
- Quick connect (Ctrl+K): enter a host name or IP address and connect without saving the system.
- Import `.rdp`, RDCMan and mRemoteNG files.
- Shows running sessions and recent connections.
- Passwords are stored only in Windows Credential Manager.
- IT can configure the app through a policy file (see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)).

## Development

Requires Windows and Node.js 22.

```powershell
npm install
npm start          # start the app
npm test           # run the tests
npm run dist:msi   # build the MSI installer
```

In the VS Code terminal, unset `ELECTRON_RUN_AS_NODE` before `npm start`.

`npm start` and the dist scripts first compile the session helper `native/BpRdpHost.cs` with the C# compiler that ships with Windows (no SDK needed). The helper hosts one Remote Desktop session per process.

## Notes

- The brand colors in `src/renderer/styles/theme.css` and the app icon are placeholders.
- The installer is not code-signed yet.
- SSH and web connections are implemented but switched off. To enable them, add `ssh` and/or `web` to `ENABLED_PROTOCOLS` in `src/renderer/js/targets.js`.
