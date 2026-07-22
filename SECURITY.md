# Security

OneDriveAsADrive runs a small local WebDAV server that holds a Microsoft Graph token and bridges it to Windows File Explorer. Because that server can reach your OneDrive/SharePoint files, access to it is controlled deliberately. This document is the threat model and the reasoning behind each control.

## What the server is, and why it's local

Windows' built-in WebDAV redirector can't do modern OAuth2/MFA auth, so it can't talk to Microsoft 365 directly. OneDriveAsADrive sits in between: it authenticates to Microsoft Graph with **your own account** (via the Windows Account Broker / MSAL — the app never sees your password), and exposes a plain WebDAV endpoint on `http://localhost` that Windows *can* map as a drive. The server binds to loopback only and is never intended to be reachable off the machine.

## Controls

- **Loopback binding + client-IP check.** The server binds to `localhost`. The middleware also rejects any request whose remote IP isn't a loopback address, so even a misconfiguration like `--urls http://*:40323` won't expose it off-box.
- **Host-header check (anti-DNS-rebinding).** Requests whose `Host` isn't a loopback name are refused. This closes the DNS-rebinding vector where a malicious website rebinds its domain to `127.0.0.1` and tries to reach the server through your browser.
- **Per-install secret (HTTP Basic auth).** A 32-byte cryptographically random secret is generated on first run and stored in `%LOCALAPPDATA%\OneDriveAsADrive\.secret`. Every request must present it (constant-time compared). This stops other local users or low-privilege processes on a shared machine from reaching your files. It's why the installer sets `BasicAuthLevel=2` — so the Windows WebDAV client will send Basic credentials over loopback HTTP.
- **Least-privilege Graph scopes.** OneDrive-only setups request `Files.ReadWrite` (your own drive). SharePoint mounts widen to `Files.ReadWrite.All` + `Sites.Read.All` **only when a SharePoint mount is configured**, and those typically require a one-time tenant admin consent.
- **Verified downloads.** The installer verifies each release zip/Setup.exe against its published SHA256 before running it (guards transit tampering/corruption — not a compromised GitHub account, which could republish the hash).

## Threat model

**Protects against:** other users and untrusted processes on the same machine; web-based attacks (DNS rebinding); off-host access via misconfiguration; tampered downloads in transit.

**Does not protect against:** malware already running as **you** — such code can read your token cache and files regardless of this app. Loopback Basic auth is unencrypted, which is fine over `127.0.0.1` but means you must not expose this server off the host.

## What the logs contain

The persistent log at `%LOCALAPPDATA%\OneDriveAsADrive\logs\app.log` **never contains the secret** — the `net use` line is written with `<secret>` redacted. The real, copy-pasteable command is printed only to the console when you run the app with `--console`/`--debug` on purpose. No telemetry is collected; see [PRIVACY.md](PRIVACY.md).

## Reporting a vulnerability

Please open a [GitHub issue](https://github.com/Avatorsinc/OneDriveAsADrive/issues) for non-sensitive reports, or for anything you'd rather not disclose publicly, use GitHub's private security advisory feature on the repository. There's no bounty — this is a personal open-source project — but reports are genuinely appreciated.
