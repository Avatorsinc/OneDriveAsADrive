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

    // How long we trust cached data. Measured against a real personal OneDrive, ONE Graph call
    // costs ~800ms no matter how small the folder is (an empty folder and a 10KB listing time the
    // same), so a cache miss is always expensive and the old 12s was far too eager to throw work
    // away — walk into a folder, read it, hit Back, and you'd already paid for it twice. 60s still
    // picks up outside edits within a minute, and our own writes call Invalidate immediately.
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromSeconds(60);
    // Phantom-file 404s basically never become real, so cache the "nope" longer.
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(45);

    // Graph pages children 200 at a time by default. Each extra page is another ~800ms, and they
    // MUST be walked in order, so a 1000-item folder costs 4 seconds of pure pagination. Asking
    // for 999 up front turns that into one trip. ($top is legal on the children endpoint; it is
    // NOT legal inside $expand=children(...) — Graph answers that with 400 invalidRequest.)
    private const int ChildrenPageSize = 999;

    // How many subfolders we'll warm in the background off a single listing, and how many of those
    // may be in flight at once. Six matches what the service will actually overlap: five folders
    // fetched sequentially took 3654ms, the same five in parallel took 858ms.
    private const int PrefetchMaxFolders = 32;
    private static readonly SemaphoreSlim PrefetchGate = new(6);

    // Requests currently in flight, keyed the same way as the cache. Explorer does not ask once
    // and wait — the redirector fires overlapping PROPFINDs for the same folder, and without this
    // each one started its own ~800ms Graph call for an answer its siblings were already fetching.
    private readonly ConcurrentDictionary<string, Lazy<Task<DriveItem?>>> _itemFlights = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<DriveItem>>>> _childFlights = new();

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

        return await Coalesce(_itemFlights, key, () => FetchItemAsync(driveId, path, prefetch: true));
    }

    // One fetch, however many callers asked for it. The Lazy is what makes this airtight:
    // ConcurrentDictionary.GetOrAdd may run its factory on several threads under contention and
    // only keep one result — which would still have STARTED the duplicate calls we're avoiding.
    // Lazy with ExecutionAndPublication guarantees the factory body runs exactly once.
    private static async Task<T> Coalesce<T>(
        ConcurrentDictionary<string, Lazy<Task<T>>> flights, string key, Func<Task<T>> factory)
    {
        var lazy = flights.GetOrAdd(key,
            _ => new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            // Drop it whether it succeeded or threw: the result now lives in the cache, and a
            // failure must not be pinned here for the next caller to inherit.
            flights.TryRemove(key, out _);
        }
    }

    private async Task<DriveItem?> FetchItemAsync(string driveId, string path, bool prefetch)
    {
        var key = ItemKey(driveId, path);

        // $expand=children is ONLY legal on folders. On a file, Graph blows up with "Children
        // cannot be listed from an item that is not a folder". We don't know which it is until
        // we ask, so: try with the expand (one round trip for the folder case), and if that
        // fails, retry the same GET WITHOUT the expand — a real folder still comes back fine,
        // we just don't get its children pre-seeded. Files are the common case for reads/writes,
        // so this correctness fix is essential — not just an optimization.
        //
        // NOTE: we deliberately DON'T key off the HTTP status here. That "not a folder" error
        // does NOT reliably surface as ResponseStatusCode == 400 (it came through as an
        // unhandled ODataError in the wild), so we catch the expand failure broadly and let the
        // plain retry sort out real-vs-phantom via its own 404 check. Peter learned not to
        // assume — the hard way, involving a wood chipper.
        async Task<DriveItem?> Fetch(bool expand) =>
            string.IsNullOrEmpty(path)
                ? await _client.Drives[driveId].Root.GetAsync(rc =>
                    { if (expand) rc.QueryParameters.Expand = ["children"]; })
                : await _client.Drives[driveId].Root.ItemWithPath(path).GetAsync(rc =>
                    { if (expand) rc.QueryParameters.Expand = ["children"]; });

        DriveItem? item;
        var expanded = true;
        try
        {
            item = await Fetch(expand: true);
        }
        catch (ODataError e) when (e.ResponseStatusCode == 404)
        {
            // File doesn't exist. That's not an error, that's just Tuesday.
            _cache.Set(key, (DriveItem?)null, NegativeTtl);
            return null;
        }
        catch (ODataError)
        {
            // Expand failed — almost always because the target is a file, not a folder.
            // Retry the plain GET; if THAT 404s, it really doesn't exist.
            try
            {
                item = await Fetch(expand: false);
                expanded = false;
            }
            catch (ODataError inner) when (inner.ResponseStatusCode == 404)
            {
                _cache.Set(key, (DriveItem?)null, NegativeTtl);
                return null;
            }
        }

        _cache.Set(key, item, PositiveTtl);

        // Bonus: the expand handed us children for free — seed THAT cache too, so the
        // PROPFIND that follows is an instant hit. BUT only if the expand wasn't
        // truncated (folders with >200 items paginate; a partial seed would hide files).
        //
        // We gate on `expanded` rather than on Children being non-null, because those aren't the
        // same question. An EMPTY folder comes back from the expand with no children collection at
        // all, which the old check read as "no data, don't seed" — so every empty folder paid a
        // second ~800ms round trip to be told it was still empty. It's the one case where we
        // already know the answer for certain, so cache it: if the expand ran and didn't paginate,
        // null children means zero children.
        if (item?.Folder != null && expanded &&
            item.AdditionalData?.ContainsKey("children@odata.nextLink") != true)
        {
            var children = item.Children ?? [];
            _cache.Set(ChildrenKey(driveId, path), children, PositiveTtl);
            if (prefetch) SchedulePrefetch(driveId, path, children);
        }

        return item;
    }

    // Warm the subfolders of a listing we just fetched, in the background.
    //
    // This is the one that actually attacks the FEEL of the drive. The ~800ms per folder is the
    // service's, not ours — we can't make a call faster, we can only make it happen before the
    // user asks. Someone who just opened a folder is about to double-click something in it, and
    // they spend a second or two looking at it first; that's the window we fill. When they do
    // click, the listing is already sitting in the cache.
    //
    // Deliberately one level deep and fire-and-forget: no recursion (a prefetch never schedules
    // its own prefetch), a cap on breadth, and a gate on concurrency, so opening a big folder
    // warms what's likely next instead of crawling the whole drive.
    private void SchedulePrefetch(string driveId, string path, IEnumerable<DriveItem> children)
    {
        var folders = children
            .Where(c => c.Folder != null && !string.IsNullOrEmpty(c.Name))
            .Take(PrefetchMaxFolders);

        foreach (var folder in folders)
        {
            var childPath = string.IsNullOrEmpty(path) ? folder.Name! : $"{path}/{folder.Name}";
            if (_cache.TryGetValue(ChildrenKey(driveId, childPath), out _)) continue;

            _ = Task.Run(async () =>
            {
                await PrefetchGate.WaitAsync();
                try
                {
                    // Re-check under the gate: by the time our turn came, the user may well have
                    // opened this folder themselves and paid for it already.
                    if (_cache.TryGetValue(ChildrenKey(driveId, childPath), out _)) return;
                    await Coalesce(_itemFlights, ItemKey(driveId, childPath),
                        () => FetchItemAsync(driveId, childPath, prefetch: false));
                }
                catch
                {
                    // Speculative work. If it fails the user simply pays for the folder on entry,
                    // exactly as before — there is nobody to report this to.
                }
                finally
                {
                    PrefetchGate.Release();
                }
            });
        }
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

        return await Coalesce(_childFlights, key, () => FetchChildrenAsync(driveId, path));
    }

    private async Task<List<DriveItem>> FetchChildrenAsync(string driveId, string path)
    {
        var key = ChildrenKey(driveId, path);

        DriveItemCollectionResponse? firstPage;
        try
        {
            firstPage = string.IsNullOrEmpty(path)
                ? await _client.Drives[driveId].Items[await GetRootIdAsync(driveId)].Children
                    .GetAsync(rc => rc.QueryParameters.Top = ChildrenPageSize)
                : await _client.Drives[driveId].Root.ItemWithPath(path).Children
                    .GetAsync(rc => rc.QueryParameters.Top = ChildrenPageSize);
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
        SchedulePrefetch(driveId, path, all);
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

        // If the client's stream dried up before we sent every byte Graph is expecting, the
        // upload session is now a half-written file. Do NOT let the caller PUT a 201 for that -
        // a truncated upload silently reported as success is how you lose data and trust.
        if (pos != total)
            throw new IOException(
                $"Upload truncated: sent {pos} of {total} bytes. The source stream ended early.");
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
