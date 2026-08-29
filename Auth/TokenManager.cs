using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using System.Runtime.InteropServices;
using OneDriveAsADrive.Config;

namespace OneDriveAsADrive.Auth;

// You know what really grinds my gears? Having to register an Azure AD app
// just to access your own damn files. So we're using Microsoft's own public
// client ID. Freakin' sweet.
public class TokenManager
{
    // Microsoft Graph Command Line Tools client ID — Microsoft's own public app.
    // No app registration needed. Like finding a parking spot right at the front.
    private const string ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    // Scopes are chosen at runtime based on what's mounted:
    //
    //  • OneDrive only  → Files.ReadWrite = your OWN OneDrive. Narrow on purpose: it does
    //    NOT trip tenant admin-consent walls, so personal accounts just work.
    //
    //  • Any SharePoint → we MUST widen to Files.ReadWrite.All (read/write files in the
    //    libraries you can reach) + Sites.Read.All (to discover the site's drives). These
    //    broad scopes usually require a ONE-TIME tenant admin consent. That's the price of
    //    SharePoint; there's no narrow scope that reaches other people's shared libraries.
    //    Peter can't raid the studio fridge without a studio badge.
    private readonly string[] _scopes;

    // Optional account (UPN/email) to pin which identity we sign in as, when the machine has
    // more than one. Null = take the default/first account. Stops us walking home with the
    // wrong family, Griffin-style.
    private readonly string? _account;

    private readonly IPublicClientApplication _app;
    private readonly ILogger<TokenManager> _log;

