using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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
// It's drive-agnostic now: every method takes a driveId, so the SAME instance serves your
// personal OneDrive AND every SharePoint library at once. To Graph, a SharePoint document
// library is just another "drive" — so all the same calls work. Freakin' sweet.
//
// Performance note: Windows Explorer is CLINGY. It re-asks for the same folders
// and probes the same phantom files (Desktop.ini, thumbs.db) over and over like
// Stewie asking "are we there yet." So we cache aggressively for a few seconds.
public class OneDriveProvider
{
    // Shared HttpClient for streaming file content straight off OneDrive's
    // pre-authenticated download URLs (no bearer token needed on those). One instance,
    // reused, like Peter's one good pair of pants.
    private static readonly HttpClient Http = new();

    // Graph caps uploads via simple PUT around 4 MB. Bigger than this and we switch
    // to a chunked upload session, or the upload just faceplants.
    private const long SimpleUploadLimit = 4L * 1024 * 1024;
    // Upload session chunk size: 5 MiB. MUST be a multiple of 320 KiB or Graph sulks.
    // (5 MiB = 16 × 320 KiB. The math checks out, Brian.)
    private const int UploadChunkSize = 5 * 1024 * 1024;

    private readonly GraphServiceClient _client;
    private readonly IMemoryCache _cache;

    // Root item IDs, cached per drive. Kills a redundant round trip on root children.
    private readonly ConcurrentDictionary<string, string> _rootIds = new();

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

    // Get metadata for a path — AND, if it's a folder, grab its (first page of) children
    // in the SAME network call via $expand=children. One round trip instead of two.
    //
    // Results are cached. 404s return null (and are cached) instead of throwing —
    // Windows probes for phantom files constantly and we're not gonna cry every time.
    public async Task<DriveItem?> GetItemAsync(string driveId, string davPath)
    {
        var path = ToGraphPath(davPath);
        var key = ItemKey(driveId, path);

        if (_cache.TryGetValue(key, out DriveItem? cached))
            return cached; // could be a cached null (negative hit) — that's fine

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

        // Bonus: the expand handed us children for free — seed THAT cache too, so the
        // PROPFIND that follows is an instant hit. BUT only if the expand wasn't
        // truncated (folders with >200 items paginate; a partial seed would hide files).
        if (item?.Folder != null && item.Children != null &&
            item.AdditionalData?.ContainsKey("children@odata.nextLink") != true)
        {
            _cache.Set(ChildrenKey(driveId, path), item.Children, PositiveTtl);
        }

        return item;
    }

    // List folder contents — ALL of them. Follows @odata.nextLink so folders with
    // hundreds/thousands of items don't get silently chopped at the first page.
    // (Usually a cache hit anyway thanks to the expand seed above.)
    public async Task<List<DriveItem>> GetChildrenAsync(string driveId, string davPath)
    {
        var path = ToGraphPath(davPath);
        var key = ChildrenKey(driveId, path);

        if (_cache.TryGetValue(key, out List<DriveItem>? cachedList) && cachedList != null)
            return cachedList;

        DriveItemCollectionResponse? firstPage;
        try
        {
            firstPage = string.IsNullOrEmpty(path)
                ? await _client.Drives[driveId].Items[await GetRootIdAsync(driveId)].Children.GetAsync()
                : await _client.Drives[driveId].Root.ItemWithPath(path).Children.GetAsync();
        }
        catch (ODataError e) when (e.ResponseStatusCode == 404)
        {
            return [];
        }

        var all = new List<DriveItem>();
        if (firstPage != null)
        {
            // PageIterator walks every page for us. No more "first 200 and a prayer."
            var iterator = PageIterator<DriveItem, DriveItemCollectionResponse>
                .CreatePageIterator(_client, firstPage, item => { all.Add(item); return true; });
            await iterator.IterateAsync();
        }

        _cache.Set(key, all, PositiveTtl);
        return all;
    }

    // Root's item ID for a given drive, cached. Kills a redundant round trip on root children.
    private async Task<string> GetRootIdAsync(string driveId)
    {
        if (_rootIds.TryGetValue(driveId, out var id)) return id;
        var root = await _client.Drives[driveId].Root.GetAsync();
        id = root?.Id ?? throw new InvalidOperationException("No root ID. The deuce?");
        _rootIds[driveId] = id;
        return id;
    }

    // Stream a file's bytes, honoring an optional HTTP Range header. Uses OneDrive's
    // pre-authenticated @microsoft.graph.downloadUrl, which lives on a CDN that speaks
    // Range natively — so seeking a 2GB video doesn't download the first 1.9GB first.
    // Returns the raw upstream response so the middleware can relay status + headers.
    public async Task<HttpResponseMessage> DownloadAsync(string driveId, string davPath, string? rangeHeader)
    {
        var item = await GetItemAsync(driveId, davPath);
        if (item == null)
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var url = item.AdditionalData != null &&
                  item.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var u)
            ? u?.ToString()
            : null;

