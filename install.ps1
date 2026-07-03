#Requires -Version 5.1
<#
.SYNOPSIS
    Installs OneDriveAsADrive - mounts OneDrive and/or SharePoint libraries as local drive letters.
.DESCRIPTION
    Downloads the latest release, verifies it, configures the WebClient service for HTTP WebDAV,
    registers a hidden background Scheduled Task that runs at logon, starts the server, and maps
    every drive described in config.json (or a single OneDrive on Z: if there's no config).
.PARAMETER DriveLetter
    Drive letter for the default single-OneDrive mount when no config.json is present (default: Z).
.PARAMETER Port
    Local port for the WebDAV server (default: 8080, or the port from config.json).
.PARAMETER Config
    Optional path to a config.json to deploy machine-wide (to %ProgramData%). Requires admin.
    Use this to push SharePoint + OneDrive mounts to a machine (IT deployment).
.EXAMPLE
    iwr https://github.com/Avatorsinc/OneDriveAsADrive/releases/latest/download/install.ps1 -UseBasicParsing | iex
.EXAMPLE
    .\install.ps1 -Config .\config.json
#>
param(
    [string]$DriveLetter = "Z",
    [int]$Port = 8080,
    [string]$Config
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoOwner  = "Avatorsinc"
$RepoName   = "OneDriveAsADrive"
$InstallDir = "$env:LOCALAPPDATA\$RepoName"
$ExePath    = "$InstallDir\$RepoName.exe"
$TaskName   = "OneDriveAsADrive"

function Write-Step([string]$msg) { Write-Host "  $msg" -ForegroundColor Cyan }
function Write-OK([string]$msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "  [!!] $msg" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "OneDriveAsADrive Installer" -ForegroundColor White
Write-Host "==========================" -ForegroundColor DarkGray
Write-Host ""

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# -- 0. Deploy a supplied config.json machine-wide (IT scenario) ----------------
if ($Config) {
    if (-not (Test-Path $Config)) { Write-Fail "Config file not found: $Config" }
    if (-not $isAdmin) { Write-Fail "-Config deploys to %ProgramData% and needs Administrator." }
    $machineCfgDir = "$env:ProgramData\$RepoName"
    New-Item -ItemType Directory -Force $machineCfgDir | Out-Null
    Copy-Item $Config "$machineCfgDir\config.json" -Force
    Write-OK "Deployed config to $machineCfgDir\config.json"
}

# -- 1. Download latest release -----------------------------------------------
Write-Step "Fetching latest release..."
try {
    $release   = Invoke-RestMethod "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
    $asset     = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
    $hashAsset = $release.assets | Where-Object { $_.name -like "*.zip.sha256" } | Select-Object -First 1
    if (-not $asset) { Write-Fail "No zip asset found in latest release." }
} catch {
    Write-Fail "Could not reach GitHub API: $_"
}

$zipPath = "$env:TEMP\$RepoName.zip"
Write-Step "Downloading $($release.tag_name)..."
Invoke-WebRequest $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
Write-OK "Downloaded to $zipPath"

# -- 1b. Verify SHA256 ---------------------------------------------------------
# Don't run a self-contained exe fetched off the internet without checking it's the
# real one. Catches corrupted or tampered-in-transit downloads. (It does NOT protect
# against a compromised GitHub account - that attacker republishes the hash too.)
if ($hashAsset) {
    Write-Step "Verifying SHA256..."
    $expected = ((Invoke-WebRequest $hashAsset.browser_download_url -UseBasicParsing).Content -split '\s+')[0].Trim().ToUpper()
    $actual   = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToUpper()
    if ($expected -ne $actual) {
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        Write-Fail "SHA256 mismatch! Expected $expected but got $actual. Aborting - do NOT run this."
    }
    Write-OK "SHA256 verified"
} else {
    Write-Host "  [WARN] No .sha256 published for this release - skipping integrity check." -ForegroundColor Yellow
}

# -- 2. Extract ---------------------------------------------------------------
Write-Step "Installing to $InstallDir..."
# Stop any running instance so we can overwrite the exe.
Get-Process $RepoName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (Test-Path $InstallDir) {
    # Keep the .secret and any per-user config; nuke the rest.
    Get-ChildItem $InstallDir -Exclude ".secret","config.json" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Expand-Archive $zipPath $InstallDir -Force
Remove-Item $zipPath -Force
Write-OK "Extracted"

# -- 2b. Read effective config (to know which drives to map + the port) ---------
# The SERVER reads config.json too, so the installer and the exe MUST agree on both the
# drive letters and the port. If there's no config anywhere, we WRITE one - otherwise the
# server would fall back to a hardcoded Z: while we mapped -DriveLetter, and every request
# to the wrong prefix would 404. Config is the single source of truth for the port.
$cfg = $null
$cfgPath = $null
foreach ($p in @("$env:ProgramData\$RepoName\config.json", "$env:LOCALAPPDATA\$RepoName\config.json")) {
    if (Test-Path $p) {
        try { $cfg = Get-Content $p -Raw | ConvertFrom-Json; $cfgPath = $p; break }
        catch { Write-Host "  [WARN] Ignoring malformed $p" -ForegroundColor Yellow }
    }
}
if (-not $cfg) {
    $cfgPath = "$env:LOCALAPPDATA\$RepoName\config.json"
    New-Item -ItemType Directory -Force (Split-Path $cfgPath) | Out-Null
    $cfg = [pscustomobject]@{
        port   = $Port
        mounts = @([pscustomobject]@{ letter = $DriveLetter.ToUpper(); type = "onedrive"; name = "OneDrive" })
    }
    ($cfg | ConvertTo-Json -Depth 5) | Set-Content $cfgPath -Encoding UTF8
    Write-OK "Wrote default config to $cfgPath ($($DriveLetter.ToUpper()): -> OneDrive)"
} else {
    Write-OK "Using config: $cfgPath"
}
# Port comes from config so the background task and the drive mappings never disagree.
if ($PSBoundParameters.ContainsKey('Port') -and [int]$cfg.port -ne $Port) {
    Write-Host "  [WARN] -Port $Port ignored - config.json says port $($cfg.port). Edit config.json to change it." -ForegroundColor Yellow
}
$Port = [int]$cfg.port
$mounts = @($cfg.mounts)

# -- 3. WebClient service + HTTP auth registry tweak --------------------------
Write-Step "Configuring WebDAV (requires admin for registry)..."
if ($isAdmin) {
    $webClientParams = "HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters"
    Set-ItemProperty $webClientParams -Name BasicAuthLevel       -Value 2  -Type DWord -Force
    Set-ItemProperty $webClientParams -Name FileSizeLimitInBytes -Value 0xFFFFFFFF -Type DWord -Force
    Set-Service  WebClient -StartupType Automatic -ErrorAction SilentlyContinue
    Start-Service WebClient -ErrorAction SilentlyContinue
    Write-OK "WebClient configured"
} else {
    Write-Host "  [WARN] Not running as admin - skipping registry tweak." -ForegroundColor Yellow
    Write-Host "         Run this script as Administrator to enable HTTP WebDAV properly." -ForegroundColor DarkYellow
}

# -- 4. Background Scheduled Task (hidden, runs at each logon as this user) ------
# A Scheduled Task beats a Startup shortcut: it's harder to disable by accident, can
# restart on crash, and runs hidden. The exe is a WinExe so there's no window anyway -
# the user never notices it. Admins debug with:  OneDriveAsADrive.exe --console
Write-Step "Registering background task..."
try {
    # No --urls here: the exe reads the port from config.json, so an admin who later edits
    # config.json's port gets honored without re-registering the task.
    $action    = New-ScheduledTaskAction -Execute $ExePath
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -StartWhenAvailable -Hidden -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
                    -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description "OneDriveAsADrive WebDAV bridge (background)" -Force | Out-Null
    Write-OK "Task registered - starts hidden at every logon"
} catch {
    # Fall back to a plain Startup shortcut if task registration is blocked.
    Write-Host "  [WARN] Scheduled Task failed ($_). Falling back to Startup shortcut." -ForegroundColor Yellow
    $lnkPath  = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\$RepoName.lnk"
    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnkPath)
    $shortcut.TargetPath  = $ExePath
    $shortcut.Arguments   = ""
    $shortcut.WindowStyle = 7
    $shortcut.Description  = "OneDriveAsADrive WebDAV bridge"
    $shortcut.Save()
    Write-OK "Startup shortcut created"
}

# -- 5. Start the server now (hidden) ------------------------------------------
Write-Step "Starting OneDriveAsADrive..."
$proc = Start-Process $ExePath -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 4
if ($proc.HasExited) { Write-Fail "Server exited unexpectedly. Run '$ExePath --console' to see why." }
Write-OK "Server running in background (PID $($proc.Id))"

# -- 6. Read the per-install secret --------------------------------------------
# The server generates a random secret on first start and writes it here. No secret = no drive.
$secretPath = "$InstallDir\.secret"
$secret = $null
for ($i = 0; $i -lt 10 -and -not $secret; $i++) {
    if (Test-Path $secretPath) { $secret = (Get-Content $secretPath -Raw).Trim() }
    if (-not $secret) { Start-Sleep -Milliseconds 500 }
}
if (-not $secret) { Write-Fail "Server never wrote its secret file ($secretPath). Run '$ExePath --console'." }

# -- 6b. Migration: clear stale v1.1-style mappings to the server ROOT ----------
# v1.1 mapped drives straight to http://localhost:PORT/ (no prefix). v1.2 serves files only
# under /letter/ prefixes, so those old root mappings now 404 into a dead drive. Find and
# delete any drive pointing at this server's root (shown as \\localhost@PORT\DavWWWRoot).
try {
    # NB: build the pattern from a SINGLE-quoted literal + concat. A char class like [A-Za-z]
    # inside a double-quoted, $-interpolated string makes Windows PowerShell 5.1 misparse it as
    # a type literal ("Missing ] at end of attribute"). 5.1 is what the npx installer runs under.
    $rootPat = '([A-Za-z]):\s+\\\\localhost@' + $Port + '\\DavWWWRoot\b'
    foreach ($line in (net use 2>$null)) {
        if ($line -match $rootPat) {
            Write-Host "  [migrate] Removing stale root mapping $($matches[1]): (pre-v1.2)" -ForegroundColor DarkYellow
            net use "$($matches[1]):" /delete /y | Out-Null
        }
    }
} catch { }

# -- 7. Map every configured drive ---------------------------------------------
foreach ($m in $mounts) {
    $letter = "$($m.letter)".ToUpper()
    $drive  = "${letter}:"
    $prefix = "$($m.letter)".ToLower()
    $url    = "http://localhost:$Port/$prefix/"
    $label  = if ($m.PSObject.Properties.Name -contains 'name' -and $m.name) { $m.name } else { $m.type }

    Write-Step "Mapping $drive ($label) -> $url"
    if (Test-Path "$drive\") { net use $drive /delete /y | Out-Null }
    $result = net use $drive $url /user:onedrive $secret /persistent:yes 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [WARN] net use failed for ${drive}: $result" -ForegroundColor Yellow
        Write-Host "         Manual: net use $drive $url /user:onedrive <secret>  (secret in $secretPath)" -ForegroundColor DarkYellow
    } else {
        Write-OK "Drive $drive mapped"
        # Friendly Explorer label: without this the drive shows as "\\localhost@8080\s".
        # MountPoints2 key for \\localhost@PORT\x is ##localhost@PORT#x; _LabelFromReg is
        # what Explorer displays, so you get "S: (Finance)" instead of the raw path.
        try {
            $mpKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\##localhost@$Port#$prefix"
            if (-not (Test-Path $mpKey)) { New-Item -Path $mpKey -Force | Out-Null }
            New-ItemProperty -Path $mpKey -Name '_LabelFromReg' -Value $label -PropertyType String -Force | Out-Null
        } catch { }
    }
}

Write-Host ""
Write-Host "Done! Open File Explorer and look for your mapped drive(s)." -ForegroundColor Green
Write-Host "Debug anytime with:  `"$ExePath`" --console" -ForegroundColor DarkGray
Write-Host ""