    // Summon a window handle for the WAM sign-in popup. Peter once did this to yell at his TV.
    // WAM REFUSES to prompt without a parent HWND (window_handle_required). A background WinExe
    // has no console, so we fall back to the foreground window, then the desktop — anything
    // non-zero — so the very first interactive sign-in works even when we're running hidden.
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]   private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]   private static extern IntPtr GetDesktopWindow();

    private static IntPtr ParentWindow()
    {
        var h = GetConsoleWindow();
        if (h == IntPtr.Zero) h = GetForegroundWindow();
        if (h == IntPtr.Zero) h = GetDesktopWindow();
        return h;
    }

    public TokenManager(ILogger<TokenManager> log, MountConfig config)
    {
        _log = log;
        _account = string.IsNullOrWhiteSpace(config.Account) ? null : config.Account.Trim();

        _scopes = config.AnySharePoint
            ? ["Files.ReadWrite.All", "Sites.Read.All", "offline_access"]
            : ["Files.ReadWrite", "offline_access"];

        if (config.AnySharePoint)
            _log.LogInformation(
                "SharePoint mounts detected — requesting broader Graph scopes (Files.ReadWrite.All, Sites.Read.All). " +
                "A one-time tenant admin consent may be required.");

        // Build the MSAL app with Windows WAM broker.
        // WAM is like Joe Swanson — it handles things the right way so you don't have to.
        // Authority "common" = accepts BOTH work/school AND personal Microsoft accounts.
        // Personal accounts self-consent (no admin wall), so this is also how you test
        // when your work tenant is locked down tighter than Stewie's diary.
        _app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, "common")
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                ListOperatingSystemAccounts = true  // show all signed-in Windows accounts
            })
            .Build();
    }

    // The BACKGROUND SERVER must never pop a sign-in window — it's a windowless WinExe, so an
    // interactive prompt has nothing to attach to and just hangs whatever launched it (which is
    // exactly how a "silent" winget install stalled forever). So interactive is OFF by default:
    // the server does silent-only auth and, if there's no cached token yet, fails cleanly rather
    // than prompting. The ONLY place a window exists is the `--login` step, which flips this on.
    public bool AllowInteractive { get; set; }

    // The account we most recently got a real token for. A corporate laptop can easily have four
    // identities signed in to Windows, and only one of them can reach the files we want; once we
    // know which, we go straight there instead of re-failing our way down the list every request.
    private IAccount? _lastGood;

    // Who we can ACTUALLY authenticate as, for the settings UI's status line.
    //
    // This used to report accounts[0], which on a multi-account machine is a coin flip and was
    // cheerfully naming a work account that could not produce a token while a perfectly good
    // personal one sat behind it. A status line that names an account you can't use is worse than
    // no status line - it sends people off reinstalling things that were never broken.
    public async Task<string?> GetSignedInAccountAsync()
    {
        try
        {
            // The server warms up auth on startup, so this is normally already answered and free.
            if (_lastGood != null) return _lastGood.Username;
            var result = await TrySilentAsync(await CandidatesAsync());
            return result?.Account?.Username;
        }
        catch
        {
            // Broker unavailable or cache unreadable — report "not signed in" rather than throwing
            // into a page render.
            return null;
        }
    }

    // Which accounts are worth trying, best first.
    private async Task<List<IAccount>> CandidatesAsync()
    {
        var accounts = (await _app.GetAccountsAsync()).ToList();

        // A pinned account is a decision, not a hint: try it and nothing else, so a typo surfaces
        // as "that account can't sign in" instead of silently mounting somebody else's OneDrive.
        if (_account != null)
            return accounts
                .Where(a => string.Equals(a.Username, _account, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var ordered = new List<IAccount>();
        var known = _lastGood?.HomeAccountId?.Identifier;
        if (known != null)
            ordered.AddRange(accounts.Where(a => a.HomeAccountId?.Identifier == known));
        ordered.AddRange(accounts.Where(a => a.HomeAccountId?.Identifier != known));
        return ordered;
    }

    // Walk the candidates and take the first token anyone will give us.
    //
    // The old code tried exactly ONE account and treated its MsalUiRequiredException as proof that
    // nobody was signed in. It isn't: that exception means "this identity can't get this token
    // right now" - no consent in its tenant, a revoked grant, or a Windows account that has simply
    // never used this app (which is most of them, since we list OS accounts). It says nothing at
    // all about the next account in the list.
    private async Task<AuthenticationResult?> TrySilentAsync(IReadOnlyList<IAccount> candidates)
    {
        foreach (var account in candidates)
        {
            try
            {
                var result = await _app.AcquireTokenSilent(_scopes, account).ExecuteAsync();
                if (_lastGood?.HomeAccountId?.Identifier != result.Account?.HomeAccountId?.Identifier)
                    _log.LogInformation("Authenticated as {Account}", result.Account?.Username);
                _lastGood = result.Account ?? account;
                // Giggity. Token acquired without anyone knowing.
                return result;
            }
            catch (MsalUiRequiredException ex)
            {
                _log.LogDebug("No silent token for {Account}: {Reason}", account.Username, ex.Message);
            }
        }
        return null;
    }

    public async Task<string> GetAccessTokenAsync()
    {
        // First try silent — like when Stewie does something terrible and nobody notices.
        var candidates = await CandidatesAsync();
        var silent = await TrySilentAsync(candidates);
        if (silent != null) return silent.AccessToken;

        if (!AllowInteractive)
        {
            // Silent auth failed and we're the background server — do NOT prompt. Let the caller
            // deal with a token-less server (drive stays empty until someone runs --login). Popping
            // a window from here is what hung the unattended install; we're not making that mistake.
            //
            // A pinned account that matched nothing is its own diagnosis, so say so - otherwise the
            // message sends people to --login, which won't help if the UPN is simply wrong.
            throw new InvalidOperationException(
                _account != null && candidates.Count == 0
                    ? $"No signed-in Windows account matches '{_account}'. Check the account on the settings page, or run 'OneDriveAsADrive.exe --login' to sign in as it."
                    : "No cached credentials. Run 'OneDriveAsADrive.exe --login' to sign in.");
        }

        // Interactive path (--login only). Like Peter finally reading the room.
        _log.LogInformation("No cached token on any of {Count} account(s) — prompting via WAM...", candidates.Count);

        // Interactive WAM — ONLY reached in --login mode (AllowInteractive = true), where a real
        // console window exists for the picker. A machine can have several signed-in identities
        // (personal @outlook AND work @tenant), and only one can reach a given tenant's SharePoint;
        // a pinned account signs straight in, otherwise the user picks.
        var interactive = _app.AcquireTokenInteractive(_scopes)
            .WithParentActivityOrWindow(ParentWindow());
        interactive = _account != null
            ? interactive.WithLoginHint(_account)
            : interactive.WithPrompt(Prompt.SelectAccount);

        var result = await interactive.ExecuteAsync();
        _lastGood = result.Account;
        _log.LogInformation("Signed in as {Account}", result.Account?.Username);
        return result.AccessToken;
    }
}
