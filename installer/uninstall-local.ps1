# Per-user cleanup invoked by the Setup.exe uninstaller. IMPORTANT: this runs ELEVATED
# (Inno's UninstallRun can't runasoriginaluser), so HKCU/%LOCALAPPDATA% here resolve to
# whoever approved the UAC prompt. On the normal case (admin uninstalling on their own PC)
# that's the same profile that installed, so cleanup is complete. In a separate-admin
# install it's the admin's profile - the original user's leftovers are handled by
# `npx onedriveasadrive uninstall` run as that user (see README Uninstall). Don't assume
# this always runs as the installing user; future you, this comment is the warning.

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
