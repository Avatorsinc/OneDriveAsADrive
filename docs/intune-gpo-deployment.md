# IT deployment (Intune / GPO / SCCM)

OneDriveAsADrive is built for fleet rollout: there's **no server** and **no app registration**, and auth rides each user's own Windows session (WAM), so it's per-user by design. You push one config file and run the installer.

## Overview

1. Author a machine-wide `config.json` describing the drives every user should get.
2. Deploy it to `%ProgramData%\OneDriveAsADrive\config.json`.
3. Run the installer (Setup.exe silent, or `install.ps1 -Config`).
4. (Recommended) Have a Global Admin grant tenant-wide [admin consent](admin-consent.md) once, so users see zero prompts.
5. (Optional) If you need any of it *enforced*, add the [administrative template](#enforcing-settings-admx--registry-policy).

## How settings resolve

Settings resolve **per field**, most authoritative first:

| # | Source | Path | Enforced? |
|---|--------|------|-----------|
| 1 | Admin policy | `HKLM\SOFTWARE\Policies\OneDriveAsADrive` (then `HKCU\…`) | Yes — locked, user cannot change |
| 2 | The user's own setting | `%LOCALAPPDATA%\OneDriveAsADrive\config.json` | — |
| 3 | Your deployed config | `%ProgramData%\OneDriveAsADrive\config.json` | No — a starting point, not a leash |
| 4 | Built-in default | — | Single OneDrive on `Z:`, port 40323 |

Per field means exactly that: you can deploy a `config.json` that sets the port and the drives, have a user change only their drives, and the port you set still applies.

> **Changed in 1.3.** Previously `%ProgramData%` won outright over `%LOCALAPPDATA%`. It no longer does. Users now get a settings page and their own choices stick, so a deployed `config.json` is a **seed**, not enforcement. If you relied on `%ProgramData%` to hold a configuration in place, see below — you now have to say so explicitly.

Two ways to get hard enforcement back:

- **`"allowUserOverride": false`** in the machine-wide `config.json`. Locks every field in that file, no registry needed. Simple, but a standard user can write to `%ProgramData%\OneDriveAsADrive\` on default ACLs, so this is a guardrail against accidents, not against a determined user.
- **Registry policy** (below). The `Policies` hive is ACL'd to administrators by construction, so this is the one that actually holds. Use it when enforcement matters.

## config.json

```json
{
  "port": 40323,
  "account": "user@contoso.com",
  "allowUserOverride": true,
  "mounts": [
    { "letter": "O", "type": "onedrive", "name": "OneDrive" },
    { "letter": "S", "type": "sharepoint",
      "site": "contoso.sharepoint.com:/sites/Finance",
      "library": "Documents", "name": "Finance" }
  ]
}
```

Omit `account` to let the broker pick the signed-in account. `allowUserOverride` defaults to `true` and is only read from the machine-wide file. See [`config.example.json`](../config.example.json).

## Enforcing settings (ADMX / registry policy)

The administrative template lives in [`deploy/policy/`](../deploy/policy/). Five settings, all optional, all off by default:

| Setting | Registry value | Type | Effect |
|---------|----------------|------|--------|
| Local port | `Port` | DWORD | Fixes the loopback port |
| Sign-in account | `Account` | SZ | Pins the UPN to sign in as |
| Drives to mount | `Mounts` | SZ (JSON array) | Fixes the exact drive list |
| Allow users to manage their own drives | `AllowUserMounts` | DWORD | `0` freezes the drive list from `config.json` |
| Turn off the settings page | `DisableSettingsUi` | DWORD | `1` removes the UI entirely |

Anything configured here is locked: the settings page shows it as *Managed by your organization* and rejects changes to it. Anything **not** configured stays the user's. `HKLM` beats `HKCU`, so a device-targeted profile wins over a user-targeted one.

Prefer the narrow settings over `DisableSettingsUi`. Turning off the page also removes the sign-in and re-map buttons, so a user with an expired token or a broken mapping has no self-service path and calls you instead.

**Intune (ADMX ingestion):**

1. **Devices → Configuration → Create → Import ADMX**, upload `OneDriveAsADrive.admx` and `en-US\OneDriveAsADrive.adml`.
2. Create an **Imported Administrative templates** profile and configure the settings you want.
3. Assign to devices (`HKLM`) or users (`HKCU`).

**Intune (OMA-URI)**, if you'd rather skip ingestion — one Custom profile, one row per setting:

```
./Device/Vendor/MSFT/Registry/HKLM/SOFTWARE/Policies/OneDriveAsADrive/AllowUserMounts
```

**GPO:** copy the `.admx` into `%SystemRoot%\PolicyDefinitions` (or the domain Central Store) and the `.adml` into the matching `en-US` folder, then find the settings under **Computer Configuration → Administrative Templates → OneDriveAsADrive**.

**Plain registry**, for a quick test or a config script:

```powershell
$k = 'HKLM:\SOFTWARE\Policies\OneDriveAsADrive'
New-Item $k -Force | Out-Null
Set-ItemProperty $k -Name AllowUserMounts -Value 0 -Type DWord
Set-ItemProperty $k -Name Mounts -Type String -Value '[{"letter":"S","type":"sharepoint","site":"contoso.sharepoint.com:/sites/Finance","library":"Documents","name":"Finance"}]'
```

Policy is read at startup, so changes take effect the next time OneDriveAsADrive launches. A port or account change also needs the drives re-mapping, because the mapping embeds the port.

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

Both configure the Windows WebClient service + `BasicAuthLevel`, register the per-user background Scheduled Task, add a **OneDriveAsADrive Settings** Start Menu shortcut, and map the configured drives.

## Delivery methods

- **Intune (Win32 app):** wrap `Setup.exe` (`.intunewin`), install command `OneDriveAsADrive-Setup.exe /VERYSILENT`, detection = the installed exe / Apps entry. Deploy the `config.json` first via a separate Win32 app, a PowerShell script, or a configuration profile that writes `%ProgramData%\OneDriveAsADrive\config.json`.
- **GPO:** a computer startup script that copies `config.json` to `%ProgramData%\OneDriveAsADrive\` and runs the installer; or a scheduled-task GPO.
- **SCCM / login script:** same pattern — stage config, run installer.

## First-run sign-in at scale

Delegated auth needs the user to sign in once. With tenant-wide admin consent granted, that first sign-in is a silent WAM pass (no prompt) on machines where the user already has a work account. Without it, each user gets one Microsoft sign-in the first time the drive is accessed.

Users can re-run sign-in themselves from the settings page (Start Menu → **OneDriveAsADrive Settings**) instead of raising a ticket — unless you've turned that page off.

## Rollback / uninstall

- **Per machine:** remove via Settings → Apps, or `Setup.exe /VERYSILENT /uninstall`.
- **Per user leftovers** (mapped drives, task, secret) clean up with the uninstaller; in separate-admin scenarios, run `npx onedriveasadrive uninstall` as the user. See the README Uninstall section for the elevation caveat.
- **Revert the machine-wide WebDAV settings** if nothing else needs them: set `HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters\BasicAuthLevel` back to `1`, and remove `FileAttributesLimitInBytes` / `FileSizeLimitInBytes` to restore the Windows defaults.
- **Drop the policy** by deleting `HKLM\SOFTWARE\Policies\OneDriveAsADrive` (or setting each policy back to Not Configured). Users get their own settings back at the next start.

## Security

See [SECURITY.md](../SECURITY.md) for the threat model, the loopback + per-install-secret controls, and what the logs contain.
