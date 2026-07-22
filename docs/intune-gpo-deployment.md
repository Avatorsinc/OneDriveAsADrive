# IT deployment (Intune / GPO / SCCM)

OneDriveAsADrive is built for fleet rollout: there's **no server** and **no app registration**, and auth rides each user's own Windows session (WAM), so it's per-user by design. You push one config file and run the installer.

## Overview

1. Author a machine-wide `config.json` describing the drives every user should get.
2. Deploy it to `%ProgramData%\OneDriveAsADrive\config.json`.
3. Run the installer (Setup.exe silent, or `install.ps1 -Config`).
4. (Recommended) Have a Global Admin grant tenant-wide [admin consent](admin-consent.md) once, so users see zero prompts.

Machine-wide config (`%ProgramData%`) wins over per-user (`%LOCALAPPDATA%`). No config at all → a single OneDrive on `Z:`.

## config.json

```json
{
  "port": 40323,
  "account": "user@contoso.com",
  "mounts": [
    { "letter": "O", "type": "onedrive", "name": "OneDrive" },
    { "letter": "S", "type": "sharepoint",
      "site": "contoso.sharepoint.com:/sites/Finance",
      "library": "Documents", "name": "Finance" }
  ]
}
```

Omit `account` to let the broker pick the signed-in account. See [`config.example.json`](../config.example.json).

## Silent install

**Setup.exe (winget-style):**

```powershell
OneDriveAsADrive-Setup-vX.Y.Z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

**Or the script installer with a bundled config (admin):**

```powershell
iwr https://github.com/Avatorsinc/OneDriveAsADrive/releases/latest/download/install.ps1 -UseBasicParsing -OutFile install.ps1
.\install.ps1 -Config .\config.json
```

Both configure the Windows WebClient service + `BasicAuthLevel`, register the per-user background Scheduled Task, and map the configured drives.

## Delivery methods

- **Intune (Win32 app):** wrap `Setup.exe` (`.intunewin`), install command `OneDriveAsADrive-Setup.exe /VERYSILENT`, detection = the installed exe / Apps entry. Deploy the `config.json` first via a separate Win32 app, a PowerShell script, or a configuration profile that writes `%ProgramData%\OneDriveAsADrive\config.json`.
- **GPO:** a computer startup script that copies `config.json` to `%ProgramData%\OneDriveAsADrive\` and runs the installer; or a scheduled-task GPO.
- **SCCM / login script:** same pattern — stage config, run installer.

## First-run sign-in at scale

Delegated auth needs the user to sign in once. With tenant-wide admin consent granted, that first sign-in is a silent WAM pass (no prompt) on machines where the user already has a work account. Without it, each user gets one Microsoft sign-in the first time the drive is accessed.

## Rollback / uninstall

- **Per machine:** remove via Settings → Apps, or `Setup.exe /VERYSILENT /uninstall`.
- **Per user leftovers** (mapped drives, task, secret) clean up with the uninstaller; in separate-admin scenarios, run `npx onedriveasadrive uninstall` as the user. See the README Uninstall section for the elevation caveat.
- **Revert the machine-wide WebDAV setting** if nothing else needs it: set `HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters\BasicAuthLevel` back to `1`.

## Security

See [SECURITY.md](../SECURITY.md) for the threat model, the loopback + per-install-secret controls, and what the logs contain.
