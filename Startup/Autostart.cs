using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace OneDriveAsADrive.Startup;

// The on/off switch for "does this come back by itself?".
//
// People ask for this as "run it as a service", and a real Windows Service is the wrong shape for
// this program: services run in session 0, which has no drive letters to map into your Explorer
// and no desktop for the account picker to appear on. What the request actually means is "start
// without me having to do anything", and on Windows the per-user answer to that is a logon
// Scheduled Task — the exact one install.ps1 already registers. This class owns that task so the
// tray and the settings page can turn it on and off without anyone opening Task Scheduler.
//
// "Enabled" here means the task exists. Toggling off deletes it rather than marking it disabled:
// existence is a single locale-independent exit code from schtasks, whereas reading a disabled
// flag back means parsing translated output. Enabling always re-creates from the XML below, so a
// task left behind by an older install pointing at a since-moved exe repairs itself on the way in.
[SupportedOSPlatform("windows")]
public sealed class Autostart(ILogger<Autostart> log)
{
    // The same name install.ps1 registers and uninstall-local.ps1 deletes. Picking a different one
    // here would orphan the installer's task and leave two copies racing for the port at logon.
    public const string TaskName = "OneDriveAsADrive";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public enum Mechanism
    {
        None,
        ScheduledTask,
        RunKey
    }

    public sealed record Status(bool Enabled, Mechanism Via)
    {
        // Shown verbatim in the settings page and the tray, so it says which of the two got used —
        // the difference matters when someone goes looking for it in Task Scheduler and it isn't
        // there because task registration was refused.
        public string Description => Via switch
        {
            Mechanism.ScheduledTask => $"Scheduled Task \"{TaskName}\", runs at every sign-in",
            Mechanism.RunKey => "Startup entry in your registry (a Scheduled Task wasn't allowed)",
            _ => "Not set to start automatically"
        };
    }

    // Last answer GetAsync/SetAsync came back with: -1 not asked yet, 0 off, 1 on.
    //
    // Reading the real state means starting schtasks.exe, which is far too slow to do while a menu
    // is opening — but a tick box has to be right the instant it's drawn, and drawing it unticked
    // "for now" is a wrong answer, not a pending one. So the state is cached here, on the singleton
    // both surfaces share: toggling from the settings page updates it, and the tray's next paint
    // sees it without asking Windows anything.
    private volatile int _cached = -1;

    public bool? LastKnown => _cached < 0 ? null : _cached == 1;

    public async Task<Status> GetAsync()
    {
        var status = await ReadAsync();
        _cached = status.Enabled ? 1 : 0;
        return status;
    }

    private static async Task<Status> ReadAsync()
    {
        if (await TaskExistsAsync()) return new Status(true, Mechanism.ScheduledTask);
        if (RunValueExists()) return new Status(true, Mechanism.RunKey);
        return new Status(false, Mechanism.None);
    }

    // Throws with a message meant for a human — both callers put it straight in front of one.
    public async Task<Status> SetAsync(bool enabled)
    {
        if (enabled) await EnableAsync();
        else await DisableAsync();

        var status = await GetAsync();
        log.LogInformation("Autostart is now {State} ({Via})", status.Enabled ? "on" : "off", status.Via);
        return status;
    }

    // ── On ───────────────────────────────────────────────────────────────────────
    private async Task EnableAsync()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not work out where this program lives on disk.");

        try
        {
            await RegisterTaskAsync(exe);
            // Never leave both in place: two triggers means two processes at logon, and the loser
            // exits on the port mutex — noisy, and it looks like a crash in the log.
            RemoveRunValue();
            return;
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not register the logon task ({Reason}). Falling back to a Startup registry entry.",
                ex.Message);
        }

        // Locked-down machines refuse task registration outright. The Run key needs nothing beyond
        // write access to the user's own hive, so it works where the task doesn't. It starts the
        // app a little later in logon and doesn't restart it on failure, which is why it's second.
        SetRunValue(exe);
    }

    // ── Off ──────────────────────────────────────────────────────────────────────
    private async Task DisableAsync()
    {
        var problems = new List<string>();

        try
        {
            if (await TaskExistsAsync())
            {
                var (code, output) = await RunSchtasksAsync(["/Delete", "/TN", TaskName, "/F"]);
                if (code != 0) problems.Add($"could not remove the scheduled task ({Brief(output)})");
            }
        }
        catch (Exception ex)
        {
            problems.Add(ex.Message);
        }

        try
        {
            RemoveRunValue();
        }
        catch (Exception ex)
        {
            problems.Add(ex.Message);
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("Could not turn off automatic start: " + string.Join("; ", problems));
    }

    // ── Scheduled Task ───────────────────────────────────────────────────────────
    private static async Task<bool> TaskExistsAsync()
    {
        // Exit code only. /Query prints the task in the machine's language, and every other way of
        // reading its state means matching against words that change per install.
        var (code, _) = await RunSchtasksAsync(["/Query", "/TN", TaskName]);
        return code == 0;
    }

    private async Task RegisterTaskAsync(string exe)
    {
        // schtasks' own /Create switches can't express half of what this task needs — in particular
        // there's no flag for Hidden or for an unlimited run time, and the default it picks instead
        // (72 hours) would quietly kill the server on the third day. So: XML.
        var xml = TaskXml(exe, $@"{Environment.UserDomainName}\{Environment.UserName}");
        var path = Path.Combine(Path.GetTempPath(), $"odad-task-{Guid.NewGuid():N}.xml");

        try
        {
            // UTF-16 with a BOM, matching the declared encoding. schtasks rejects the file as
            // malformed if those two disagree.
            await File.WriteAllTextAsync(path, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var (code, output) = await RunSchtasksAsync(["/Create", "/TN", TaskName, "/XML", path, "/F"]);
            if (code != 0) throw new InvalidOperationException(Brief(output));
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp file; the OS will get it eventually */ }
        }
    }

    // Element order follows what Task Scheduler itself exports, which is the order its schema
    // expects. Out-of-order children come back as "the task XML is malformed" with no clue which.
    private static string TaskXml(string exe, string user)
    {
        var u = SecurityElement.Escape(user);
        var command = SecurityElement.Escape(exe);

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>{u}</Author>
    <Description>OneDriveAsADrive WebDAV bridge (background)</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{u}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{u}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static async Task<(int Code, string Output)> RunSchtasksAsync(string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start schtasks.exe.");

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (proc.ExitCode, message.Trim());
    }

    // schtasks answers a failure with a banner and a blank line before the actual complaint.
    // Only the complaint is worth showing.
    private static string Brief(string output)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "schtasks refused the request" : line;
    }

    // ── Startup registry entry (fallback) ────────────────────────────────────────
    private static bool RunValueExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(TaskName) != null;
    }

    private static void SetRunValue(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("Could not open your Startup registry key.");
        // Quoted: on any normal install the path contains a space, and an unquoted one sends
        // Windows looking for C:\Program.exe.
        key.SetValue(TaskName, $"\"{exe}\"", RegistryValueKind.String);
    }

    private static void RemoveRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(TaskName) != null) key.DeleteValue(TaskName, throwOnMissingValue: false);
    }
}
