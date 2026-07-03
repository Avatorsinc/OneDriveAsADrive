# Per-user cleanup for the Setup.exe uninstaller. Runs as the ORIGINAL user (not the
# elevated uninstaller) because the scheduled task, mapped drives, and %LOCALAPPDATA%
# files all live in the user's world. Undoes everything install.ps1 set up - the
# rare Griffin family plan that actually cleans up after itself.

$RepoName = "OneDriveAsADrive"

# Stop the server and drop the logon task.
taskkill /IM "$RepoName.exe" /F 2>$null | Out-Null
schtasks /Delete /TN $RepoName /F 2>$null | Out-Null

# Kill the PERSISTENT mappings at the source: HKCU:\Network\<letter> is where Windows
# remembers drives to reconnect at logon. Elevation reaches these (same user hive), so
# even from the elevated uninstaller the drives die for good at next logon.
#
# GOTCHA (this cost us a release): net use DISPLAYS the mapping as "\\localhost@8080\z",
# but the RemotePath actually STORED here is the literal URL "http://localhost:8080/z/".
# Matching the pretty display form finds nothing. Match "localhost" and be done. Roadhouse.
Get-ChildItem "HKCU:\Network" -ErrorAction SilentlyContinue | ForEach-Object {
    $remote = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).RemotePath
    if ("$remote" -like '*localhost*') {
        # Drop the live mapping first (harmless if this session can't see it), then the key.
        net use "$($_.PSChildName):" /delete /y 2>$null | Out-Null
        Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Belt-and-braces: also sweep the live session by its displayed UNC form, in case a mapping
# exists without a persistent registry entry. [A-Za-z] lives in a single-quoted literal so
# Windows PowerShell 5.1 doesn't misparse it as a type literal.
$pat = '([A-Za-z]):\s+\\\\localhost@\d+'
foreach ($line in (net use 2>$null)) {
    if ($line -match $pat) { net use "$($matches[1]):" /delete /y 2>$null | Out-Null }
}

# Drop the Explorer drive-label registry keys we created (##localhost@PORT#letter).
Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2" -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -like '*localhost@*' } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# Startup-shortcut fallback (only exists if task registration failed at install time).
Remove-Item "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\$RepoName.lnk" -Force -ErrorAction SilentlyContinue

# Per-user files: exe copy, logs, .secret, per-user config. All of it. ROADHOUSE.
Remove-Item "$env:LOCALAPPDATA\$RepoName" -Recurse -Force -ErrorAction SilentlyContinue
