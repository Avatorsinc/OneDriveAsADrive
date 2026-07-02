using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using OneDriveAsADrive.Auth;

namespace OneDriveAsADrive.Graph;

// This whole class is basically Peter trying to find where he put his files.
// Except it actually works. Unlike Peter.
public class OneDriveProvider
{
    private readonly GraphServiceClient _client;
    private string? _driveId;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public OneDriveProvider(TokenManager tokenManager)
    {
        var authProvider = new BaseBearerTokenAuthenticationProvider(
            new MsalTokenProvider(tokenManager));
        _client = new GraphServiceClient(authProvider);
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

    // Get metadata for any path. Root ("/") or file/folder.
    // "Giggity giggity — I'll take that DriveItem."
    public async Task<DriveItem?> GetItemAsync(string davPath)
    {
        var driveId = await GetDriveIdAsync();
        var path = ToGraphPath(davPath);
        return string.IsNullOrEmpty(path)
            ? await _client.Drives[driveId].Root.GetAsync()
            : await _client.Drives[driveId].Root.ItemWithPath(path).GetAsync();
    }

    // List folder contents. Depth: 1. Like Chris looking in the fridge — only one level deep.
    public async Task<List<DriveItem>> GetChildrenAsync(string davPath)
    {
        var driveId = await GetDriveIdAsync();
        var path = ToGraphPath(davPath);

        DriveItemCollectionResponse? resp;
        if (string.IsNullOrEmpty(path))
        {
            // Root children need the actual root item ID.
            // Microsoft in v6: "Root? Never heard of her." — Roadhouse.
            var root = await _client.Drives[driveId].Root.GetAsync();
            resp = await _client.Drives[driveId].Items[root!.Id!].Children.GetAsync();
        }
        else
        {
            resp = await _client.Drives[driveId].Root.ItemWithPath(path).Children.GetAsync();
        }

        return resp?.Value ?? [];
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
    }

    // Delete. Permanent. Gone. Like Meg's self-esteem.
    public async Task DeleteAsync(string davPath)
    {
        var driveId = await GetDriveIdAsync();
        var path = ToGraphPath(davPath);
        await _client.Drives[driveId].Root.ItemWithPath(path).DeleteAsync();
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
        {
            var root = await _client.Drives[driveId].Root.GetAsync();
            await _client.Drives[driveId].Items[root!.Id!].Children.PostAsync(newFolder);
        }
        else
        {
            await _client.Drives[driveId].Root.ItemWithPath(parentPath).Children.PostAsync(newFolder);
        }
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
    }

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
