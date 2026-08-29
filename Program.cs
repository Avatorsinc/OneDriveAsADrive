// OneDriveAsADrive
// Mounts OneDrive AND SharePoint document libraries as local WebDAV drive letters.
// No app registration. No MFA circus. No WebDAV-over-the-internet nonsense.
// Just your files, right here, like God and Peter Griffin intended.

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OneDriveAsADrive.Auth;
using OneDriveAsADrive.Config;
using OneDriveAsADrive.Graph;
using OneDriveAsADrive.Logging;
using OneDriveAsADrive.Settings;
using OneDriveAsADrive.Startup;
using OneDriveAsADrive.Tray;
using OneDriveAsADrive.WebDav;

// ── Background vs. debug ──────────────────────────────────────────────────────
// Built as a WinExe, so normally there's NO console window — it runs invisibly in the
// background and the user never notices (that's the point). An admin who wants to watch
// it work passes --console to pop a real console with live logs; --debug does that AND
// turns the log level down to Debug (per-request chatter). Like the difference between
// Stewie's public face and his evil lab.
var debugLevel = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));
var showConsole = debugLevel || args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase));
var minLevel = debugLevel ? LogLevel.Debug : LogLevel.Information;

// The tray icon is the only entry point a normal user can find, so it's on by default. --no-tray
// is for deployments where nobody is looking at that desktop anyway: a kiosk, a session with the
// notification area hidden by policy, or an admin who wants the drives and none of the UI.
var noTray = args.Any(a => a.Equals("--no-tray", StringComparison.OrdinalIgnoreCase));

var config = MountConfig.Load();

// ── First-run sign-in ─────────────────────────────────────────────────────────
// --login just does the interactive WAM sign-in and exits — no web server. The
// installer runs this ONCE, visibly, so the account picker has a real window to show
// against (a hidden background start can't prompt, and consent silently faceplants -
// which is exactly the bug that shipped nothing). After this, WAM has cached the token
// and every hidden background start authenticates silently. Sign in once, giggity forever.
if (args.Any(a => a.Equals("--login", StringComparison.OrdinalIgnoreCase)))
{
    NativeConsole.Ensure();
    Console.WriteLine("OneDriveAsADrive - signing you in...");
    using var loginFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
    // This is the ONE place a real window exists, so this is the ONE place interactive is allowed.
    var loginTokens = new TokenManager(loginFactory.CreateLogger<TokenManager>(), config) { AllowInteractive = true };
    try
    {
        await loginTokens.GetAccessTokenAsync();
        Console.WriteLine("Signed in. You can close this window - your drives are being set up.");
        return;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Sign-in failed: " + ex.Message);
        Environment.Exit(1);
    }
}

// ── Settings UI ───────────────────────────────────────────────────────────────
// --settings opens the local settings page in the default browser. It does NOT start a server of
// its own — the background instance is already holding the port — so this has to run before the
// single-instance mutex below, or we'd bow out and never open anything.
//
// Auth without IPC: both processes can read the per-user .secret file, so both can derive the
// same short-lived bootstrap token. We put the DERIVED token in the URL, never the raw secret —
// a token is worthless a minute later, a leaked secret is your drive.
if (args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
{
    var uiConfig = MountConfig.Load();

    if (uiConfig.SettingsUiDisabled)
    {
        NativeConsole.Ensure();
        Console.Error.WriteLine("The settings page is disabled by your organization's policy.");
        Environment.Exit(1);
    }

    // Someone clicking a Start Menu shortcut shouldn't have to know whether the background task is
    // running. If nothing's listening, start it and wait for the bind.
    if (!await ServerProbe.IsListeningAsync(uiConfig.Port))
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true }); }
        catch { /* fall through — the browser will show the connection error itself */ }

        for (var i = 0; i < 40 && !await ServerProbe.IsListeningAsync(uiConfig.Port); i++)
            await Task.Delay(200);
    }

    var bootstrap = new SettingsAuth(new ServerSecret()).MintBootstrapToken();
    // 127.0.0.1 rather than the name, so the browser doesn't spend time resolving localhost to
    // ::1 first. Both are accepted by the loopback guard.
    var settingsUrl = $"http://127.0.0.1:{uiConfig.Port}/-/settings?t={Uri.EscapeDataString(bootstrap)}";
    Process.Start(new ProcessStartInfo(settingsUrl) { UseShellExecute = true });
    return;
}

if (showConsole) NativeConsole.Ensure();

