# BearingPoint Remote Desktop

An internal desktop app for finding remote systems, connecting to them and keeping track of the sessions. Real RDP connections run through the Windows Remote Desktop client (`mstsc.exe`). The app manages systems, settings, credentials and session state around it.

![Status](https://img.shields.io/badge/status-internal%20preview-lightgrey) ![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20%7C%20Server-blue) ![Version](https://img.shields.io/badge/version-0.3.0-informational)

> **Placeholders:** the brand colors in [src/renderer/styles/theme.css](src/renderer/styles/theme.css) and the app icon/logo are placeholders. Replace them with the approved values from the BearingPoint Brand Center before release, then re-check contrast (black text on `--bp-red`). The sample systems ("Load sample systems") are mock data and are labeled as such.

## Install (users)

1. Download `BearingPoint Remote Desktop <version>.msi` from the release or the internal software portal.
2. Run the installer. It installs for all users under `C:Program Files`.
3. Start **BearingPoint Remote Desktop** from the Start menu.

The installer is not code-signed yet, so Windows SmartScreen may show a warning. IT deployment (Intune or SCCM, policy file, central list) is described in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Quick start

- **Connect to any host:** press **Ctrl+K** or click **Connect to host or IP…**, type a computer name or IP address (optionally with `:port`) and press Enter. Windows then asks for username and password. Nothing is saved unless you tick **Save to My systems**.
- **Save a system:** go to **My systems** and click **Add system**. Only the address is required. Group, operating system, location, tags and description are under **Advanced settings**.
- **Import:** click **Import** to bring in `.rdp`, RDCMan or mRemoteNG files.

## Requirements

- Windows 10/11 or Windows Server 2019 or later, with the built-in Remote Desktop client (`mstsc.exe`).
- For development: Node.js 22 or later. No .NET or Rust toolchain is needed.

## Run and develop

```powershell
npm install
npm start          # starts the app (in VS Code terminals, unset ELECTRON_RUN_AS_NODE first)
npm test           # unit tests (node:test)
npm run dist:msi   # MSI installer, see docs/DEPLOYMENT.md
```

## Features

**Systems**
- Dashboard, My systems (grouped), Favorites, search across name, host, environment, OS, location and tags, collapsible filters, and card or list view.
- Add, edit, duplicate and remove systems.
- Import `.rdp` files, RDCMan (`.rdg`) and mRemoteNG (`confCons.xml`). Imported files are treated as untrusted: stored passwords, start programs and drive access are dropped, and XML is parsed without DTDs or entities.
- A central system list from IT, read-only and signed with Ed25519, with an offline cache (see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)).
- Export as `.rdp`.
- Availability check through a TCP probe on 3389, or on the gateway's port 443. Status is always shown as text and icon. An offline/VPN hint appears when nothing can be reached.

**Connecting**
- A connection dialog with display, multi-monitor, clipboard, drives, printers, audio, microphone, smart cards, RD Gateway, Remote Credential Guard / Restricted Admin, admin session and "sign in as a different user".
- Real progress steps, with a lock against double starts.
- Ctrl+K quick connect to any host name or IP address, without saving the system first. Recent systems in the taskbar jump list and the tray menu.

**Sessions**
- State from the mstsc process and the RDP client event log, matched by ProcessId: connecting, active, reconnecting, ended, failed.
- Error codes are explained in plain language (for example 0x204 and 0x104).
- Bring a session window to the front, disconnect (with an explanation of disconnect vs. sign out), reconnect.
- Recent sessions with duration and result.

**Security**
- Passwords live only in Windows Credential Manager (generic `TERMSRV/<host>`, through Win32 `CredWriteW` and koffi). The app never stores or displays them.
- Electron hardening: sandbox, contextIsolation, CSP, the `app://` protocol instead of `file://`, IPC sender checks, all permissions denied, navigation blocked, an `openExternal` allowlist and fuses.
- IT policy via `%ProgramData%\BearingPoint\RdpClient\policy.json`: group access, locked device options, launch mode, signing (see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)).
- A local audit log without secrets.

**Accessibility**
- Aimed at WCAG 2.2 AA:
  - Full keyboard operation, with focus trap and focus restore in dialogs.
  - Tooltips that can be hovered.
  - Status never conveyed by color alone.
  - Live announcements for search results.
- Support for `prefers-reduced-motion` and Windows high contrast (`forced-colors`).

## Architecture

| File | Responsibility |
|---|---|
| `src/main/main.js` | Electron main process: window, tray, jump list, IPC, connection flow, policy |
| `src/main/sessions.js` | Starting mstsc and tracking sessions through the shared event-log poller |
| `src/main/rdpfile.js` | `.rdp` generation, import and mstsc arguments |
| `src/main/win32.js` | Credential Manager, window focus, event log (wevtapi) and file owner checks (koffi, no PowerShell) |
| `src/main/central.js` | Signed central system list |
| `src/main/importers.js` | RDCMan and mRemoteNG import |
| `src/main/store.js` | Systems, settings, audit log and policy |
| `src/main/probe.js`, `errors.js` | Availability check and plain-language error texts |
| `src/renderer/` | UI (vanilla ES modules). `styles/theme.css` holds the design tokens. |

## Launch modes and the Windows security confirmation (April 2026)

Since the April 2026 update, Windows asks for confirmation every time an `.rdp` file is opened. The app supports three ways of dealing with this:

- **Connection file (default):** all options apply. Windows shows its confirmation dialog, and the app tells the user about it.
- **Signed connection file:** set `signingThumbprint` and have IT trust the publisher by GPO. The dialog then no longer appears.
- **Direct (`mstsc /v:`):** no dialog, but device options come from `Default.rdp`. This mode is automatically disabled when the policy locks device options.

## Project status and limitations

- **Version 0.3.0, internal preview.** Not yet released to users.
- Brand colors and the logo are placeholders (see above).
- The MSI is not code-signed yet.
- Sign-in against a real RDP host has not been tested end to end yet. Connection start, session tracking, the credential round trip and policy handling are tested.
- The maintenance mode for systems was removed for now and may come back later.
- Windows only. A macOS version would launch connections through Microsoft's Windows App instead of `mstsc.exe`, without the live session state.

## Contributing

1. Create a branch, make the change, run `npm test`.
2. Keep to the design system: tokens only in `theme.css`, status never conveyed by color alone, no white text on red.
3. Security-sensitive options must stay configurable through the IT policy.

Internal BearingPoint project. Not for external distribution.
