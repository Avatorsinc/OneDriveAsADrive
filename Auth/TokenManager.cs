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

    private readonly IPublicClientApplication _app;
    private readonly ILogger<TokenManager> _log;

    // Summon the console window handle. Peter once did this to yell at his TV. Same energy.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    public TokenManager(ILogger<TokenManager> log, MountConfig config)
    {
        _log = log;

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

    public async Task<string> GetAccessTokenAsync()
    {
        // First try silent — like when Stewie does something terrible and nobody notices.
        var accounts = await _app.GetAccountsAsync();

        try
        {
            var silent = await _app.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
                .ExecuteAsync();

            // Giggity. Token acquired without anyone knowing.
            return silent.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            // Silent auth failed. Like Peter trying to sneak out of church early.
            _log.LogInformation("Silent auth failed — prompting via WAM (your existing Windows work session)...");
        }

        // Interactive WAM — Windows will use the already-signed-in work account.
        // Should be a quick popup at worst. NOT a full MFA rodeo. Victory is mine!
        var result = await _app.AcquireTokenInteractive(_scopes)
            .WithParentActivityOrWindow(GetConsoleWindow())
            .ExecuteAsync();

        return result.AccessToken;
    }
}