// ── Single instance ───────────────────────────────────────────────────────────
// Only ONE server can hold 127.0.0.1:PORT. The installer starts it AND the logon task
// starts it - two copies racing for the port is how the moderator's manual validation
// crashed with an unhandled "address already in use". So: grab a named mutex keyed to the
// port; if another copy already owns it, our job's being done - bow out quietly with exit 0
// instead of an APPCRASH. A gunfight over one parking spot helps nobody. Roadhouse.
using var instanceLock = new System.Threading.Mutex(true, $"Local\\OneDriveAsADrive_{config.Port}", out var isPrimary);
if (!isPrimary)
{
    new FileLoggerProvider(minLevel: minLevel).CreateLogger("Startup")
        .LogInformation("Another instance already owns port {Port}. Nothing to do; exiting.", config.Port);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache(); // so Explorer's clingy re-probing hits RAM, not Redmond
builder.Services.AddSingleton(config);              // the mounts we're serving
builder.Services.AddSingleton<ServerSecret>();      // per-install Basic-auth password
builder.Services.AddSingleton<TokenManager>();
builder.Services.AddSingleton<DriveResolver>();     // mount -> Graph driveId
builder.Services.AddSingleton<OneDriveProvider>();
builder.Services.AddSingleton<SettingsAuth>();      // sessions + CSRF for the settings page
builder.Services.AddSingleton<DriveMapper>();       // applies mount changes with net use
builder.Services.AddSingleton<Autostart>();         // the "start when I sign in" switch
builder.Services.AddSingleton<TrayIcon>();          // the notification-area entry point

// Always log to a file (so background runs are debuggable); only add the console logger
// when a window actually exists to print to.
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(minLevel: minLevel));
if (showConsole) builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(minLevel);

// Port comes from config unless the user pins it with --urls. Like telling Peter where
// the fridge is — he'll always find it.
if (!args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)))
    builder.WebHost.UseUrls($"http://localhost:{config.Port}");

var app = builder.Build();

app.Logger.LogInformation("Config: {Source} — serving {Count} mount(s)",
    config.SourcePath ?? "defaults (single OneDrive on Z:)", config.Mounts.Count);

// MountConfig.Load() runs before the host exists, so anything it wanted to complain about
// (malformed JSON, a bad drive letter, an unreadable policy key) has been queued until now.
foreach (var warning in config.LoadWarnings)
    app.Logger.LogWarning("[config] {Warning}", warning);

if (config.IsManaged)
    app.Logger.LogInformation(
        "Managed by policy — port locked: {Port}, account locked: {Account}, drives locked: {Mounts}, settings UI disabled: {Ui}",
        config.PortLocked, config.AccountLocked, config.MountsLocked, config.SettingsUiDisabled);

// Warm up auth on startup — but SILENT ONLY (AllowInteractive stays false here). If there's a
// cached token, great, we're warm. If not, we DON'T pop a window and we DON'T exit — we start the
// server anyway and let the user sign in via --login. Exiting-on-no-token is what made a silent
// install look like a failed install; a token-less server that's up and waiting is the right call.
var tokenManager = app.Services.GetRequiredService<TokenManager>();
try
{
    await tokenManager.GetAccessTokenAsync();
    app.Logger.LogInformation("Auth OK — Holy crap, we're in! OneDrive is ready.");
}
catch (Exception ex)
{
    // No cached token yet. Not fatal: serve anyway, the drive just answers 401-ish until sign-in.
    // Peter waits by the fridge; the server waits for --login.
    app.Logger.LogWarning("Not signed in yet ({Reason}). Run 'OneDriveAsADrive.exe --login'. Serving token-less for now.", ex.Message);
}

// Settings first — it claims only /-/... and passes everything else straight through, so the
// WebDAV hot path picks up nothing but one string comparison.
app.UseMiddleware<SettingsMiddleware>();
app.UseMiddleware<WebDavMiddleware>();

// Log how to mount every configured drive. Once per startup.
// Like Quagmire announcing his arrival. Giggity.
//
// The persistent app.log is a support artifact people paste into issues, so we NEVER write
// the real secret there - it goes to the file redacted as <secret>. The genuine net use
// line (secret and all) prints to the CONSOLE only, which exists solely when a human ran
// --console/--debug on purpose. Loose logs sink ships; the secret lives in .secret anyway.
var secret = app.Services.GetRequiredService<ServerSecret>();
var listenUrl = (builder.WebHost.GetSetting("urls") ?? $"http://localhost:{config.Port}").TrimEnd('/');
app.Logger.LogInformation("WebDAV listening on {Url} — everybody, everybody, everybody!", listenUrl);
app.Logger.LogInformation("Secret stored at {Path}", secret.FilePath);
foreach (var m in config.Mounts)
{
    var mountUrl = $"{listenUrl}{m.Prefix}/";
    var redacted = $"net use {m.Letter}: {mountUrl} /user:{secret.Username} <secret> /persistent:yes";
    app.Logger.LogInformation("Mount {Letter}: ({Name}) -> {Url}  |  {NetUse}",
        m.Letter, m.DisplayName, mountUrl, redacted);
    // Real, copy-pasteable line only to the on-purpose console - never the file log.
    if (showConsole)
        Console.WriteLine($"  {m.Letter}: -> net use {m.Letter}: {mountUrl} /user:{secret.Username} {secret.Value} /persistent:yes");
}
app.Logger.LogInformation("Unmount with: net use <Letter>: /delete");

