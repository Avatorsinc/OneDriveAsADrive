using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneDriveAsADrive.Auth;
using OneDriveAsADrive.Config;
using OneDriveAsADrive.Startup;
using OneDriveAsADrive.WebDav;

namespace OneDriveAsADrive.Settings;

// The settings surface: a small local web app for the person actually using the drives, so
// changing a port or adding a SharePoint library doesn't mean hand-editing JSON and restarting.
//
// It lives under /- , which can never collide with a mount: Sanitize() guarantees every mount
// prefix is exactly one A–Z character, and '-' isn't one.
//
// Runs BEFORE WebDavMiddleware and claims only its own paths; everything else falls through
// untouched, so the WebDAV hot path is unaffected.
[SupportedOSPlatform("windows")]
public sealed class SettingsMiddleware(
    RequestDelegate next,
    MountConfig config,
    SettingsAuth auth,
    TokenManager tokens,
    DriveMapper mapper,
    Autostart autostart,
    ILogger<SettingsMiddleware> log)
{
    public const string BasePath = "/-";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path != BasePath && !path.StartsWith(BasePath + "/", StringComparison.Ordinal))
        {
            await next(ctx);
            return;
        }

        // Admin kill switch. 404 rather than 403 on purpose — on a machine where the org turned
        // this off, we don't advertise that there was ever anything here to find.
        if (config.SettingsUiDisabled)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        if (!LoopbackGuard.RemoteIsLoopback(ctx) || !LoopbackGuard.IsLoopbackHost(ctx.Request.Host.Host))
        {
            log.LogWarning("Rejected non-loopback settings request from {Ip}", ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = 403;
            return;
        }

        // Browser-provenance gates. A cross-site page trying to reach us fails here before it can
        // reach anything that reads or writes state.
        if (!SettingsAuth.IsAcceptableFetchSite(ctx) || !SettingsAuth.IsAcceptableOrigin(ctx, config.Port))
        {
            log.LogWarning("Rejected cross-origin settings request (origin {Origin})",
                ctx.Request.Headers.Origin.FirstOrDefault() ?? "none");
            ctx.Response.StatusCode = 403;
            return;
        }

        NoStore(ctx);

        switch (path)
        {
            case BasePath + "/settings": await HandlePage(ctx); break;
            case BasePath + "/api/state": await HandleState(ctx); break;
            case BasePath + "/api/settings": await HandleSave(ctx); break;
            case BasePath + "/api/signin": await HandleSignIn(ctx); break;
            case BasePath + "/api/remap": await HandleRemap(ctx); break;
            case BasePath + "/api/autostart": await HandleAutostart(ctx); break;
            default: ctx.Response.StatusCode = 404; break;
        }
    }

    // ── Page ─────────────────────────────────────────────────────────────────────
    private async Task HandlePage(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Get) { ctx.Response.StatusCode = 405; return; }

        // Arriving from `--settings`: trade the one-minute bootstrap token for a session cookie,
        // then redirect to the bare URL so the token never sticks around in history or a
        // shoulder-surfed address bar.
        var token = ctx.Request.Query["t"].FirstOrDefault();
        if (token != null)
        {
            if (!auth.IsValidBootstrapToken(token))
            {
                log.LogWarning("Settings bootstrap token rejected (expired or wrong).");
                ctx.Response.StatusCode = 403;
                await WriteHtml(ctx, SettingsPage.DeniedHtml);
                return;
            }

            var (sid, _) = auth.CreateSession();
            ctx.Response.Cookies.Append(SettingsAuth.CookieName, sid, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = BasePath + "/",
                IsEssential = true
            });
            ctx.Response.Redirect(BasePath + "/settings");
            return;
        }

        if (!auth.IsValidSession(ctx.Request.Cookies[SettingsAuth.CookieName]))
        {
            ctx.Response.StatusCode = 403;
            await WriteHtml(ctx, SettingsPage.DeniedHtml);
            return;
        }

        await WriteHtml(ctx, SettingsPage.Html);
    }

    // ── State ────────────────────────────────────────────────────────────────────
    private async Task HandleState(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Get) { ctx.Response.StatusCode = 405; return; }

        var csrf = auth.CsrfTokenFor(ctx.Request.Cookies[SettingsAuth.CookieName]);
        if (csrf == null) { await WriteExpired(ctx); return; }

        var startup = await autostart.GetAsync();

        var state = new
        {
            version = typeof(SettingsMiddleware).Assembly.GetName().Version?.ToString(3) ?? "dev",
            port = config.Port,
            account = config.Account,
            mounts = config.Mounts,
            locked = new { port = config.PortLocked, account = config.AccountLocked, mounts = config.MountsLocked },
            source = new
            {
                port = config.PortSource.ToString(),
                account = config.AccountSource.ToString(),
                mounts = config.MountsSource.ToString()
            },
            managed = config.IsManaged,
            configSummary = config.SourcePath,
            signedInAs = await tokens.GetSignedInAccountAsync(),
            autostart = Describe(startup),
            warnings = config.LoadWarnings,
            csrfToken = csrf
        };

        await WriteJson(ctx, state);
    }

    // ── Save ─────────────────────────────────────────────────────────────────────
    private sealed class SaveRequest
    {
        public int? Port { get; set; }
        public string? Account { get; set; }
        public List<Mount>? Mounts { get; set; }
    }

    private async Task HandleSave(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Post) { ctx.Response.StatusCode = 405; return; }
        if (!auth.IsValidMutation(ctx)) { await WriteExpired(ctx); return; }

        SaveRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<SaveRequest>(ctx.Request.Body, Json);
        }
        catch (JsonException ex)
        {
            await WriteError(ctx, 400, $"That request body isn't valid JSON: {ex.Message}");
            return;
        }
        if (body == null) { await WriteError(ctx, 400, "Empty request body."); return; }

        // Locked fields: accept an unchanged echo (the UI posts the whole form back), reject an
        // actual attempt to change one. Silently ignoring it would be worse — the user would think
        // it saved.
        if (config.PortLocked && body.Port.HasValue && body.Port != config.Port)
        {
            await WriteError(ctx, 409, "The port is managed by your organization and can't be changed here.");
            return;
        }
        if (config.AccountLocked && Normalize(body.Account) != Normalize(config.Account))
        {
            await WriteError(ctx, 409, "The account is managed by your organization and can't be changed here.");
            return;
        }
        if (config.MountsLocked && body.Mounts != null && !SameMounts(body.Mounts, config.Mounts))
        {
            await WriteError(ctx, 409, "Your drives are managed by your organization and can't be changed here.");
            return;
        }

        var warnings = new List<string>();

        var newPort = config.PortLocked ? config.Port : body.Port ?? config.Port;
        if (newPort is < 1024 or > 65535)
        {
            await WriteError(ctx, 400, "Pick a port between 1024 and 65535. Ports below 1024 need admin rights to bind.");
            return;
        }

        var newAccount = config.AccountLocked ? config.Account : Normalize(body.Account);

        var newMounts = config.MountsLocked
            ? config.Mounts
            : MountConfig.SanitizeMounts(body.Mounts ?? config.Mounts, warnings);

        if (newMounts.Count == 0)
        {
            await WriteError(ctx, 400, "You need at least one drive. Add a OneDrive or SharePoint mount before saving.");
            return;
        }

        // Persist only what the user genuinely owns. If a value still matches what the machine
        // config (or the built-in default) supplies, we deliberately DON'T write it — that keeps
        // the user file minimal and lets a later admin change still flow through for anything the
        // user never touched.
        var persistPort = ShouldPersist(newPort != config.Port, config.PortSource);
        var persistAccount = ShouldPersist(!string.Equals(newAccount, config.Account, StringComparison.OrdinalIgnoreCase), config.AccountSource);
        var persistMounts = ShouldPersist(!SameMounts(newMounts, config.Mounts), config.MountsSource);

        try
        {
            MountConfig.SaveUser(
                persistPort ? newPort : null,
                persistAccount ? newAccount : null,
                persistMounts ? newMounts : null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not write user config");
            await WriteError(ctx, 500, $"Could not save your settings: {ex.Message}");
            return;
        }

        // What needs a restart, and why:
        //  • port    — Kestrel bound it at startup and can't move while running.
        //  • account — TokenManager captured it when it was constructed.
        //  • new SharePoint — the Graph scopes are chosen at TokenManager construction too, so
        //    going from OneDrive-only to any SharePoint needs a wider consent than we hold.
        var portChanged = newPort != config.Port;
        var accountChanged = !string.Equals(newAccount, config.Account, StringComparison.OrdinalIgnoreCase);
        var newSharePoint = newMounts.Any(m => m.IsSharePoint) && !config.AnySharePoint;
        var restartRequired = portChanged || accountChanged || newSharePoint;

        // Mounts that don't widen scopes take effect immediately — the middleware resolves them
        // off this same list on every request.
        if (!restartRequired) config.Mounts = newMounts;

        log.LogInformation("Settings saved (port {Port}, {Count} mount(s), restart required: {Restart})",
            newPort, newMounts.Count, restartRequired);

        await WriteJson(ctx, new { ok = true, restartRequired, warnings });
    }

    // Only write a field into the user's layer when it actually differs, or when the user already
    // owns it. A value inherited from the machine config stays inherited.
    private static bool ShouldPersist(bool changed, SettingSource currentSource) =>
        changed || currentSource == SettingSource.User;

    // ── Sign-in ──────────────────────────────────────────────────────────────────
    private async Task HandleSignIn(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Post) { ctx.Response.StatusCode = 405; return; }
        if (!auth.IsValidMutation(ctx)) { await WriteExpired(ctx); return; }

        // This server is a windowless WinExe — WAM refuses to prompt without a real parent window,
        // so we can't sign in from inside this process. Launching a separate --login process is
        // the supported path: it allocates its own console and the account picker has something to
        // attach to.
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) { await WriteError(ctx, 500, "Could not locate the executable to sign in with."); return; }

            Process.Start(new ProcessStartInfo(exe, "--login") { UseShellExecute = true });
            await WriteJson(ctx, new { ok = true, message = "A sign-in window is opening. Finish there, then come back and reload." });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not launch --login");
            await WriteError(ctx, 500, $"Could not start sign-in: {ex.Message}");
        }
    }

    // ── Remap ────────────────────────────────────────────────────────────────────
    private async Task HandleRemap(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Post) { ctx.Response.StatusCode = 405; return; }
        if (!auth.IsValidMutation(ctx)) { await WriteExpired(ctx); return; }

        try
        {
            var result = await mapper.ApplyAsync(config);
            await WriteJson(ctx, new { ok = result.Errors.Count == 0, result.Mapped, result.Unmapped, result.Errors });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Remap failed");
            await WriteError(ctx, 500, $"Could not remap drives: {ex.Message}");
        }
    }

    // ── Autostart ────────────────────────────────────────────────────────────────
    private sealed class AutostartRequest
    {
        public bool Enabled { get; set; }
    }

    // Not part of HandleSave, on purpose. Everything there lives in config.json and takes effect
    // when the file is written; this one reaches outside the app entirely — it registers or
    // removes a Windows scheduled task — and it can be refused by the machine while every other
    // field on the page saves fine. Bundling the two would mean one failure mode swallowing the
    // other's result.
    private async Task HandleAutostart(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Post) { ctx.Response.StatusCode = 405; return; }
        if (!auth.IsValidMutation(ctx)) { await WriteExpired(ctx); return; }

        AutostartRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<AutostartRequest>(ctx.Request.Body, Json);
        }
        catch (JsonException ex)
        {
            await WriteError(ctx, 400, $"That request body isn't valid JSON: {ex.Message}");
            return;
        }
        if (body == null) { await WriteError(ctx, 400, "Empty request body."); return; }

        try
        {
            var status = await autostart.SetAsync(body.Enabled);
            await WriteJson(ctx, new { ok = true, autostart = Describe(status) });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not change autostart");
            await WriteError(ctx, 500, ex.Message);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────
    private static object Describe(Autostart.Status status) =>
        new { enabled = status.Enabled, via = status.Via.ToString(), description = status.Description };

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool SameMounts(List<Mount> a, List<Mount> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            string.Equals(p.First.Letter, p.Second.Letter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.First.Type, p.Second.Type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.First.Site, p.Second.Site, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.First.Library, p.Second.Library, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal));

    private static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static async Task WriteHtml(HttpContext ctx, string html)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        // The page is entirely self-contained: no external anything, no framing, no form posts to
        // elsewhere. Lock that down so a bug in it can't become a way out.
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
            "connect-src 'self'; img-src data:; form-action 'none'; frame-ancestors 'none'; base-uri 'none'";
        await ctx.Response.WriteAsync(html);
    }

    private static async Task WriteJson(HttpContext ctx, object payload)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
    }

    private static async Task WriteError(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        await WriteJson(ctx, new { ok = false, error = message });
    }

    // A dead session used to be a bare 403 with an empty body, which the page could only render as
    // "Re-map failed." — the one message guaranteed to send someone hunting for a bug in the drive
    // mapping, which was working fine. Say what actually happened and how to get out of it. The
    // `expired` flag lets the page react to this specific case rather than string-matching.
    private static async Task WriteExpired(HttpContext ctx)
    {
        ctx.Response.StatusCode = 403;
        await WriteJson(ctx, new
        {
            ok = false,
            expired = true,
            error = "This settings page has expired. Open Settings again from the OneDriveAsADrive tray icon (next to the clock), or from the Start Menu."
        });
    }
}
