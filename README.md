# OneDriveAsADrive

Mount your **OneDrive for Business** as a real Windows drive letter (e.g. `Z:\`) — no failed WebDAV connections, no app registration, no MFA prompts.

[![GitHub release](https://img.shields.io/github/v/release/Avatorsinc/OneDriveAsADrive)](https://github.com/Avatorsinc/OneDriveAsADrive/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Build](https://github.com/Avatorsinc/OneDriveAsADrive/actions/workflows/release.yml/badge.svg)](https://github.com/Avatorsinc/OneDriveAsADrive/actions)

---

## Screenshots

| App running | Drive in File Explorer |
|---|---|
| ![Terminal showing OneDriveAsADrive running](screenshots/terminal.png) | ![Z: drive in File Explorer](screenshots/explorer.png) |

---

## Why

Windows' built-in WebDAV client fails with modern Microsoft 365 accounts because those tenants require OAuth2 / modern auth — basic username/password WebDAV is dead. OneDriveAsADrive runs a **local** WebDAV server that speaks to Microsoft Graph API (which handles modern auth correctly) and lets Windows map it as a normal drive letter.

```
File Explorer ──► Z:\ (WebDAV on localhost)
                    │
              OneDriveAsADrive
                    │
              Microsoft Graph API
                    │
              OneDrive for Business
```

---

## Requirements

- Windows 10 or 11
- **OneDrive sync client installed and signed in** with your work/school Microsoft 365 account
- PowerShell 5.1+ (comes with Windows)
- No .NET install needed — the release is self-contained

---

## Quick Install

Open **PowerShell as Administrator** and run:

```powershell
iwr https://github.com/Avatorsinc/OneDriveAsADrive/releases/latest/download/install.ps1 -UseBasicParsing | iex
```

The installer will:
1. Download the latest release
2. Configure the Windows WebClient service for local HTTP WebDAV
3. Add a startup entry so the server runs automatically at login
4. Start the server and map `Z:` to your OneDrive

> **First run:** A Windows sign-in prompt may appear to select your work account. This happens once — after that it runs silently.

---

## Manual Install

1. Download the latest `OneDriveAsADrive-vX.X.X-win-x64.zip` from [Releases](https://github.com/Avatorsinc/OneDriveAsADrive/releases/latest)
2. Extract `OneDriveAsADrive.exe` anywhere
3. Run as **Administrator** once to configure WebClient:
   ```powershell
   Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters" -Name BasicAuthLevel -Value 2
   Set-Service WebClient -StartupType Automatic
   Start-Service WebClient
   ```
4. Run the app:
   ```powershell
   .\OneDriveAsADrive.exe
   ```
5. Map the drive (in a separate PowerShell window):
   ```powershell
   net use Z: http://localhost:8080/ /persistent:yes
   ```

---

## Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `--urls` | `http://localhost:8080` | Change the listening port |

Example — use port 9090 and map as `W:`:

```powershell
.\OneDriveAsADrive.exe --urls http://localhost:9090
net use W: http://localhost:9090/ /persistent:yes
```

---

## Uninstall

```powershell
# Remove drive mapping
net use Z: /delete

# Kill the server
Get-Process OneDriveAsADrive -ErrorAction SilentlyContinue | Stop-Process

# Remove startup entry and files
Remove-Item "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\OneDriveAsADrive.lnk" -Force -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\OneDriveAsADrive" -Recurse -Force -ErrorAction SilentlyContinue
```

---

## How It Works

1. **Auth** — Uses [MSAL.NET](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet) with the Windows Authentication Broker (WAM). WAM leverages your existing Windows work account session — the same account the OneDrive sync client is signed into — so no separate login or MFA is needed after the first consent.

2. **WebDAV server** — An ASP.NET Core app handles `PROPFIND`, `GET`, `PUT`, `DELETE`, `MKCOL`, and `MOVE` requests from Windows File Explorer / `net use`.

3. **Microsoft Graph** — All file operations go through the [Microsoft Graph API](https://learn.microsoft.com/en-us/graph/api/resources/onedrive) (`/me/drive`), which supports online-only files (Files On Demand) that the local sync folder doesn't show.

---

## Troubleshooting

**`net use` returns error 67 (network name not found)**
- Make sure the app is running before mapping the drive
- Ensure the WebClient service is running: `Start-Service WebClient`

**`net use` returns error 1312 (session does not exist)**
- Run `Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters" -Name BasicAuthLevel -Value 2` as Administrator, then `Restart-Service WebClient`

**Auth popup appears every time**
- This means WAM can't find a cached account. Sign in to your work account in Windows Settings → Accounts → Access work or school.

**Files show as 0 bytes or fail to open**
- Some file types are locked by OneDrive policies. Check if the file opens in the browser via your SharePoint URL (e.g. `https://yourtenant-my.sharepoint.com`).

---

## Building from Source

```powershell
git clone https://github.com/Avatorsinc/OneDriveAsADrive
cd OneDriveAsADrive
dotnet run
```

Publish a self-contained single-file exe:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/
```

---

## License

MIT — see [LICENSE](LICENSE).