// Warm each mounted drive's root in the background.
//
// Measured against a real OneDrive, one Graph call costs ~800ms whatever it returns, and the
// first open of a drive pays two of them (resolve the drive, then list the root) - about two
// seconds of dead air the very first time someone clicks the drive letter. Nothing about that
// call gets cheaper, so we make it EARLY instead: the server has nothing else to do at startup,
// and the user hasn't clicked anything yet. Listing the root also triggers the provider's own
// subfolder prefetch, so by the time the drive is opened the first level down is warm too.
//
// Fire-and-forget on purpose: it must never delay the listener coming up, and if it fails
// (no token yet, offline) the drive simply behaves as it did before - the first click pays.
_ = Task.Run(async () =>
{
    var resolver = app.Services.GetRequiredService<DriveResolver>();
    var provider = app.Services.GetRequiredService<OneDriveProvider>();
    foreach (var m in config.Mounts)
    {
        try
        {
            var driveId = await resolver.ResolveDriveIdAsync(m);
            await provider.GetItemAsync(driveId, "/");
            app.Logger.LogDebug("Prewarmed {Letter}: root", m.Letter);
        }
        catch (Exception ex)
        {
            app.Logger.LogDebug("Could not prewarm {Letter}: ({Reason})", m.Letter, ex.Message);
        }
    }
});

// ── Bring the drive letters up ────────────────────────────────────────────────
// Starting the app is what puts the drive letters back. It has to be, because turning it off from
// the tray deliberately takes them down: without this, "off, then on again" would leave someone
// with no drives and nothing on screen explaining why. The installer and "Re-map drives now" still
// map too - they just aren't the only things that ever do.
//
// Idempotent either way: ApplyAsync deletes and re-creates each configured letter, which also
// repairs one that Windows restored pointing at a stale port. Registered on ApplicationStarted
// rather than run inline, because `net use` reaches us over the loopback port — Kestrel has to be
// listening before the first one goes out.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        var result = await app.Services.GetRequiredService<DriveMapper>().ApplyAsync(config);
        foreach (var error in result.Errors)
            app.Logger.LogWarning("[startup map] {Error}", error);
    }
    catch (Exception ex)
    {
        // A letter we couldn't map is a bad day, not a reason to take the server down with it —
        // the WebDAV endpoint still works, and the tray can retry.
        app.Logger.LogWarning(ex, "Could not map drives at startup");
    }
}));

// ── The visible bit ───────────────────────────────────────────────────────────
// Up to here the app is completely invisible: no console, no window, and a settings page you can
// only reach by knowing to run `OneDriveAsADrive.exe --settings` from somewhere. That's fine for
// an admin and useless for a person, who has no way to tell it's running, let alone configure it.
// The notification area is where Windows users already look for background programs, so that's
// where the door goes. It adds no capability the command line didn't already have.
if (!noTray)
    app.Services.GetRequiredService<TrayIcon>().Start();

// Safety net: the mutex above catches OUR own double-start, but something ELSE could be
// squatting on the port. If the bind fails, don't crash - log it and leave with exit 0.
// A dead process is worse than a polite one that noticed the seat was taken.
try
{
    await app.RunAsync();
}
catch (IOException ex) when (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    app.Logger.LogWarning("Port {Port} is already in use — another server is running. Exiting cleanly.", config.Port);
}

// Is a server already bound to this port? Used by --settings to decide whether it needs to start
// the background instance before opening the browser at it.
internal static class ServerProbe
{
    public static async Task<bool> IsListeningAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch
        {
            // Refused, timed out, or cancelled — either way, nothing's answering yet.
            return false;
        }
    }
}

// Pops a real console window for a WinExe process when --console is passed, so admins can
// watch the logs live. Without this, a WinExe has nowhere to print.
internal static class NativeConsole
{
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    public static void Ensure()
    {
        // Reuse a parent console if we were launched from one; otherwise spawn a fresh window.
        if (!AttachConsole(-1)) AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}
