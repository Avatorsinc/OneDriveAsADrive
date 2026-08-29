using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Forms;
using OneDriveAsADrive.Auth;
using OneDriveAsADrive.Config;
using OneDriveAsADrive.Logging;
using OneDriveAsADrive.Settings;
using OneDriveAsADrive.Startup;

namespace OneDriveAsADrive.Tray;

// The visible half of the app.
//
// Everything else here is deliberately invisible: a WinExe with no console, no window, and a
// settings page you reach by knowing to run `OneDriveAsADrive.exe --settings`. That is a fine
// design for an admin deployment and a terrible one for a person, who has no way to find out the
// program is even running, let alone that it has settings. The notification area is where Windows
// users already look for exactly this class of thing, so that's where the entry point goes.
//
// Everything the menu does already existed and was already reachable some other way. This adds no
// new capability and no new privilege - it just stops requiring a command line to use any of it.
[SupportedOSPlatform("windows")]
public sealed class TrayIcon(
    MountConfig config,
    SettingsAuth auth,
    DriveMapper mapper,
    TokenManager tokens,
    Autostart autostart,
    IHostApplicationLifetime lifetime,
    ILogger<TrayIcon> log)
{
    private NotifyIcon? _icon;
    private ToolStripMenuItem? _accountItem;
    private ToolStripMenuItem? _autostartItem;
    private volatile string _account = "Checking sign-in...";

    // NotifyIcon is a Component, not a Control, so it has no BeginInvoke of its own - and its
    // methods still have to run on the thread that owns the pump. This is how anything off-thread
    // (a finished re-map, the host shutting down) gets back onto it.
    private SynchronizationContext? _sync;

    // WinForms needs a thread that is STA and owns a message pump, and Kestrel owns the main
    // thread. So the tray gets its own: STA, background (so a stuck menu can never hold the
    // process open), running nothing but Application.Run.
    public void Start()
    {
        var thread = new Thread(Pump)
        {
            Name = "TrayIcon",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // The host shutting down for any other reason (Ctrl+C on --console, a service stop, the
        // port already being taken) must also take the icon with it, or Windows leaves a ghost
        // that only disappears when someone happens to mouse over it.
        lifetime.ApplicationStopping.Register(Stop);
    }

    private void Pump()
    {
        try
        {
            Application.EnableVisualStyles();
            // Menus otherwise render at 96 DPI and go blurry on the scaled displays most laptops
            // ship with. Has to happen before the first control exists, which is here.
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { /* pre-Win10, not worth failing over */ }

            // Set explicitly rather than waiting for the first control to install one, so _sync is
            // never null in the window between here and Application.Run.
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _sync = SynchronizationContext.Current;

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = Tooltip(),
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };

            // Double-click is the reflex for "show me the thing", so it opens Settings - the same
            // action as the menu item, not some secret fourth behaviour.
            _icon.DoubleClick += (_, _) => OpenSettings();

            _ = Task.Run(RefreshAccountAsync);
            _ = Task.Run(RefreshAutostartAsync);
            Application.Run();
        }
        catch (Exception ex)
        {
            // No tray is a degraded app, not a broken one: the drives still mount and --settings
            // still works. Never take the server down over the decoration.
            log.LogWarning("Tray icon unavailable ({Reason}). The drives are unaffected.", ex.Message);
        }
        finally
        {
            _icon?.Dispose();
            _icon = null;
        }
    }

    private void Stop()
    {
        var icon = _icon;
        if (icon == null) return;

        try
        {
            // Hide before ExitThread: disposing alone sometimes leaves the icon painted until the
            // shell next repaints that area.
            _sync?.Post(_ =>
            {
                icon.Visible = false;
                Application.ExitThread();
            }, null);
        }
        catch
        {
            // The pump may already be gone, in which case there is nothing left to tidy.
        }
    }

    // ── Menu ─────────────────────────────────────────────────────────────────────
    private ContextMenuStrip BuildMenu()
    {
        // ShowCheckMargin, or the autostart tick has nowhere to draw: with both margins off a
        // ToolStripMenuItem happily reports Checked = true and renders identically to an unchecked
        // one. ShowImageMargin stays off - none of these items has an icon, and the two margins
        // together indent the text twice as far as anything else in the notification area does.
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };

        var header = new ToolStripMenuItem($"OneDriveAsADrive {Version()}") { Enabled = false };
        _accountItem = new ToolStripMenuItem(_account) { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(_accountItem);

        if (config.IsManaged)
            menu.Items.Add(new ToolStripMenuItem("Managed by your organization") { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());

        // One row per configured drive. This doubles as the answer to "is it even working?" -
        // if a letter you expected isn't listed, the config is what's wrong, not the drive.
        foreach (var mount in config.Mounts)
        {
            var letter = mount.Letter.ToUpperInvariant();
            var item = new ToolStripMenuItem($"Open {letter}: ({mount.DisplayName})");
            item.Click += (_, _) => OpenDrive(letter);
            menu.Items.Add(item);
        }

        if (config.Mounts.Count > 0) menu.Items.Add(new ToolStripSeparator());

        // Hidden, not disabled, when policy kills the settings UI: a greyed-out row invites a
        // support ticket, and the admin's intent was for it not to be a thing.
        if (!config.SettingsUiDisabled)
        {
            var settings = new ToolStripMenuItem("Settings...");
            settings.Click += (_, _) => OpenSettings();
            menu.Items.Add(settings);
            menu.Items.Add(new ToolStripSeparator());
        }

        var signIn = new ToolStripMenuItem("Sign in / switch account");
        signIn.Click += (_, _) => SignIn();
        menu.Items.Add(signIn);

        var remap = new ToolStripMenuItem("Re-map drives now");
        // Task.Run, NOT a bare fire-and-forget call. A click handler starts on the tray thread, and
        // this thread has a WinForms SynchronizationContext, so every await inside ApplyAsync would
        // otherwise resume right back here - including the `net use` calls. Against a server that
        // has stopped, those block on the WebDAV redirector for tens of seconds, and a wedged tray
        // thread means the shell stalls talking to our notification icon. Nothing that can touch
        // the network gets to run on this thread.
        remap.Click += (_, _) => _ = Task.Run(RemapAsync);
        menu.Items.Add(remap);

        var logItem = new ToolStripMenuItem("View log");
        logItem.Click += (_, _) => OpenLog();
        menu.Items.Add(logItem);

        menu.Items.Add(new ToolStripSeparator());

        // The "keep it on" half of on/off. A tick box rather than a Start/Stop pair, because
        // there's nothing to start - you're reading this menu, so it's already running. The only
        // question anyone actually has is whether it comes back tomorrow.
        //
        // CheckOnClick is off deliberately: the tick has to follow what the machine ended up
        // doing, not what was clicked. Registering the task can be refused, and a box that ticks
        // itself regardless would be a lie.
        _autostartItem = new ToolStripMenuItem("Start automatically when I sign in") { Checked = AutostartOn };
        _autostartItem.Click += (_, _) => _ = Task.Run(ToggleAutostartAsync);   // spawns schtasks - off this thread
        menu.Items.Add(_autostartItem);

        var exit = new ToolStripMenuItem("Turn off (disconnect drives and quit)");
        exit.Click += (_, _) => Exit();
        menu.Items.Add(exit);

        // Both of these can change out from under us - a --login in another process, or someone
        // deleting the task in Task Scheduler - so re-read them each time the menu is summoned.
        // Fire-and-forget: they update the rows for the next open rather than stalling this one.
        menu.Opening += (_, _) =>
        {
            _accountItem!.Text = _account;
            _autostartItem!.Checked = AutostartOn;
            _ = Task.Run(RefreshAccountAsync);     // token broker call - same rule, off this thread
            _ = Task.Run(RefreshAutostartAsync);
        };

        return menu;
    }

    // ── Actions ──────────────────────────────────────────────────────────────────
    private void OpenDrive(string letter)
    {
        // Explorer's own error for an unmapped letter is "Windows cannot access Z:\", which sends
        // people looking for a network problem they don't have. Say the actual thing instead.
        if (!Directory.Exists($"{letter}:\\"))
        {
            Notify($"{letter}: is not mapped yet",
                "Use \"Re-map drives now\", or sign in first if you haven't.");
            return;
        }

        Launch(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"{letter}:\\" }, UseShellExecute = true },
            "open " + letter + ":");
    }

    private void OpenSettings()
    {
        if (config.SettingsUiDisabled)
        {
            Notify("Settings are disabled", "Your organization's policy turned off the settings page.");
            return;
        }

        // We ARE the server, so unlike `--settings` there's no port probe and no second process to
        // start - just mint the same short-lived bootstrap token in-process and hand it to the
        // browser. 127.0.0.1 rather than the name, to skip a ::1 resolution detour.
        var url = $"http://127.0.0.1:{config.Port}/-/settings?t={Uri.EscapeDataString(auth.MintBootstrapToken())}";
        Launch(new ProcessStartInfo(url) { UseShellExecute = true }, "open settings");
    }

    private void SignIn()
    {
        // Interactive sign-in needs a real window to show the account picker against, and this
        // process deliberately has none. --login is the mode that owns that, so defer to it.
        Launch(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--login" }, UseShellExecute = true },
            "start sign-in");
    }

    private async Task RemapAsync()
    {
        try
        {
            var result = await mapper.ApplyAsync(config);

            var parts = new List<string>();
            if (result.Mapped.Count > 0) parts.Add("Mapped " + string.Join(", ", result.Mapped.Select(l => l + ":")));
            if (result.Unmapped.Count > 0) parts.Add("Removed " + string.Join(", ", result.Unmapped.Select(l => l + ":")));
            if (result.Errors.Count > 0) parts.AddRange(result.Errors);

            Notify(result.Errors.Count > 0 ? "Re-map finished with problems" : "Drives re-mapped",
                parts.Count > 0 ? string.Join(Environment.NewLine, parts) : "Nothing needed changing.",
                result.Errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);

            await RefreshAccountAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Re-map from the tray failed");
            Notify("Re-map failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OpenLog()
    {
        var path = FileLoggerProvider.DefaultLogPath;
        if (!File.Exists(path))
        {
            Notify("No log yet", "Nothing has been written to " + path);
            return;
        }

        // notepad.exe explicitly rather than the .log file association, which on a stock machine
        // is often nothing at all - and "no application is associated" is a worse answer than
        // the file.
        Launch(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = true },
            "open the log");
    }

    private void Exit()
    {
        // The letters come down with the server, always, and this says so up front rather than
        // offering it as a choice. Leaving them behind is the trap that makes this look broken:
        // they stay listed in Explorer looking perfectly normal, because they're mapped
        // /persistent:yes, and the first click blocks in the WebDAV redirector until it times out
        // - which reads as the whole shell freezing, not as this program having been closed.
        var comesBack = AutostartOn
            ? "It starts again, and re-connects them, the next time you sign in to Windows."
            : "Automatic start is off, so it won't come back on its own - open OneDriveAsADrive "
              + "from the Start Menu when you want your drives again.";

        var answer = MessageBox.Show(
            "Turn OneDriveAsADrive off?" + Environment.NewLine + Environment.NewLine +
            "Your drive letters will be disconnected and the program will close." + Environment.NewLine +
            Environment.NewLine + comesBack,
            "OneDriveAsADrive",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        log.LogInformation("Turn-off requested from the tray menu.");
        // Hide the icon now - the disconnect takes a second or two and a menu that still responds
        // during it invites a second click.
        if (_icon != null) _icon.Visible = false;
        _ = Task.Run(ShutdownAsync);
    }

    private async Task ShutdownAsync()
    {
        try
        {
            // Order is the whole point: unmap while we're still answering. Doing it the other way
            // round means `net use /delete` is itself talking to a dead port, and that stalls too.
            var result = await mapper.DisconnectAsync(config);
            if (result.Unmapped.Count > 0)
                log.LogInformation("Disconnected {Letters} before shutting down",
                    string.Join(", ", result.Unmapped.Select(l => l + ":")));
            foreach (var error in result.Errors)
                log.LogWarning("[shutdown] {Error}", error);
        }
        catch (Exception ex)
        {
            // Still stop. A drive we failed to disconnect is a worse outcome than a process that
            // refuses to close, but only slightly - and staying up doesn't fix it either.
            log.LogWarning(ex, "Could not disconnect drives on the way out");
        }

        // Stop the host, not this thread: ApplicationStopping (registered in Start) brings the
        // pump down, so shutdown runs the same path whatever triggered it.
        lifetime.StopApplication();
    }

    // Straight off the shared singleton rather than a copy of our own, so a toggle from the
    // settings page is already reflected the next time this menu paints.
    private bool AutostartOn => autostart.LastKnown ?? false;

    private async Task ToggleAutostartAsync()
    {
        var want = !AutostartOn;
        try
        {
            var status = await autostart.SetAsync(want);

            Notify(status.Enabled ? "Automatic start is on" : "Automatic start is off",
                status.Enabled
                    ? "OneDriveAsADrive will start and connect your drives every time you sign in. "
                      + status.Description
                    : "OneDriveAsADrive will no longer start on its own. Your drives stay connected "
                      + "until you turn it off or sign out.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not change autostart from the tray");
            Notify("Could not change that setting", ex.Message, ToolTipIcon.Error);
            await RefreshAutostartAsync();   // put the tick back where reality left it
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────
    private async Task RefreshAutostartAsync()
    {
        try
        {
            await autostart.GetAsync();   // its own cache is what the menu reads
        }
        catch (Exception ex)
        {
            // Unreadable is not the same as off, but an unticked box is the safer thing to show:
            // it invites a click that re-registers the task, which is also the repair.
            log.LogDebug(ex, "Could not read the autostart state");
        }
    }

    private async Task RefreshAccountAsync()
    {
        try
        {
            var account = await tokens.GetSignedInAccountAsync();
            _account = string.IsNullOrWhiteSpace(account) ? "Not signed in" : "Signed in as " + account;
        }
        catch
        {
            _account = "Not signed in";
        }
    }

    private void Launch(ProcessStartInfo psi, string what)
    {
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Tray could not {What}", what);
            Notify("Could not " + what, ex.Message, ToolTipIcon.Error);
        }
    }

    private void Notify(string title, string body, ToolTipIcon severity = ToolTipIcon.Info)
    {
        var icon = _icon;
        if (icon == null) return;

        try
        {
            // ShowBalloonTip has to be called on the pump's thread, and RemapAsync resumes on a
            // thread-pool one. Post rather than Send, so a click handler never waits on the pump
            // it is itself running inside.
            _sync?.Post(_ => Balloon(icon, title, body, severity), null);
        }
        catch
        {
            // Notifications can be off at the OS level. The action itself already happened.
        }
    }

    private static void Balloon(NotifyIcon icon, string title, string body, ToolTipIcon severity)
    {
        icon.BalloonTipTitle = title;
        // Windows truncates long balloon text anyway; the full detail is in the log.
        icon.BalloonTipText = body.Length > 240 ? body[..240] + "..." : body;
        icon.BalloonTipIcon = severity;
        icon.ShowBalloonTip(5000);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OneDriveAsADrive.ico");
            if (stream != null)
            {
                // Pick the frame that matches the tray's actual slot size rather than letting the
                // shell squash the 256px one. On a 150% display that slot is 24px, not 16.
                using var full = new System.Drawing.Icon(stream);
                return new System.Drawing.Icon(full, SystemInformation.SmallIconSize);
            }
        }
        catch
        {
            // Fall through to the stock icon - a generic tray entry beats no tray entry.
        }

        return SystemIcons.Application;
    }

    // 63 characters is the hard limit on a tray tooltip, and Windows silently rejects anything
    // longer, leaving no tooltip at all.
    private string Tooltip()
    {
        var letters = string.Join(", ", config.Mounts.Select(m => m.Letter.ToUpperInvariant() + ":"));
        var text = string.IsNullOrEmpty(letters) ? "OneDriveAsADrive" : $"OneDriveAsADrive - {letters}";
        return text.Length > 63 ? text[..60] + "..." : text;
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "";
}
