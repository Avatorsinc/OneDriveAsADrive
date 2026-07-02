using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDriveAsADrive.Auth;

namespace OneDriveAsADrive.Graph;

// This whole class is basically Peter trying to find where he put his files.
// Except it actually works. Unlike Peter.
//
// Performance note: Windows Explorer is CLINGY. It re-asks for the same folders
// and probes the same phantom files (Desktop.ini, thumbs.db) over and over like
// Stewie asking "are we there yet." So we cache aggressively for a few seconds.
public class OneDriveProvider
{
    private readonly GraphServiceClient _client;
    private readonly IMemoryCache _cache;

    private string? _driveId;
    private string? _rootId;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // How long we trust cached data. Short enough that changes show up quickly,
    // long enough that Explorer's rapid-fire probing hits cache instead of Redmond.
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromSeconds(12);
    // Phantom-file 404s basically never become real, so cache the "nope" longer.
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(45);

    public OneDriveProvider(TokenManager tokenManager, IMemoryCache cache)
    {
        var authProvider = new BaseBearerTokenAuthenticationProvider(
            new MsalTokenProvider(tokenManager));
        _client = new GraphServiceClient(authProvider);
        _cache = cache;
    }

    // Lazy-loads the drive ID on first use.
    // Like Brian's novel — referenced constantly but only materializes when forced.
    private async Task<string> GetDriveIdAsync()
    {
        if (_driveId != null) return _driveId;
        await _initLock.WaitAsync();
        try
        {
            if (_driveId == null)
            {
                var drive = await _client.Me.Drive.GetAsync();
                _driveId = drive?.Id ?? throw new InvalidOperationException(
                    // Holy crap, we couldn't get the drive ID.
                    // That's worse than the time Peter couldn't find the remote.
                    "Could not retrieve OneDrive ID from Microsoft Graph");
            }
            return _driveId;
        }
        finally { _initLock.Release(); }
    }

    // Get metadata for a path — AND, if it's a folder, grab its children in the SAME
    // network call via $expand=children. One round trip instead of two. Freakin' sweet.
    //
    // Results are cached. 404s return null (and are cached) instead of throwing —
    // Windows probes for phantom files constantly and we're not gonna cry every time.
    public async Task<DriveItem?> GetItemAsync(string davPath)
    {
        var path = ToGraphPath(davPath);
        var key = ItemKey(path);

        if (_cache.TryGetValue(key, out DriveItem? cached))
            return cached; // could be a cached null (negative hit) — that's fine

        var driveId = await GetDriveIdAsync();
        DriveItem? item;
        try
        {
            item = string.IsNullOrEmpty(path)
                ? await _client.Drives[driveId].Root.GetAsync(rc =>
                    rc.QueryParameters.Expand = ["children"])
                : await _client.Drives[driveId].Root.ItemWithPath(path).GetAsync(rc =>
                    rc.QueryParameters.Expand = ["children"]);
        }
        catch (ODataError e) when (e.ResponseStatusCode == 404)
        {
            // File doesn't exist. That's not an error, that's just Tuesday.
            _cache.Set(key, (DriveItem?)null, NegativeTtl);
            return null;
        }

        _cache.Set(key, item, PositiveTtl);

        // Bonus: the expand handed us the children for free, so seed THAT cache too.
        // Now the PROPFIND that follows is an instant cache hit. Giggity.
        if (item?.Folder != null && item.Children != null)
            _cache.Set(ChildrenKey(path), item.Children, PositiveTtl);

        return item;
    }

    // List folder contents. Almost always a cache hit thanks to the expand above —
    // GetItemAsync already stuffed the children in the cache like Peter stuffing his face.
    public async Task<List<DriveItem>> GetChildrenAsync(string davPath)
    {
        var path = ToGraphPath(davPath);
        var key = ChildrenKey(path);

        if (_cache.TryGetValue(key, out List<DriveItem>? cached) && cached != null)
            return cached;

        var driveId = await GetDriveIdAsync();
        DriveItemCollectionResponse? resp;
        try
        {
            resp = string.IsNullOrEmpty(path)
                ? await _client.Drives[driveId].Items[await GetRootIdAsync()].Children.GetAsync()
                : await _client.Drives[driveId].Root.ItemWithPath(path).Children.GetAsync();
        }
        catch (ODataError e) when (e.ResponseStatusCode == 404)
        {
            return [];
        }

        var children = resp?.Value ?? [];
        _cache.Set(key, children, PositiveTtl);
        return children;
    }

