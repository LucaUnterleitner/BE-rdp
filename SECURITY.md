# Security

## Secrets

- The app never stores passwords. "Save password" writes a generic credential `TERMSRV/<host>` to Windows Credential Manager, which `mstsc.exe` and the RDP control use for CredSSP. The app reads back only whether a credential exists and its user name. The managed password copy is zeroed after `CredWrite`.
- Passwords are never sent to the session host, never written to `.rdp` files and never logged. The audit log drops `password`/`token` keys as a second line of defense, and the diagnostic log masks values that look like secrets.
- Imported files are untrusted: stored passwords (`password 51`, RDCMan/mRemoteNG encrypted passwords) and start programs are discarded, drive redirection is imported as off, and server identity checks and NLA are never weakened. The import preview lists every dropped item.
- Entra ID: public client without a secret. Tokens stay in the Windows broker (WAM) or in memory; MSAL logging runs with PII logging off. No tenant or client ids are in the repository.
- Code signing: no certificates, private keys or passwords in the repository. Signing uses the certificate store or a PFX password from an environment variable (RELEASE.md).

## Input validation and injection

- Every system passes `ConnectionNormalizer` before it is saved or started: host names (`[A-Za-z0-9._-]`, IPv6), ports (1–65535), user names, gateway host, SSH key file and jump host, web paths. Ids must be plain UUIDs (they end up in jump list command lines).
- `.rdp` files: control characters are stripped from every value, so no extra properties can be injected.
- Processes are started with `ProcessStartInfo.ArgumentList` (no shell): `mstsc.exe`, `rdpsign.exe`, `explorer.exe`, the session host. The SSH terminal runs in `cmd.exe /d /v:off` with `--` before the host; every argument is re-checked against `" % & | < > ^ ! ( )`.
- Executables are started by absolute paths from `System32`.
- XML imports forbid DTDs (`DtdProcessing.Prohibit`, no resolver, size limit), which prevents entity expansion attacks.
- Web connections open only `http`/`https` URLs built from validated fields, without user information.
- The session host checks every field again, accepts no generic property setter and attaches only to windows of its parent process.

## Trust of external configuration

- **IT policy** (`%ProgramData%\BearingPoint\RdpClient\policy.json`) is used only when the file **and its folder** are owned by Administrators or SYSTEM. Standard users can create folders in `ProgramData`, so files owned by anyone else are ignored, with a warning in Settings.
- **Central system list**: Ed25519 signature checked before parsing (public key from the policy), older versions than the cached one rejected (rollback protection), expiry date honored, invalid entries skipped. Windows Integrated Authentication is sent only to the origin of the configured URL.
- **Group restriction and Entra roles** are enforced in `AppController`, so they also apply to the jump list, the tray and `--connect`.

## Local attack surface

- Single instance: a named pipe limited to the current user (`PipeOptions.CurrentUserOnly`) and Windows session. Only `--connect=<uuid>` and `--show` are acted on; ids are validated.
- Development switches (`--data-dir`, `--perf-report`) are ignored when the app runs from its MSIX package.
- Temporary `.rdp` files go to `%LOCALAPPDATA%\BearingPoint\RemoteDesktop\tmp` with random names. They are deleted as soon as mstsc has read them (connected event), when the session ends, and at the next start (older than 30 s).
- The MSIX runs as a medium-integrity full-trust app (`runFullTrust`). `unvirtualizedResources` is requested only to keep user data in the real AppData folders.

## Reporting

Report security problems to the BearingPoint IT security team, not in public issues.
