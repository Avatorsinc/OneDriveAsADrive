using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneDriveAsADrive.Auth;

namespace OneDriveAsADrive.Settings;

// Auth for the settings UI. Deliberately NOT the same mechanism the WebDAV redirector uses.
//
// The tempting shortcut is to put the settings page behind the existing HTTP Basic secret. That
// is a real hole, not a theoretical one: once a browser has cached Basic credentials for
// localhost:PORT, ANY website the user later visits can POST to our settings endpoint and the
// browser attaches those credentials for them. The existing DNS-rebinding guard does not help —
// it checks that the Host header is loopback, and in that attack the Host header genuinely IS
// localhost:PORT. It sails straight through.
//
// So the settings surface gets its own three locks:
//   1. A bootstrap token, proving the caller can read the per-user .secret file.
//   2. A session cookie: HttpOnly + SameSite=Strict, so a cross-site request never carries it.
//   3. A CSRF token echoed in a custom header, which also forces a preflight on cross-origin
//      attempts. Belt, suspenders, and a second pair of suspenders.
public sealed class SettingsAuth(ServerSecret secret)
{
    public const string CookieName = "odad_sid";
    public const string CsrfHeader = "X-CSRF-Token";

    private const string BootstrapContext = "odad-settings-bootstrap";
    private const string SessionContext = "odad-settings-session";
    private const string CsrfContext = "odad-settings-csrf";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);

    // ── Bootstrap ────────────────────────────────────────────────────────────────
    // `--settings` needs to prove it's the same user without an IPC channel to the already-running
    // server. Both processes can read %LOCALAPPDATA%\...\.secret, so both can derive the same HMAC.
    //
    // We pass the DERIVED token in the URL, never the raw secret: a token is worthless a minute
    // later, whereas a secret leaked into browser history is your drive. It's minute-bucketed, and
    // we accept the previous bucket too so a launch at :59.9 doesn't fail for no reason.
    public string MintBootstrapToken() => Hmac(UnixMinute());

    public bool IsValidBootstrapToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var minute = UnixMinute();
        // FixedTimeEquals on both candidates, and no early return between them, so a caller can't
        // time the difference between "wrong" and "one minute stale".
        var a = FixedEquals(token, Hmac(minute));
        var b = FixedEquals(token, Hmac(minute - 1));
        return a || b;
    }

    private static long UnixMinute() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;

    private string Hmac(long minute) => Hmac($"{BootstrapContext}|{minute}");

    // Every signed thing here goes through this one door, and each caller prefixes its own
    // context string, so a bootstrap token can never be replayed as a session or a CSRF token.
    private string Hmac(string message) =>
        Base64Url(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret.Value), Encoding.UTF8.GetBytes(message)));

    // ── Sessions ─────────────────────────────────────────────────────────────────
    // Sessions are SIGNED, not stored. They used to live in a dictionary in this process, which
    // meant restarting the server silently killed every open settings tab: the browser still had
    // its cookie, so the page looked fine and kept rendering, but every button came back 403.
    // That is not a rare corner — saving a port or account change tells the user to restart, so
    // the app was routinely instructing people into a dead page.
    //
    // A session is "{expiry}.{nonce}.{hmac}" keyed on the same per-install secret. The server can
    // verify one it has never seen, so a restart costs nothing, and the locks are unchanged: the
    // nonce is 32 random bytes, the HMAC can't be forged without the secret file, and the cookie
    // is still HttpOnly + SameSite=Strict. The one thing we give up is server-side revocation —
    // DropSession existed but was never called by anything, so there was nothing to give up.
    public (string SessionId, string CsrfToken) CreateSession()
    {
        var expires = (DateTimeOffset.UtcNow + SessionLifetime).ToUnixTimeSeconds();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var sid = SignSession(expires, nonce);
        return (sid, DeriveCsrf(sid));
    }

    public string? CsrfTokenFor(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;

        var parts = sessionId.Split('.');
        if (parts.Length != 3) return null;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
            return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expires) <= DateTimeOffset.UtcNow) return null;

        // Re-sign what we were handed and compare whole. If any byte of the expiry or nonce was
        // touched, the signature won't match — so this checks integrity and expiry together.
        if (!FixedEquals(sessionId, SignSession(expires, parts[1]))) return null;

        return DeriveCsrf(sessionId);
    }

    public bool IsValidSession(string? sessionId) => CsrfTokenFor(sessionId) != null;

    private string SignSession(long expires, string nonce)
    {
        var payload = $"{expires.ToString(CultureInfo.InvariantCulture)}.{nonce}";
        return $"{payload}.{Hmac($"{SessionContext}|{payload}")}";
    }

    // The CSRF token is derived from the session rather than stored beside it, for the same
    // reason. It stays unguessable because the session id it's derived from carries 32 random
    // bytes and the secret is needed to compute the HMAC — and it never leaves the page, which
    // only receives it over a request that already proved it holds the cookie.
    private string DeriveCsrf(string sessionId) => Hmac($"{CsrfContext}|{sessionId}");

    // Mutations only. Requires a live session AND the matching CSRF token in a custom header.
    public bool IsValidMutation(HttpContext ctx)
    {
        var sid = ctx.Request.Cookies[CookieName];
        var expected = CsrfTokenFor(sid);
        if (expected == null) return false;

        var supplied = ctx.Request.Headers[CsrfHeader].FirstOrDefault();
        return supplied != null && FixedEquals(supplied, expected);
    }

    // ── Origin checks ────────────────────────────────────────────────────────────
    // Modern browsers label the provenance of a request. A same-origin fetch from our own page is
    // "same-origin"; a top-level navigation the user typed is "none". Anything else — "cross-site",
    // "same-site" — has no business calling this API. Absent header = a non-browser client (curl,
    // our own installer), which the session cookie already gates.
    public static bool IsAcceptableFetchSite(HttpContext ctx)
    {
        var site = ctx.Request.Headers["Sec-Fetch-Site"].FirstOrDefault();
        return site is null or "same-origin" or "none";
    }

    // If an Origin is present it must be one of our own loopback origins. A browser always sends
    // Origin on POST, so this is a hard gate on the mutation path.
    public static bool IsAcceptableOrigin(HttpContext ctx, int port)
    {
        var origin = ctx.Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.Port == port
            && uri.Scheme == Uri.UriSchemeHttp
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host is "127.0.0.1" or "::1" or "[::1]");
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
