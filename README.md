# BearingPoint Remote Desktop

An internal Windows app for managing remote systems and connecting to them over RDP. Connections run through the built-in Windows Remote Desktop client (`mstsc.exe`).

## Features

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

## Notes

- The brand colors in `src/renderer/styles/theme.css` and the app icon are placeholders.
- The installer is not code-signed yet.