    // Root's item ID, cached. Kills a redundant round trip on root children.
    private async Task<string> GetRootIdAsync()
    {
        if (_rootId != null) return _rootId;
        var driveId = await GetDriveIdAsync();
        var root = await _client.Drives[driveId].Root.GetAsync();
        _rootId = root?.Id ?? throw new InvalidOperationException("No root ID. The deuce?");
        return _rootId;
    }

    // Stream a file's bytes down to the client.
    // "You know what, I am going to get that file and nothing is going to stop me."
    public async Task<Stream?> GetContentAsync(string davPath)
    {
        var driveId = await GetDriveIdAsync();
        var path = ToGraphPath(davPath);
        return await _client.Drives[driveId].Root.ItemWithPath(path).Content.GetAsync();
    }

    // Upload a file. Like Peter squeezing into a small space — it'll go in, don't worry.
    // Note: files over ~4MB should use upload sessions. That's a TODO. Don't tell Stewie.
    public async Task UploadAsync(string davPath, Stream content)
    {
        var driveId = await GetDriveIdAsync();
        var path = ToGraphPath(davPath);
        await _client.Drives[driveId].Root.ItemWithPath(path).Content.PutAsync(content);
        Invalidate(path); // new file — bust the cache so it shows up NOW
    }

    // Delete. Permanent. Gone. Like Meg's self-esteem.
    public async Task DeleteAsync(string davPath)
    {
        var driveId = await GetDriveIdAsync();
        var path = ToGraphPath(davPath);
        await _client.Drives[driveId].Root.ItemWithPath(path).DeleteAsync();
        Invalidate(path);
    }

    // Create a folder. "Victory is mine! A new folder has been born!"
    public async Task CreateFolderAsync(string davPath)
    {
        var driveId = await GetDriveIdAsync();
        var path = ToGraphPath(davPath);
        var parentPath = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        var folderName = Path.GetFileName(path);

        var newFolder = new DriveItem
        {
            Name = folderName,
            Folder = new Folder(),
            // fail = don't silently overwrite existing folder like Peter at a buffet
            AdditionalData = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "fail" }
        };

        if (string.IsNullOrEmpty(parentPath))
            await _client.Drives[driveId].Items[await GetRootIdAsync()].Children.PostAsync(newFolder);
        else
            await _client.Drives[driveId].Root.ItemWithPath(parentPath).Children.PostAsync(newFolder);

        Invalidate(path);
    }

    // Move or rename an item. PATCH with a new parent/name.
    // "Now I know how the mailman feels every Tuesday." — Peter, probably.
    public async Task MoveAsync(string davSource, string davDest)
    {
        var driveId = await GetDriveIdAsync();
        var srcPath = ToGraphPath(davSource);
        var destPath = ToGraphPath(davDest);
        var destParent = Path.GetDirectoryName(destPath)?.Replace('\\', '/') ?? "";
        var destName = Path.GetFileName(destPath);

        DriveItem? destParentItem = string.IsNullOrEmpty(destParent)
            ? await _client.Drives[driveId].Root.GetAsync()
            : await _client.Drives[driveId].Root.ItemWithPath(destParent).GetAsync();

        // PATCH with new parent ID + new name. Graph does the rest. Freakin' sweet.
        var patch = new DriveItem
        {
            Name = destName,
            ParentReference = new ItemReference { Id = destParentItem?.Id }
        };

        await _client.Drives[driveId].Root.ItemWithPath(srcPath).PatchAsync(patch);
        Invalidate(srcPath);  // it left here
        Invalidate(destPath); // ...and landed there
    }

    // Nuke cached entries for a path and its parent folder listing, so writes show
    // up immediately instead of after the TTL. Cache invalidation: one of the two
    // hard problems in computer science. The other is Meg.
    private void Invalidate(string path)
    {
        _cache.Remove(ItemKey(path));
        _cache.Remove(ChildrenKey(path));

        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        _cache.Remove(ItemKey(parent));
        _cache.Remove(ChildrenKey(parent));
    }

    private static string ItemKey(string path) => "item:" + path;
    private static string ChildrenKey(string path) => "children:" + path;

    // "/Documents/file.txt" → "Documents/file.txt" — Graph doesn't want the leading slash.
    // Deucedly inconsistent API design, but here we are.
    private static string ToGraphPath(string davPath) =>
        davPath.TrimStart('/').TrimEnd('/');
}

// Hands MSAL tokens to the Graph SDK. The middleman nobody asked for but everybody needs.
// Like Quagmire at a party.
internal class MsalTokenProvider(TokenManager tokenManager) : IAccessTokenProvider
{
    public Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default) =>
        tokenManager.GetAccessTokenAsync();

    public AllowedHostsValidator AllowedHostsValidator { get; } = new(["graph.microsoft.com"]);
}
