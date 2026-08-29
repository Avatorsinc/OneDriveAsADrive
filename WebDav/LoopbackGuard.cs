using System.Net;

namespace OneDriveAsADrive.WebDav;

// The two front-door checks every request has to pass, in one place so the WebDAV path and the
// settings path can't quietly drift apart. Both are load-bearing: what's behind them is your
// Graph token.
public static class LoopbackGuard
{
    // The packet must physically come from this machine. The host check below stops DNS
    // rebinding, but if someone launches with --urls http://*:PORT we're reachable off-box and a
    // remote client can just send "Host: localhost" to sail past it.
    public static bool RemoteIsLoopback(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        return remote == null || IPAddress.IsLoopback(remote);
    }

    // Only answer to loopback host names. A malicious website can point its DNS at 127.0.0.1
    // (DNS rebinding) and try to read your files through the browser — but its requests carry ITS
    // domain in the Host header, not "localhost". So we slam the door on anything else.
    //
    // Note what this does NOT stop: a page that simply fetches http://localhost:PORT directly
    // sends a genuine loopback Host and passes cleanly. That's CSRF, and it's why the settings
    // surface has its own cookie and token checks in SettingsAuth.
    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "127.0.0.1" or "::1" or "[::1]";
}