        // 0-byte files (and the odd metadata-only item) have no download URL.
        // Hand back an empty 200 rather than exploding. Nothing to see here.
        if (string.IsNullOrEmpty(url))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Stream.Null) };

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(rangeHeader))
            req.Headers.TryAddWithoutValidation("Range", rangeHeader);

        return await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }

    // Upload a file. Small ones go up in a single PUT; big ones (>4MB) go through a
    // chunked upload session, because Peter can't fit through the door all at once either.
    public async Task UploadAsync(string driveId, string davPath, Stream content, long? contentLength)
    {
        var path = ToGraphPath(davPath);

        if (contentLength is > 0 and <= SimpleUploadLimit)
        {
            await _client.Drives[driveId].Root.ItemWithPath(path).Content.PutAsync(content);
            Invalidate(driveId, path);
            return;
        }

        // We need the total size for the Content-Range headers. If the client didn't
        // tell us (chunked transfer, rare from Windows), buffer to learn it. Not elegant,
        // but neither is Peter, and he gets by.
        Stream source = content;
        long total;
        MemoryStream? buffered = null;
        if (contentLength is > 0)
        {
            total = contentLength.Value;
        }
        else
        {
            buffered = new MemoryStream();
            await content.CopyToAsync(buffered);
            buffered.Position = 0;
            source = buffered;
            total = buffered.Length;
        }

        // Empty file? Simple PUT handles zero bytes fine. Don't over-engineer it.
        if (total == 0)
        {
            await _client.Drives[driveId].Root.ItemWithPath(path).Content.PutAsync(Stream.Null);
            Invalidate(driveId, path);
            buffered?.Dispose();
            return;
        }

        try
        {
            await UploadViaSessionAsync(driveId, path, source, total);
        }
        finally
        {
            buffered?.Dispose();
        }

        Invalidate(driveId, path);
    }

    // The chunked-upload workhorse. Creates a session, then PUTs 5 MiB slices with
    // Content-Range headers straight to the session URL (pre-authed, no bearer needed).
    private async Task UploadViaSessionAsync(string driveId, string path, Stream source, long total)
    {
        var sessionBody = new Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession.CreateUploadSessionPostRequestBody
        {
            Item = new DriveItemUploadableProperties
            {
                AdditionalData = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "replace" }
            }
        };

        var session = await _client.Drives[driveId].Root.ItemWithPath(path)
            .CreateUploadSession.PostAsync(sessionBody);

        var uploadUrl = session?.UploadUrl
            ?? throw new InvalidOperationException("Graph refused to open an upload session. Rude.");

        var buffer = new byte[UploadChunkSize];
        long pos = 0;
        while (pos < total)
        {
            var want = (int)Math.Min(UploadChunkSize, total - pos);
            var read = await ReadExactAsync(source, buffer, want);
            if (read <= 0) break;

            using var chunk = new ByteArrayContent(buffer, 0, read);
            chunk.Headers.ContentLength = read;
            chunk.Headers.ContentRange = new ContentRangeHeaderValue(pos, pos + read - 1, total);

            using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = chunk };
            var resp = await Http.SendAsync(put);

            // 202 = "keep 'em coming", 200/201 = "done, freakin' sweet". Anything else = trouble.
            if (resp.StatusCode != HttpStatusCode.Accepted &&
                resp.StatusCode != HttpStatusCode.OK &&
                resp.StatusCode != HttpStatusCode.Created)
            {
                var detail = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Upload chunk failed at byte {pos}: {(int)resp.StatusCode} {detail}");
            }

            pos += read;
        }
    }

    // Reads up to 'count' bytes, looping until it has them or the stream ends —
    // because a single ReadAsync can return fewer bytes than asked, and a short chunk
    // would corrupt the upload. Persistence, unlike Peter's diets.
    private static async Task<int> ReadExactAsync(Stream s, byte[] buffer, int count)
    {
        var got = 0;
        while (got < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(got, count - got));
            if (n == 0) break;
            got += n;
        }
        return got;
    }

    // Delete. Permanent. Gone. Like Meg's self-esteem.
    public async Task DeleteAsync(string driveId, string davPath)
    {
        var path = ToGraphPath(davPath);
        await _client.Drives[driveId].Root.ItemWithPath(path).DeleteAsync();
        Invalidate(driveId, path);
    }

    // Create a folder. "Victory is mine! A new folder has been born!"
    public async Task CreateFolderAsync(string driveId, string davPath)
    {
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
            await _client.Drives[driveId].Items[await GetRootIdAsync(driveId)].Children.PostAsync(newFolder);
        else
            await _client.Drives[driveId].Root.ItemWithPath(parentPath).Children.PostAsync(newFolder);

        Invalidate(driveId, path);
    }

    // Move or rename an item. PATCH with a new parent/name.
    // "Now I know how the mailman feels every Tuesday." — Peter, probably.
    public async Task MoveAsync(string driveId, string davSource, string davDest)
    {
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
        Invalidate(driveId, srcPath);  // it left here
        Invalidate(driveId, destPath); // ...and landed there
    }

    // Nuke cached entries for a path and its parent folder listing, so writes show
    // up immediately instead of after the TTL. Cache invalidation: one of the two
    // hard problems in computer science. The other is Meg.
    private void Invalidate(string driveId, string path)
    {
        _cache.Remove(ItemKey(driveId, path));
        _cache.Remove(ChildrenKey(driveId, path));

        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        _cache.Remove(ItemKey(driveId, parent));
        _cache.Remove(ChildrenKey(driveId, parent));
    }

    // Cache keys are namespaced per drive so two mounts never collide on the same path.
    private static string ItemKey(string driveId, string path) => $"item:{driveId}:{path}";
    private static string ChildrenKey(string driveId, string path) => $"children:{driveId}:{path}";

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
