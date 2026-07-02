#Requires -Version 5.1
<#
.SYNOPSIS
    Installs OneDriveAsADrive — mounts your OneDrive for Business as a local drive letter.
.DESCRIPTION
    Downloads the latest release, configures the WebClient service for HTTP WebDAV,
    adds a startup entry, runs the server, and maps the drive.
.PARAMETER DriveLetter
    Drive letter to map (default: Z)
.PARAMETER Port
    Local port for the WebDAV server (default: 8080)
.EXAMPLE
    iwr https://github.com/Avatorsinc/OneDriveAsADrive/releases/latest/download/install.ps1 -UseBasicParsing | iex
.EXAMPLE
    .\install.ps1 -DriveLetter W -Port 9090
#>
param(
    [string]$DriveLetter = "Z",
    [int]$Port = 8080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoOwner  = "Avatorsinc"
$RepoName   = "OneDriveAsADrive"
$InstallDir = "$env:LOCALAPPDATA\$RepoName"
$ExePath    = "$InstallDir\$RepoName.exe"
$DriveMap   = "${DriveLetter}:"

function Write-Step([string]$msg) { Write-Host "  $msg" -ForegroundColor Cyan }
function Write-OK([string]$msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "  [!!] $msg" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "OneDriveAsADrive Installer" -ForegroundColor White
Write-Host "==========================" -ForegroundColor DarkGray
Write-Host ""

# ── 1. Download latest release ───────────────────────────────────────────────
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

# ── 1b. Verify SHA256 ─────────────────────────────────────────────────────────
# Don't run a self-contained exe fetched off the internet without checking it's the
# real one. Catches corrupted or tampered-in-transit downloads. (It does NOT protect
# against a compromised GitHub account — that attacker republishes the hash too.)
if ($hashAsset) {
    Write-Step "Verifying SHA256..."
    $expected = ((Invoke-WebRequest $hashAsset.browser_download_url -UseBasicParsing).Content -split '\s+')[0].Trim().ToUpper()
    $actual   = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToUpper()
    if ($expected -ne $actual) {
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        Write-Fail "SHA256 mismatch! Expected $expected but got $actual. Aborting — do NOT run this."
    }
    Write-OK "SHA256 verified"
} else {
    Write-Host "  [WARN] No .sha256 published for this release — skipping integrity check." -ForegroundColor Yellow
}

# ── 2. Extract ───────────────────────────────────────────────────────────────
Write-Step "Installing to $InstallDir..."
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Expand-Archive $zipPath $InstallDir -Force
Remove-Item $zipPath -Force
Write-OK "Extracted"

# ── 3. WebClient service + HTTP auth registry tweak ──────────────────────────
Write-Step "Configuring WebDAV (requires admin for registry)..."
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    $webClientParams = "HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters"
    Set-ItemProperty $webClientParams -Name BasicAuthLevel       -Value 2  -Type DWord -Force
    Set-ItemProperty $webClientParams -Name FileSizeLimitInBytes -Value 0xFFFFFFFF -Type DWord -Force
    Set-Service  WebClient -StartupType Automatic -ErrorAction SilentlyContinue
    Start-Service WebClient -ErrorAction SilentlyContinue
    Write-OK "WebClient configured"
} else {
    Write-Host "  [WARN] Not running as admin — skipping registry tweak." -ForegroundColor Yellow
    Write-Host "         Run this script as Administrator to enable HTTP WebDAV properly." -ForegroundColor DarkYellow
}

# ── 4. Startup entry (current user, no admin needed) ─────────────────────────
Write-Step "Adding startup entry..."
$startupFolder = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
$lnkPath       = "$startupFolder\$RepoName.lnk"
$shell    = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnkPath)
$shortcut.TargetPath  = $ExePath
$shortcut.Arguments   = "--urls http://localhost:$Port"
$shortcut.WindowStyle = 7
$shortcut.Description = "OneDriveAsADrive WebDAV bridge"
$shortcut.Save()
Write-OK "Will start automatically at login"

# ── 5. Start the server ───────────────────────────────────────────────────────
Write-Step "Starting OneDriveAsADrive..."
$proc = Start-Process $ExePath -ArgumentList "--urls http://localhost:$Port" -WindowStyle Minimized -PassThru
Start-Sleep -Seconds 4
if ($proc.HasExited) { Write-Fail "Server exited unexpectedly. Check Event Viewer / run manually for errors." }
Write-OK "Server running (PID $($proc.Id))"

# ── 6. Map the drive (with the per-install secret) ────────────────────────────
# The server generates a random secret on first start and writes it here. Read it
# and hand it to net use so Windows authenticates. No secret = no drive.
$secretPath = "$InstallDir\.secret"
$secret = $null
for ($i = 0; $i -lt 10 -and -not $secret; $i++) {
    if (Test-Path $secretPath) { $secret = (Get-Content $secretPath -Raw).Trim() }
    if (-not $secret) { Start-Sleep -Milliseconds 500 }
}
if (-not $secret) { Write-Fail "Server never wrote its secret file ($secretPath). Check the server window for errors." }

Write-Step "Mapping $DriveMap to http://localhost:$Port/..."
if (Test-Path "$DriveMap\") { net use $DriveMap /delete /y | Out-Null }
$result = net use $DriveMap "http://localhost:$Port/" /user:onedrive $secret /persistent:yes 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [WARN] net use failed: $result" -ForegroundColor Yellow
    Write-Host "         Try manually: net use $DriveMap http://localhost:$Port/ /user:onedrive <secret>" -ForegroundColor DarkYellow
    Write-Host "         (secret is in $secretPath)" -ForegroundColor DarkYellow
} else {
    Write-OK "Drive $DriveMap mapped"
}

Write-Host ""
Write-Host "Done! Open File Explorer and look for drive $DriveMap" -ForegroundColor Green
Write-Host ""
