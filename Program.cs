// OneDriveAsADrive
// Mounts OneDrive AND SharePoint document libraries as local WebDAV drive letters.
// No app registration. No MFA circus. No WebDAV-over-the-internet nonsense.
// Just your files, right here, like God and Peter Griffin intended.

using System.Runtime.InteropServices;
using OneDriveAsADrive.Auth;
using OneDriveAsADrive.Config;
using OneDriveAsADrive.Graph;
using OneDriveAsADrive.Logging;
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
    var loginTokens = new TokenManager(loginFactory.CreateLogger<TokenManager>(), config);
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

if (showConsole) NativeConsole.Ensure();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache(); // so Explorer's clingy re-probing hits RAM, not Redmond
builder.Services.AddSingleton(config);              // the mounts we're serving
builder.Services.AddSingleton<ServerSecret>();      // per-install Basic-auth password
builder.Services.AddSingleton<TokenManager>();
builder.Services.AddSingleton<DriveResolver>();     // mount -> Graph driveId
builder.Services.AddSingleton<OneDriveProvider>();

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

// Warm up auth on startup. Better to get the crying out of the way now
// than mid-request when Windows File Explorer is waiting.
var tokenManager = app.Services.GetRequiredService<TokenManager>();
try
{
    await tokenManager.GetAccessTokenAsync();
    app.Logger.LogInformation("Auth OK — Holy crap, we're in! OneDrive is ready.");
}
catch (Exception ex)
{
    // Something went wrong. This is worse than the time Peter got his arm stuck in the vending machine.
    // Make sure an account is signed in to Windows (Settings → Accounts → Work or school / Email & accounts).
    app.Logger.LogError(ex, "Auth failed — is an account signed in? Deucedly inconvenient.");
    return;
}

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

await app.RunAsync();

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
