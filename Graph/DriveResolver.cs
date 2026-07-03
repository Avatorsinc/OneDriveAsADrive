using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using OneDriveAsADrive.Auth;
using OneDriveAsADrive.Config;

namespace OneDriveAsADrive.Graph;

// Turns a Mount (onedrive, or a SharePoint site+library) into a Graph driveId.
// Everything downstream just needs the driveId — the OneDriveProvider treats a SharePoint
// document library exactly like a personal drive, because to Graph they ARE the same thing.
//
// We call Graph over plain HTTP here (not the SDK) purely to dodge the SDK's path-parameter
// encoding when addressing a site by "hostname:/sites/Name". Fewer surprises. Like a rerun.
public sealed class DriveResolver
{
    private readonly TokenManager _tokens;
    private readonly ILogger<DriveResolver> _log;

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
    };

    // Resolved drive IDs, cached forever (they don't change). Keyed per mount so two
    // SharePoint libraries don't get confused for each other.
    private readonly ConcurrentDictionary<string, Task<string>> _drives = new();

    public DriveResolver(TokenManager tokens, ILogger<DriveResolver> log)
    {
        _tokens = tokens;
        _log = log;
    }

    public Task<string> ResolveDriveIdAsync(Mount mount)
    {
        var key = mount.Prefix + "|" + mount.Type + "|" + mount.Site + "|" + mount.Library;
        return _drives.GetOrAdd(key, _ => ResolveInnerAsync(mount, key));
    }

    private async Task<string> ResolveInnerAsync(Mount mount, string key)
    {
        try
        {
            return mount.IsSharePoint
                ? await ResolveSharePointAsync(mount)
                : await GetJsonIdAsync("me/drive");
        }
        catch
        {
            // Don't poison the cache with a permanently-faulted task — let the next request retry.
            _drives.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<string> ResolveSharePointAsync(Mount mount)
    {
        if (string.IsNullOrWhiteSpace(mount.Site))
            throw new InvalidOperationException(
                $"SharePoint mount {mount.Letter}: has no 'site' configured. Give me something to work with.");

        // 1) site address ("contoso.sharepoint.com:/sites/Finance") -> site id
        var site = mount.Site.Trim();
        var siteId = await GetJsonIdAsync($"sites/{site}");

        // 2) no library named? use the site's default document library.
        if (string.IsNullOrWhiteSpace(mount.Library))
        {
            var driveId = await GetJsonIdAsync($"sites/{siteId}/drive");
            _log.LogInformation("Resolved SharePoint {Letter}: {Site} (default library) -> drive {Drive}",
                mount.Letter, site, driveId);
            return driveId;
        }

        // 3) named library -> find the matching drive under the site.
        using var doc = await GetJsonAsync($"sites/{siteId}/drives");
        foreach (var d in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var name = d.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.Equals(name, mount.Library, StringComparison.OrdinalIgnoreCase))
            {
                var driveId = d.GetProperty("id").GetString()!;
                _log.LogInformation("Resolved SharePoint {Letter}: {Site} / {Library} -> drive {Drive}",
                    mount.Letter, site, mount.Library, driveId);
                return driveId;
            }
        }

        throw new InvalidOperationException(
            $"SharePoint site '{site}' has no document library named '{mount.Library}'. Check the name in the site's Documents.");
    }

    private async Task<string> GetJsonIdAsync(string relativeUrl)
    {
        using var doc = await GetJsonAsync(relativeUrl);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"Graph returned no id for {relativeUrl}. The deuce?");
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl)
    {
        var token = await _tokens.GetAccessTokenAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Graph GET {relativeUrl} failed: {(int)resp.StatusCode}. {Truncate(body)}");
        return JsonDocument.Parse(body);
    }

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "...";
}
