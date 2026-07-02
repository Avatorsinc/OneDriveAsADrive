using System.Text;
using System.Web;
using System.Xml.Linq;
using Microsoft.Graph.Models;
using OneDriveAsADrive.Graph;

namespace OneDriveAsADrive.WebDav;

// This middleware is the whole show. Windows asks for files, we translate that
// into Graph API calls and lie to Windows about having a real drive.
// Like Peter telling Lois he's on a diet. Technically not wrong.
#pragma warning disable CS9113
public class WebDavMiddleware(RequestDelegate next, OneDriveProvider drive, ILogger<WebDavMiddleware> log)
#pragma warning restore CS9113
{
    // "DAV:" namespace. The holy namespace. Bow before it. ROADHOUSE.
    private static readonly XNamespace Dav = "DAV:";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method.ToUpperInvariant();
        var path = HttpUtility.UrlDecode(ctx.Request.Path.Value ?? "/");

        log.LogDebug("{Method} {Path} — somebody's poking around", method, path);

        try
        {
            switch (method)
            {
                case "OPTIONS":  await HandleOptions(ctx); break;
                case "PROPFIND": await HandlePropfind(ctx, path); break;
                case "GET":
                case "HEAD":     await HandleGet(ctx, path, method == "HEAD"); break;
                case "PUT":      await HandlePut(ctx, path); break;
                case "DELETE":   await HandleDelete(ctx, path); break;
                case "MKCOL":    await HandleMkcol(ctx, path); break;
                case "MOVE":     await HandleMove(ctx, path); break;
                default:
                    // "What the deuce is a LOCK request doing here?"
                    ctx.Response.StatusCode = 405;
                    break;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Holy crap — unhandled error on {Method} {Path}", method, path);
            ctx.Response.StatusCode = 500;
        }
    }

    // OPTIONS — tell Windows what we can do.
    // Like Quagmire's dating profile. Lists everything. Giggity.
    private static Task HandleOptions(HttpContext ctx)
    {
        ctx.Response.Headers["DAV"] = "1,2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        ctx.Response.Headers["Allow"] = "OPTIONS,GET,HEAD,PUT,DELETE,PROPFIND,MKCOL,MOVE";
        ctx.Response.StatusCode = 200;
        return Task.CompletedTask;
    }

    // PROPFIND — the most chatty WebDAV method. Windows asks about every single thing.
    // "Hey what's this? Hey what about this? Hey what's in here?"
    // Like Chris when he finds a new game.
    private async Task HandlePropfind(HttpContext ctx, string path)
    {
        // Depth: 0 = just this item. Depth: 1 = item + its children. infinity = rejected, we're not animals.
        var depth = ctx.Request.Headers["Depth"].FirstOrDefault() ?? "1";

        var item = await drive.GetItemAsync(path);
        if (item == null)
        {
            // "Meg, you're a 404." — Every Griffin, probably.
            ctx.Response.StatusCode = 404;
            return;
        }

        var responses = new List<XElement> { BuildResponse(path, item) };

        if (depth != "0" && item.Folder != null)
        {
            var children = await drive.GetChildrenAsync(path);
            foreach (var child in children)
            {
                var childPath = path.TrimEnd('/') + "/" + child.Name;
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
        var href = EncodePath(path) + (isFolder ? "/" : "");

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

    // GET / HEAD — serve the file. Or just the headers. Brian orders a full meal and only eats the garnish.
    private async Task HandleGet(HttpContext ctx, string path, bool headOnly)
    {
        var item = await drive.GetItemAsync(path);
        if (item == null) { ctx.Response.StatusCode = 404; return; }
        if (item.Folder != null) { ctx.Response.StatusCode = 400; return; }

        ctx.Response.Headers["Content-Length"] = item.Size?.ToString() ?? "0";
        ctx.Response.Headers["Content-Type"] = item.File?.MimeType ?? "application/octet-stream";
        ctx.Response.Headers["Last-Modified"] = item.LastModifiedDateTime?.UtcDateTime.ToString("R") ?? "";
        if (item.ETag != null) ctx.Response.Headers["ETag"] = item.ETag;

        ctx.Response.StatusCode = 200;
        if (headOnly) return; // HEAD says "I only want the metadata." Very Stewie of you.

        var stream = await drive.GetContentAsync(path);
        if (stream != null)
            await stream.CopyToAsync(ctx.Response.Body);
    }

    // PUT — upload a file. Like Peter getting a new toy. Freakin' sweet.
    private async Task HandlePut(HttpContext ctx, string path)
    {
        await drive.UploadAsync(path, ctx.Request.Body);
        ctx.Response.StatusCode = 201;
    }

    // DELETE — ROADHOUSE.
    private async Task HandleDelete(HttpContext ctx, string path)
    {
        await drive.DeleteAsync(path);
        ctx.Response.StatusCode = 204;
    }

    // MKCOL — make a collection (folder). "Victory is mine! A new folder!"
    private async Task HandleMkcol(HttpContext ctx, string path)
    {
        await drive.CreateFolderAsync(path);
        ctx.Response.StatusCode = 201;
    }

    // MOVE — rename or move. Windows sends the full destination URL in a header.
    // Because WebDAV designers said "why use the path when you can use an entire URL?" Guys, come on.
    private async Task HandleMove(HttpContext ctx, string path)
    {
        var dest = ctx.Request.Headers["Destination"].FirstOrDefault();
        if (string.IsNullOrEmpty(dest)) { ctx.Response.StatusCode = 400; return; }

        // Strip the "http://localhost:8080" prefix, keep just the path part
        var destPath = HttpUtility.UrlDecode(new Uri(dest).AbsolutePath);

        await drive.MoveAsync(path, destPath);
        ctx.Response.StatusCode = 201;
    }

    // URL-encode each path segment individually.
    // Spaces in filenames would break everything otherwise. Like Meg at prom.
    private static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(HttpUtility.UrlPathEncode));
}
