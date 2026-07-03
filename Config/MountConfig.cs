using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDriveAsADrive.Config;

// One mounted drive. Either your personal OneDrive, or a SharePoint document library.
// Each one becomes its own drive letter in Explorer. Peter gets a shelf for every kind of beer.
public sealed class Mount
{
    // Drive letter, e.g. "S". Also doubles as the URL path prefix (/s/...) so one server
    // can serve many drives without them stepping on each other.
    public string Letter { get; set; } = "Z";

    // "onedrive" or "sharepoint". Anything else and we assume you meant onedrive. Forgiving, like Brian.
    public string Type { get; set; } = "onedrive";

    // SharePoint only: the site address, e.g. "contoso.sharepoint.com:/sites/Finance".
    public string? Site { get; set; }

    // SharePoint only (optional): the document library name, e.g. "Documents". Omit for the
    // site's default library.
    public string? Library { get; set; }

    // Friendly label for logs / the synthetic root listing. Cosmetic.
    public string? Name { get; set; }

    // URL path this mount lives under, e.g. "/s". Lower-cased so routing is predictable.
    [JsonIgnore]
    public string Prefix => "/" + Letter.Trim().ToLowerInvariant();

    [JsonIgnore]
    public bool IsSharePoint => Type?.Equals("sharepoint", StringComparison.OrdinalIgnoreCase) == true;

    [JsonIgnore]
    public string DisplayName => Name ?? (IsSharePoint ? (Site ?? "SharePoint") : "OneDrive");
}

// The whole config. Deployed by IT to %PROGRAMDATA% (machine-wide) or dropped in
// %LOCALAPPDATA% (per-user). No config at all? We default to a single OneDrive on Z:.
public sealed class MountConfig
{
    public int Port { get; set; } = 8080;
    public List<Mount> Mounts { get; set; } = [];

    // Optional: the account (UPN / email) to sign in as, e.g. "you@contoso.com". On a machine
    // with several signed-in Microsoft accounts, this pins which identity every mount uses —
    // otherwise we grab whichever the broker cached first, which is like letting Peter pick the
    // designated driver: technically an account, rarely the one you wanted. Leave null to accept
    // the default.
    public string? Account { get; set; }

    // Where the config was loaded from (or null if we fell back to defaults). For logging.
    [JsonIgnore]
    public string? SourcePath { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // Machine-wide config IT pushes via Intune/GPO wins; then per-user; else defaults.
    public static string MachineConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OneDriveAsADrive", "config.json");

    public static string UserConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneDriveAsADrive", "config.json");

    public static MountConfig Load()
    {
        foreach (var path in new[] { MachineConfigPath, UserConfigPath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var cfg = JsonSerializer.Deserialize<MountConfig>(File.ReadAllText(path), JsonOpts);
                if (cfg is { Mounts.Count: > 0 })
                {
                    cfg.SourcePath = path;
                    cfg.Sanitize();
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                // Bad JSON shouldn't brick the whole thing. Log-and-fall-through beats a crash loop.
                Console.Error.WriteLine($"[config] Ignoring malformed {path}: {ex.Message}");
            }
        }

        // No usable config — the classic single-OneDrive-on-Z setup. Just works out of the box.
        return new MountConfig
        {
            Mounts = [new Mount { Letter = "Z", Type = "onedrive", Name = "OneDrive" }]
        };
    }

    // Drop anything obviously broken (blank/duplicate/non-letter drive letters) so routing
    // stays sane. A "letter" must be a single A–Z char — "Docs" would produce `net use DOCS:`
    // (which fails) and a /docs prefix nobody maps. Toss it and say so.
    private void Sanitize()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<Mount>();
        foreach (var m in Mounts)
        {
            var letter = m.Letter?.Trim() ?? "";
            if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
            {
                Console.Error.WriteLine($"[config] Dropping mount with invalid letter '{m.Letter}' — must be a single A–Z drive letter.");
                continue;
            }
            if (!seen.Add(letter))
            {
                Console.Error.WriteLine($"[config] Dropping duplicate mount for letter '{letter}'.");
                continue;
            }
            m.Letter = letter.ToUpperInvariant();
            kept.Add(m);
        }
        Mounts = kept;
        if (Port is <= 0 or > 65535) Port = 8080;
    }

    public bool AnySharePoint => Mounts.Any(m => m.IsSharePoint);
}
