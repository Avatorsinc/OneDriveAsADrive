using System.Security.Cryptography;

namespace OneDriveAsADrive.Auth;

// A per-install random password. Because binding to localhost is NOT a security boundary —
// any other user or process on the machine can also reach localhost:8080. Without this,
// the server is basically Peter leaving the front door open with a sign that says "TV inside."
//
// The secret lives in %LOCALAPPDATA%\OneDriveAsADrive\.secret — readable only by the current
// user's profile. Windows' WebDAV client sends it as HTTP Basic auth (which is why the
// installer flips BasicAuthLevel=2). Over loopback that's fine; nobody's sniffing 127.0.0.1.
public sealed class ServerSecret
{
    // Fixed username — the secret is the password. Windows doesn't care about the name.
    public string Username => "onedrive";
    public string Value { get; }
    public string FilePath { get; }

    public ServerSecret()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneDriveAsADrive");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, ".secret");

        if (File.Exists(FilePath))
        {
            // Reuse the existing secret so a restart doesn't break your mapped drive.
            // Consistency. Something Peter's never had.
            Value = File.ReadAllText(FilePath).Trim();
            if (!string.IsNullOrWhiteSpace(Value)) return;
        }

        Value = Generate();
        File.WriteAllText(FilePath, Value);
    }

    // 32 bytes of cryptographic randomness, URL-safe so it plays nice in a net use command.
    private static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
