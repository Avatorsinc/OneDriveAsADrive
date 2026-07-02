// OneDriveAsADrive
// Mounts OneDrive for Business as a local WebDAV drive.
// No app registration. No MFA circus. No WebDAV-over-the-internet nonsense.
// Just your files, right here, like God and Peter Griffin intended.

using OneDriveAsADrive.Auth;
using OneDriveAsADrive.Graph;
using OneDriveAsADrive.WebDav;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache(); // so Explorer's clingy re-probing hits RAM, not Redmond
builder.Services.AddSingleton<ServerSecret>(); // per-install Basic-auth password
builder.Services.AddSingleton<TokenManager>();
builder.Services.AddSingleton<OneDriveProvider>();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Default to localhost:8080. Override with --urls http://localhost:PORT
// or ASPNETCORE_URLS env var. Like telling Peter where the fridge is — he'll always find it.
builder.WebHost.UseUrls("http://localhost:8080");

var app = builder.Build();

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
    // Make sure a work/school account is signed in to Windows (Settings → Accounts → Work or school).
    app.Logger.LogError(ex, "Auth failed — is a work account signed in? Deucedly inconvenient.");
    return;
}

app.UseMiddleware<WebDavMiddleware>();

// Log how to mount the drive, secret and all. Once per startup.
// Like Quagmire announcing his arrival. Giggity.
var secret = app.Services.GetRequiredService<ServerSecret>();
var url = builder.WebHost.GetSetting("urls") ?? "http://localhost:8080";
var mountUrl = url.TrimEnd('/') + "/";
app.Logger.LogInformation("WebDAV listening on {Url} — everybody, everybody, everybody!", url);
app.Logger.LogInformation("Secret stored at {Path}", secret.FilePath);
app.Logger.LogInformation("Mount with:  net use Z: {MountUrl} /user:{User} {Secret} /persistent:yes",
    mountUrl, secret.Username, secret.Value);
app.Logger.LogInformation("Unmount with: net use Z: /delete");

await app.RunAsync();
