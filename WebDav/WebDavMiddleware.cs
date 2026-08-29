using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Xml.Linq;
using Microsoft.Graph.Models;
using OneDriveAsADrive.Auth;
using OneDriveAsADrive.Config;
using OneDriveAsADrive.Graph;

namespace OneDriveAsADrive.WebDav;

// This middleware is the whole show. Windows asks for files, we translate that
// into Graph API calls and lie to Windows about having a real drive.
// Like Peter telling Lois he's on a diet. Technically not wrong.
//
// Multi-mount edition: each configured drive lives under its own URL prefix (/o, /s, ...).
// The first path segment picks the drive; everything after it is the path WITHIN that drive.
// One server, many drive letters. Quagmire juggling phone numbers.
#pragma warning disable CS9113
public class WebDavMiddleware(
    RequestDelegate next,
    MountConfig config,
    DriveResolver resolver,
    OneDriveProvider drive,
    ServerSecret secret,
    ILogger<WebDavMiddleware> log)
#pragma warning restore CS9113
{
    // "DAV:" namespace. The holy namespace. Bow before it. ROADHOUSE.
    private static readonly XNamespace Dav = "DAV:";

    public async Task InvokeAsync(HttpContext ctx)
    {
        // SECURITY #0: the packet must physically come from this machine. The Host check
        // below stops DNS rebinding, but if someone launches with --urls http://*:40323 we're
        // reachable off-box, and a remote client can just send "Host: localhost" to sail past
        // it. So first: if the remote IP isn't loopback, the door doesn't even open. Belt and
        // suspenders, because the thing behind it holds your Graph token.
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote != null && !System.Net.IPAddress.IsLoopback(remote))
        {
            log.LogWarning("Rejected non-loopback client {Ip}", remote);
            ctx.Response.StatusCode = 403;
            return;
        }

        // SECURITY #1: only answer to loopback host names. A malicious website can point
        // its DNS at 127.0.0.1 (DNS rebinding) and try to read your files through the
        // browser — but its requests carry ITS domain in the Host header, not "localhost".
        // So we slam the door on anything that isn't a genuine loopback name.
        // No cutaway gag here, this one's actually serious.
        if (!IsLoopbackHost(ctx.Request.Host.Host))
        {
            log.LogWarning("Rejected request with non-loopback Host: {Host}", ctx.Request.Host.Value);
            ctx.Response.StatusCode = 403;
            return;
        }

        // SECURITY #2: require the per-install secret via HTTP Basic auth. Binding to
        // localhost does NOT stop other local users or low-priv malware from hitting the
        // port — this does. Windows sends the creds from the `net use` mapping. Anyone
        // without the secret gets a 401 and a door in the face.
        if (!IsAuthorized(ctx))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"OneDriveAsADrive\", charset=\"UTF-8\"";
            return;
        }

        var method = ctx.Request.Method.ToUpperInvariant();
        var fullPath = HttpUtility.UrlDecode(ctx.Request.Path.Value ?? "/");

        log.LogDebug("{Method} {Path} — somebody's poking around", method, fullPath);

        // Figure out which mounted drive this request is for. The synthetic root ("/") isn't
        // a real drive — it's just a lobby that lists the drives (handy when an admin browses
        // http://localhost:PORT/ to debug).
        var (mount, relPath) = ResolveMount(fullPath);

        try
        {
            if (mount == null)
            {
                await HandleRoot(ctx, method, fullPath);
                return;
            }

            var driveId = await resolver.ResolveDriveIdAsync(mount);

            switch (method)
            {
                case "OPTIONS":  await HandleOptions(ctx); break;
                case "PROPFIND": await HandlePropfind(ctx, driveId, fullPath, relPath); break;
                case "PROPPATCH":await HandleProppatch(ctx, fullPath); break;
                case "GET":
                case "HEAD":     await HandleGet(ctx, driveId, relPath, method == "HEAD"); break;
                case "PUT":      await HandlePut(ctx, driveId, relPath); break;
                case "DELETE":   await HandleDelete(ctx, driveId, relPath); break;
                case "MKCOL":    await HandleMkcol(ctx, driveId, relPath); break;
                case "MOVE":     await HandleMove(ctx, driveId, mount, relPath); break;
                case "LOCK":     await HandleLock(ctx, fullPath); break;
                case "UNLOCK":   ctx.Response.StatusCode = 204; break;
                default:
                    // "What the deuce is a REPORT request doing here?"
                    ctx.Response.StatusCode = 405;
                    break;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Holy crap — unhandled error on {Method} {Path}", method, fullPath);
            ctx.Response.StatusCode = 500;
        }
    }

    // Split "/s/Documents/file.txt" into (mount for "s", "/Documents/file.txt").
    // Returns (null, path) when nothing matches — that's the synthetic root.
    private (Mount? mount, string relPath) ResolveMount(string fullPath)
    {
        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return (null, "/");

        var mount = config.Mounts.FirstOrDefault(m =>
            m.Letter.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
        if (mount == null) return (null, fullPath);

        var rel = "/" + string.Join("/", segments.Skip(1));
        return (mount, rel);
    }

    // The synthetic root at "/". Not a drive — a directory of drives. Only PROPFIND/OPTIONS
    // make sense here; anything trying to read/write "/" gets a polite 404. Windows never
    // maps a drive to "/" itself, so real users won't hit this; it's an admin debug view.
    private async Task HandleRoot(HttpContext ctx, string method, string fullPath)
    {
        // Only the bare root is special. "/nonsense" that matched no mount is just a 404.
        var isBareRoot = fullPath.Trim('/').Length == 0;

        if (method == "OPTIONS") { await HandleOptions(ctx); return; }

        if (method == "PROPFIND" && isBareRoot)
        {
            var responses = new List<XElement>
            {
                // the root collection itself
                CollectionResponse("/", "OneDriveAsADrive")
            };
            responses.AddRange(config.Mounts.Select(m =>
                CollectionResponse(m.Prefix + "/", m.DisplayName)));

            var multistatus = new XDocument(
                new XElement(Dav + "multistatus",
                    new XAttribute(XNamespace.Xmlns + "D", Dav),
                    responses));

            ctx.Response.StatusCode = 207;
            ctx.Response.ContentType = "application/xml; charset=utf-8";
            await ctx.Response.WriteAsync(multistatus.ToString(), Encoding.UTF8);
            return;
        }

        ctx.Response.StatusCode = 404;
    }

    // A minimal <response> saying "this href is a folder." Used for the synthetic root listing.
    private XElement CollectionResponse(string href, string name) =>
        new(Dav + "response",
            new XElement(Dav + "href", EncodePath(href)),
            new XElement(Dav + "propstat",
                new XElement(Dav + "prop",
                    new XElement(Dav + "displayname", name),
                    new XElement(Dav + "resourcetype", new XElement(Dav + "collection"))),
                new XElement(Dav + "status", "HTTP/1.1 200 OK")));

    // localhost / 127.0.0.1 / ::1 only. Everything else can take a long walk off Quahog's pier.
    private static bool IsLoopbackHost(string host) => LoopbackGuard.IsLoopbackHost(host);

    // Validate HTTP Basic auth against the per-install secret. Constant-time compare so
    // we don't leak the secret one byte at a time via timing. Stewie would try that.
    private bool IsAuthorized(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (header == null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())); }
        catch { return false; }

        var sep = decoded.IndexOf(':');
        if (sep < 0) return false;

        var user = decoded[..sep];
        var pass = decoded[(sep + 1)..];

        if (!string.Equals(user, secret.Username, StringComparison.Ordinal))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(pass),
            Encoding.UTF8.GetBytes(secret.Value));
    }

    // OPTIONS — tell Windows what we can do.
    // Like Quagmire's dating profile. Lists everything. Giggity.
    private static Task HandleOptions(HttpContext ctx)
    {
        // Advertise class 2 (locking). We fake locks, but Word/Excel demand the option
        // exist or they refuse to save. Sometimes you gotta tell people what they wanna hear.
        ctx.Response.Headers["DAV"] = "1,2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        ctx.Response.Headers["Allow"] =
            "OPTIONS,GET,HEAD,PUT,DELETE,PROPFIND,PROPPATCH,MKCOL,MOVE,LOCK,UNLOCK";
        ctx.Response.StatusCode = 200;
        return Task.CompletedTask;
    }

    // PROPFIND — the most chatty WebDAV method. Windows asks about every single thing.
    // "Hey what's this? Hey what about this? Hey what's in here?"
    // Like Chris when he finds a new game.
    private async Task HandlePropfind(HttpContext ctx, string driveId, string fullPath, string relPath)
    {
        var depth = ctx.Request.Headers["Depth"].FirstOrDefault() ?? "1";

        // Depth: infinity would mean "walk the ENTIRE drive recursively." Absolutely not.
        // That's a denial-of-service waiting to happen. Spec says answer 403. So we do.
        if (depth.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = "application/xml; charset=utf-8";
            await ctx.Response.WriteAsync(new XDocument(
                new XElement(Dav + "error",
                    new XAttribute(XNamespace.Xmlns + "D", Dav),
                    new XElement(Dav + "propfind-finite-depth"))).ToString());
            return;
        }

        var item = await drive.GetItemAsync(driveId, relPath);
        if (item == null)
        {
            // "Meg, you're a 404." — Every Griffin, probably.
            ctx.Response.StatusCode = 404;
            return;
        }

        var responses = new List<XElement> { BuildResponse(fullPath, item) };

        if (depth != "0" && item.Folder != null)
        {
            var children = await drive.GetChildrenAsync(driveId, relPath);
            foreach (var child in children)
            {
                var childPath = fullPath.TrimEnd('/') + "/" + child.Name;
                responses.Add(BuildResponse(childPath, child));
            }
        }

        // Wrap everything in a multistatus envelope. XML is fun. Said no one ever.
        var multistatus = new XDocument(
            new XElement(Dav + "multistatus",
                new XAttribute(XNamespace.Xmlns + "D", Dav),
                responses));

        ctx.Response.StatusCode = 207; // 207 Multi-Status — the most dramatic HTTP code
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.WriteAsync(multistatus.ToString(), Encoding.UTF8);
    }

    // Build one <response> element for a drive item.
    // "I am Stewie Griffin and I have CRAFTED this XML response PERFECTLY."
    private XElement BuildResponse(string path, DriveItem item)
    {
        var isFolder = item.Folder != null;
        // Folders get exactly one trailing slash. The mapped root already arrives as "/z/",
        // so blindly appending would emit "/z//" and some clients choke on the double slash.
        // Trim first, then add the one we want.
        var encoded = EncodePath(path);
        var href = isFolder ? encoded.TrimEnd('/') + "/" : encoded;

        var props = new List<XElement>
        {
            new(Dav + "displayname", item.Name ?? ""),
            // folders get <collection/>, files get nothing. The eternal struggle.
            new(Dav + "resourcetype", isFolder ? new XElement(Dav + "collection") : null!),
            new(Dav + "getlastmodified",
                item.LastModifiedDateTime?.UtcDateTime.ToString("R") ?? DateTime.UtcNow.ToString("R")),
            new(Dav + "creationdate",
                item.CreatedDateTime?.UtcDateTime.ToString("o") ?? DateTime.UtcNow.ToString("o")),
        };

        if (!isFolder)
        {
            props.Add(new XElement(Dav + "getcontentlength", item.Size?.ToString() ?? "0"));
            props.Add(new XElement(Dav + "getcontenttype",
                item.File?.MimeType ?? "application/octet-stream"));
        }

        if (item.ETag != null)
            props.Add(new XElement(Dav + "getetag", item.ETag));

        return new XElement(Dav + "response",
            new XElement(Dav + "href", href),
            new XElement(Dav + "propstat",
                new XElement(Dav + "prop", props.Where(p => p != null)),
                new XElement(Dav + "status", "HTTP/1.1 200 OK")));
    }

    // PROPPATCH — clients try to SET properties (usually timestamps on copy/save).
    // Graph doesn't let us persist arbitrary WebDAV props, but if we 405 this, Explorer
    // and Office throw a fit mid-copy. So we nod politely and report success. Roadhouse.
    private async Task HandleProppatch(HttpContext ctx, string fullPath)
    {
        var multistatus = new XDocument(
            new XElement(Dav + "multistatus",
                new XAttribute(XNamespace.Xmlns + "D", Dav),
                new XElement(Dav + "response",
                    new XElement(Dav + "href", EncodePath(fullPath)),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "prop"),
                        new XElement(Dav + "status", "HTTP/1.1 200 OK")))));

        ctx.Response.StatusCode = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.WriteAsync(multistatus.ToString(), Encoding.UTF8);
    }

    // LOCK — we don't really lock anything (single user, single app), but Word/Excel
    // won't save unless they get a lock token back. So we hand out a fake one and
    // everybody's happy. It's like Peter's "World's Best Dad" mug. Purely ceremonial.
    private async Task HandleLock(HttpContext ctx, string fullPath)
    {
        var token = "opaquelocktoken:" + Guid.NewGuid();

        var body = new XDocument(
            new XElement(Dav + "prop",
                new XAttribute(XNamespace.Xmlns + "D", Dav),
                new XElement(Dav + "lockdiscovery",
                    new XElement(Dav + "activelock",
                        new XElement(Dav + "locktype", new XElement(Dav + "write")),
                        new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
                        new XElement(Dav + "depth", "0"),
                        new XElement(Dav + "timeout", "Second-3600"),
                        new XElement(Dav + "locktoken",
                            new XElement(Dav + "href", token))))));

        ctx.Response.Headers["Lock-Token"] = $"<{token}>";
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.WriteAsync(body.ToString(), Encoding.UTF8);
    }

    // GET / HEAD — serve the file. Supports Range so media players and big-file reads
    // don't have to swallow the whole thing at once. Brian orders a la carte now.
    private async Task HandleGet(HttpContext ctx, string driveId, string relPath, bool headOnly)
    {
        var item = await drive.GetItemAsync(driveId, relPath);
        if (item == null) { ctx.Response.StatusCode = 404; return; }
        if (item.Folder != null) { ctx.Response.StatusCode = 400; return; }

        // HEAD: metadata only, no body. Advertise that we accept ranges.
        if (headOnly)
        {
            ctx.Response.Headers["Content-Length"] = item.Size?.ToString() ?? "0";
            ctx.Response.Headers["Content-Type"] = item.File?.MimeType ?? "application/octet-stream";
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.Headers["Last-Modified"] = item.LastModifiedDateTime?.UtcDateTime.ToString("R") ?? "";
            if (item.ETag != null) ctx.Response.Headers["ETag"] = item.ETag;
            ctx.Response.StatusCode = 200;
            return;
        }

        // Stream from OneDrive's pre-authenticated download URL, forwarding any Range
        // header. Upstream (a CDN) sets the real Content-Length / Content-Range / status,
        // so we relay those verbatim — no more guessing the length and hanging Explorer.
        var range = ctx.Request.Headers["Range"].FirstOrDefault();
        using var upstream = await drive.DownloadAsync(driveId, relPath, range);

        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        if (upstream.Content.Headers.ContentLength is long len)
            ctx.Response.Headers["Content-Length"] = len.ToString();
        if (upstream.Content.Headers.ContentType is { } ct)
            ctx.Response.Headers["Content-Type"] = ct.ToString();
        if (upstream.Content.Headers.ContentRange is { } cr)
            ctx.Response.Headers["Content-Range"] = cr.ToString();

        await using var body = await upstream.Content.ReadAsStreamAsync();
        await body.CopyToAsync(ctx.Response.Body);
    }

    // PUT — upload a file. 201 if it's brand new, 204 if we overwrote an existing one.
    // WebDAV cares about the difference even if Windows shrugs. Do it right.
    private async Task HandlePut(HttpContext ctx, string driveId, string relPath)
    {
        var existed = await drive.GetItemAsync(driveId, relPath) != null;
        await drive.UploadAsync(driveId, relPath, ctx.Request.Body, ctx.Request.ContentLength);
        ctx.Response.StatusCode = existed ? 204 : 201;
    }

    // DELETE — ROADHOUSE.
    private async Task HandleDelete(HttpContext ctx, string driveId, string relPath)
    {
        await drive.DeleteAsync(driveId, relPath);
        ctx.Response.StatusCode = 204;
    }

    // MKCOL — make a collection (folder). "Victory is mine! A new folder!"
    private async Task HandleMkcol(HttpContext ctx, string driveId, string relPath)
    {
        await drive.CreateFolderAsync(driveId, relPath);
        ctx.Response.StatusCode = 201;
    }

    // MOVE — rename or move. Windows sends the full destination URL in a header.
    // Because WebDAV designers said "why use the path when you can use an entire URL?" Guys, come on.
    private async Task HandleMove(HttpContext ctx, string driveId, Mount mount, string relPath)
    {
        var dest = ctx.Request.Headers["Destination"].FirstOrDefault();
        if (string.IsNullOrEmpty(dest)) { ctx.Response.StatusCode = 400; return; }

        // Strip the "http://localhost:40323" prefix, keep just the path part.
        var destFull = HttpUtility.UrlDecode(new Uri(dest).AbsolutePath);
        var (destMount, destRel) = ResolveMount(destFull);

        // Cross-drive moves (dragging from S: to O:) can't be a single Graph PATCH — those
        // live on different drives entirely. Tell the client we can't, rather than corrupting.
        // 502 Bad Gateway is WebDAV's "the destination is on a different server" answer.
        if (destMount == null || !destMount.Letter.Equals(mount.Letter, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 502;
            return;
        }

        await drive.MoveAsync(driveId, relPath, destRel);
        ctx.Response.StatusCode = 201;
    }

    // URL-encode each path segment individually.
    // Spaces in filenames would break everything otherwise. Like Meg at prom.
    private static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(HttpUtility.UrlPathEncode));
}
