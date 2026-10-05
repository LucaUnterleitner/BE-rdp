# Deployment runbook

For IT administrators who package, sign and distribute BearingPoint Remote Desktop with Intune.

## Build

Requirements on the build machine: Node.js 22 and network access to npm and GitHub (electron-builder downloads Electron and WiX itself). No .NET SDK is needed.

```powershell
npm ci
npm test
npm run dist:msi        # → dist\BearingPoint Remote Desktop <version>.msi
```

The installer is per machine (`Program Files`), with no desktop shortcut and a Start menu entry.

**`msi.upgradeCode` in `package.json` must never change.** Windows uses it to recognize upgrades.

Electron fuses are set at build time:

- `RunAsNode` off
- Node CLI inspect arguments off
- `NODE_OPTIONS` ignored
- ASAR integrity validation on
- App code loads only from the ASAR

## Code signing (required before release)

Configure one option under `build.win` in `package.json`.

- **Azure Trusted Signing:** `azureSignOptions` (`publisherName`, `endpoint`, `codeSigningAccountName`, `certificateProfileName`). Provide `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET` as environment variables.
- **Internal CA certificate in the certificate store:** `signtoolOptions` with `certificateSubjectName` and `signingHashAlgorithms: ["sha256"]`.

Use the same publisher for AppLocker/WDAC rules.

## Intune (Win32 app)

1. Wrap the MSI with `IntuneWinAppUtil.exe -c <folder> -s "<name>.msi" -o out`.
2. Install command: `msiexec /i "BearingPoint Remote Desktop 0.3.0.msi" /qn /norestart`
3. Uninstall command: `msiexec /x {ProductCode} /qn`
4. Install behavior: System.
5. Detection rule: MSI product code, with a version check.
6. Updates: publish a new Win32 app that supersedes the previous one, and enable "Auto-update" on the Available assignment. The app has no built-in updater, which avoids proxy problems.

## App policy

Deploy `%ProgramData%\BearingPoint\RdpClient\policy.json` as a separate Win32 app or with a remediation script. Restrict write access to administrators.

**The file is only used if its owner is `BUILTIN\Administrators` or `SYSTEM`.** Standard users can create folders in `ProgramData`, so any other owner causes the file to be ignored, and Settings shows a warning. A deployment that runs as SYSTEM sets the owner correctly. To set it manually: `icacls policy.json /setowner *S-1-5-32-544`. The example is in [policy.example.json](policy.example.json).

| Key | Effect |
|---|---|
| `allowedGroups` | Only members of these groups can use the app. Matched against `whoami /groups`. Use the full name (`DOMAIN\Group`) or the short name. Everyone else sees "Access denied". |
| `serviceDeskName`, `helpUrl` | Shown on the access denied screen. `helpUrl` is the only external link the app opens (https only). |
| `allowSavedCredentials` | `false` hides "Save password". |
| `launchMode` | `file` or `direct` for everyone. `direct` is automatically replaced by `file` when `redirect` locks exist, because direct launches cannot enforce them. |
| `signingThumbprint` | SHA-256 thumbprint for `rdpsign.exe`. With the GPO "Specify SHA1 thumbprints of certificates representing trusted .rdp publishers", Windows no longer shows the security confirmation from April 2026. |
| `requireCredentialProtection` | `remoteGuard` or `restrictedAdmin` for every connection. |
| `redirect` | Locks device options: `clipboard`, `printers`, `microphone`, `smartcards` (true/false), `drives` (`none`/`all`), `audio` (`local`/`remote`/`none`). |
| `allowedProtocols` | Connection types users may create and start: any of `rdp`, `ssh`, `web`. All enabled types when not set. SSH and web are currently switched off in the app itself (`ENABLED_PROTOCOLS`). |
| `ssh.strictHostKeyChecking` | `ask` (default: ssh asks on first contact), `accept-new` (accept new host keys, refuse changed ones) or `yes` (only known hosts). |
| `notice` | Notice on the dashboard. |
| `centralList` | Central read-only system list: `url` (https, Windows Integrated Authentication) or `path` (file/UNC), plus `publicKey` (Ed25519, SPKI DER, base64). Optional `maxAgeHours` (default 72). |

Unknown keys and wrong types are ignored. Server-side GPOs for device redirection still apply in any case.

## Audit log

The local log is `%APPDATA%\BearingPoint\RdpClient\audit.log`. It is JSON Lines and rotates at 5 MB (the previous file is kept as `audit.1.log`). It contains connections, results, error codes and changes, and never passwords.

The client event log `Microsoft-Windows-TerminalServices-RDPClient/Operational` must stay enabled. The app reads events 1024, 1026 and 1027 from it to track session state.

## Central system list

IT publishes `systems.json` and a detached signature `systems.json.sig` next to it. The app verifies the signature before using the list, rejects older versions (rollback protection) and caches the last verified copy in `%LOCALAPPDATA%\BearingPoint\RdpClient\central-cache` for offline use. Users see these systems as read-only ("Managed by IT"). They can mark them as favorites and duplicate them as personal systems.

```json
{ "schema": "bp-rdp-systems/1", "version": 42, "issuedAt": "2026-10-01T08:00:00Z", "expiresAt": "2026-12-31T00:00:00Z",
  "systems": [{ "id": "8f0c6a1e-2b3c-4d5e-8f9a-0b1c2d3e4f5a", "name": "Finance Prod", "host": "fin-prod.bpnet.local",
    "folder": "Finance", "os": "Windows Server 2022", "tags": ["sap"],
    "gateway": { "mode": "always", "host": "rdgw.example.com" }, "security": { "credentialProtection": "remoteGuard" } }] }
```

Rules:

- `id` must be a UUID and stay stable for each system.
- Increase `version` with every publication.
- Entries with invalid fields are skipped.

Create the key and signature once on a protected machine, then sign every new version:

```powershell
openssl genpkey -algorithm ed25519 -out central-signing.pem                       # keep private
openssl pkey -in central-signing.pem -pubout -outform DER | openssl base64 -A     # value for "publicKey"
openssl pkeyutl -sign -inkey central-signing.pem -rawin -in systems.json | openssl base64 -A > systems.json.sig
```

## Event log reading

The app reads the RDP client log in-process through `wevtapi.dll`, so it does not start PowerShell processes that EDR products would flag. PowerShell is used only as a fallback if that native call fails.

## Prerequisites on target systems

- Remote Desktop enabled with NLA.
- Firewall rule "Remote Desktop – User Mode (TCP-In)".
- Users are members of "Remote Desktop Users".
- Remote Credential Guard requires Kerberos, so systems must be addressed by FQDN.
