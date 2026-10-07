# Deployment

For IT administrators who distribute BearingPoint Remote Desktop with Microsoft Intune.

## Package

- MSIX, x64, self-contained (no .NET runtime needs to be installed). Minimum Windows 10 2004 (19041).
- Installed per user by MSIX (Intune can deploy it in device or user context).
- AppData write virtualization is switched off in the manifest. User data therefore lives in the real folders and is kept across upgrades and uninstalls:
  - `%APPDATA%\BearingPoint\RemoteDesktop`: systems, settings, audit log, migration backup.
  - `%LOCALAPPDATA%\BearingPoint\RemoteDesktop`: logs, temporary connection files, central list cache.
- Saved passwords stay in Windows Credential Manager (`TERMSRV/<host>`), shared with `mstsc.exe` and the Electron version.

The MSI of the Electron version is no longer needed. MSIX gives clean installs, upgrades and uninstalls, and Intune distributes it natively.

## Intune (line-of-business app)

1. Intune admin center → Apps → Windows → Add → **Line-of-business app** → upload the signed `.msix`.
2. Assign as **Required** (or Available for enrolled devices).
3. The signing certificate must be trusted on the devices. An internal CA certificate is usually trusted already. Otherwise, deploy the certificate with a **Trusted certificate** profile.

### Updates

- Upload the new `.msix` with a **higher version** in the same Intune app (App properties → App package file). Intune replaces the installed version. Supersedence relationships do not apply to MSIX line-of-business apps.
- MSIX updates are in place: user data is kept, and the app is replaced when it is not running (or at the next start).
- The app has no updater of its own (no internet access needed). In-app update notice: when the package is installed from an `.appinstaller` file instead (for example a file share), the app checks for updates at start and shows a banner. Intune-managed installs do not show it, because Intune controls the version.
- Rollback: publish the older version with a higher version number, or reinstall the Electron MSI (see MIGRATION.md).

### Removing the Electron version

The native app takes over the Electron data on its first start (the Electron folder is only read). Keep the Electron app installed until users have started the new app once, then remove the Electron Win32 app assignment (uninstall: `msiexec /x {ProductCode} /qn`).

## Prerequisites on target systems

- Remote Desktop enabled with NLA, firewall rule "Remote Desktop – User Mode (TCP-In)", users in "Remote Desktop Users".
- Remote Credential Guard requires Kerberos, so systems must be addressed by FQDN.
- The client event log `Microsoft-Windows-TerminalServices-RDPClient/Operational` must stay enabled (used to track separate Remote Desktop windows).

## App policy

Deploy `%ProgramData%\BearingPoint\RdpClient\policy.json` (same path as the Electron version) as a Win32 app or remediation script running as SYSTEM. Example: [docs/policy.example.json](docs/policy.example.json).

**The file and its folder must be owned by `BUILTIN\Administrators` or `SYSTEM`.** Otherwise the policy is ignored and Settings shows a warning, because standard users can create folders in `ProgramData`. Deployment as SYSTEM sets the owner correctly. Manually: `icacls <path> /setowner *S-1-5-32-544`.

| Key | Effect |
|---|---|
| `allowedGroups` | Only members of these AD groups can use the app (full `DOMAIN\Group` or short name). |
| `serviceDeskName`, `helpUrl` | Shown on the "Access denied" page. `helpUrl` must be https. |
| `allowSavedCredentials` | `false` hides "Save password". |
| `launchMode` | `file` or `direct` for separate windows. `direct` becomes `file` when `redirect` locks exist. |
| `signingThumbprint` | SHA-256 thumbprint for `rdpsign.exe` (signed `.rdp` files avoid the Windows confirmation prompt). |
| `requireCredentialProtection` | `remoteGuard` or `restrictedAdmin` for every connection. |
| `redirect` | Locks `clipboard`, `printers`, `microphone`, `smartcards` (true/false), `drives` (`none`/`all`), `audio` (`local`/`remote`/`none`). |
| `allowedProtocols` | Subset of `rdp`, `ssh`, `web`. |
| `ssh.strictHostKeyChecking` | `ask`, `accept-new` or `yes`. |
| `notice` | `{ "title", "message" }` shown on the dashboard. |
| `centralList` | Signed central system list: `url` (https, Windows Integrated Authentication) or `path` (file/UNC), `publicKey` (Ed25519, SPKI DER, base64), optional `maxAgeHours`. |
| `entra` | Optional Microsoft Entra ID sign-in, see below. |

Unknown keys and wrong types are ignored.

### Central system list

Unchanged from the Electron version. IT publishes `systems.json` (`schema: "bp-rdp-systems/1"`, increasing `version`, optional `expiresAt`) and a detached Ed25519 signature `systems.json.sig`. The app verifies the signature, rejects older versions than the cached one and keeps the last verified copy for offline use. Entries may contain partial option groups; missing fields come from the user's defaults.

```powershell
openssl genpkey -algorithm ed25519 -out central-signing.pem                       # keep private
openssl pkey -in central-signing.pem -pubout -outform DER | openssl base64 -A     # value for "publicKey"
openssl pkeyutl -sign -inkey central-signing.pem -rawin -in systems.json | openssl base64 -A > systems.json.sig
```

## Microsoft Entra ID

Optional. Without an `entra` section the app does not sign in and has no Entra dependency at run time.

1. Entra admin center → App registrations → New registration, **single tenant**, name "BearingPoint Remote Desktop".
2. Authentication → Add a platform → **Mobile and desktop applications** → add the redirect URI `ms-appx-web://microsoft.aad.brokerplugin/<client id>` (Windows broker) and `http://localhost` (fallback without broker).
3. Do **not** create a client secret: the app is a public client.
4. API permissions: `User.Read` (delegated) is enough for sign-in. For later Microsoft Graph or backend calls, add the delegated permissions and put the scopes in `entra.scopes`.
5. Optional app roles (for example `RDP.User`) under App roles, assigned to groups in Enterprise applications. Set "Assignment required" to restrict sign-in to assigned users.

Policy section (replace the placeholders; the repository contains no real tenant or client ids):

```json
"entra": {
  "tenantId": "<directory (tenant) id>",
  "clientId": "<application (client) id>",
  "scopes": ["User.Read"],
  "requireSignIn": true,
  "allowedRoles": ["RDP.User"],
  "useBroker": true
}
```

- `requireSignIn`: the app can be used only after sign-in. `allowedRoles` is then also enforced (role claim in the ID token).
- With the broker (WAM), single sign-on uses the Windows account and the OS keeps the refresh tokens. Without the broker, tokens stay in memory for the app session only.
- Tokens and personal sign-in data are never written to logs.

## Audit and logs

- `%APPDATA%\BearingPoint\RemoteDesktop\audit.log`: JSON Lines in the same format as the Electron version, rotated at 5 MB (`audit.1.log`). Connections, results, error codes, changes; never passwords.
- `%LOCALAPPDATA%\BearingPoint\RemoteDesktop\logs\app.log`: technical log, rotated at 1 MB; values that look like secrets are masked.
