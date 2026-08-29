using System.Runtime.Versioning;
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

// Where an effective setting actually came from. The settings UI shows this, which is the whole
// point — a user who can't change the port deserves to be told it's their admin's doing and not
// a bug.
public enum SettingSource
{
    Default,   // nothing configured anywhere; built-in fallback
    Machine,   // %ProgramData% config.json — the admin's deployed starting point
    User,      // %LOCALAPPDATA% config.json — what the user chose in the UI
    Policy     // the Policies registry hive — admin enforcement, user can't touch it
}

// The on-disk shape of a config.json. Every field is nullable so we can tell "the user set the
// port to 40323" apart from "the user never mentioned the port" — which is the entire basis of
// the per-field merge below. The old code couldn't make that distinction, which is why it had to
// take one file wholesale and ignore the other.
internal sealed class ConfigFile
{
    public int? Port { get; set; }
    public List<Mount>? Mounts { get; set; }
    public string? Account { get; set; }

    // Machine config only. Default (absent/true) means the deployed config is a STARTING POINT
    // the user may change. Set it to false and the machine config becomes enforcement, for admins
    // who deploy files but no registry policy.
    public bool? AllowUserOverride { get; set; }
}

// The effective, merged config the rest of the app runs on.
//
// Resolution order, per field, highest first:
//   1. Policy registry hive     — locked, user cannot override
//   2. %LOCALAPPDATA% config    — the user's own choice
//   3. %ProgramData% config     — the admin's deployed default (a seed, not a leash)
//   4. Built-in defaults        — single OneDrive on Z:, port 40323
//
// Note this INVERTS the old behaviour, where a machine config beat a user config wholesale. The
// friendly case is now the default: users manage their own drives, and an admin who wants that
// locked down opts in explicitly (registry policy, or allowUserOverride:false).
public sealed class MountConfig
{
    // 40323, deliberately obscure. The reflexive 8080 is a warzone — Tomcat, Jenkins, dev
    // servers and proxies all squat on it, so it collides constantly. A quiet high port in
    // the registered range (nothing hands out 40323) means the drive just works out of the box.
    public const int DefaultPort = 40323;

    public int Port { get; set; } = DefaultPort;
    public List<Mount> Mounts { get; set; } = [];

    // Optional: the account (UPN / email) to sign in as, e.g. "you@contoso.com". On a machine
    // with several signed-in Microsoft accounts, this pins which identity every mount uses —
    // otherwise we grab whichever the broker cached first, which is like letting Peter pick the
    // designated driver: technically an account, rarely the one you wanted. Leave null to accept
    // the default.
    public string? Account { get; set; }

    // ── Provenance ────────────────────────────────────────────────────────────────
    // Which layer won each field, and whether the user is allowed to change it.
    [JsonIgnore] public SettingSource PortSource { get; private set; } = SettingSource.Default;
    [JsonIgnore] public SettingSource AccountSource { get; private set; } = SettingSource.Default;
    [JsonIgnore] public SettingSource MountsSource { get; private set; } = SettingSource.Default;
    [JsonIgnore] public bool PortLocked { get; private set; }
    [JsonIgnore] public bool AccountLocked { get; private set; }
    [JsonIgnore] public bool MountsLocked { get; private set; }

    // Admin kill switch for the settings UI. Off by default — the UI is the friendly path.
    [JsonIgnore] public bool SettingsUiDisabled { get; private set; }

    // True when anything at all is being enforced, so the UI can show one honest banner.
    [JsonIgnore] public bool IsManaged => PortLocked || AccountLocked || MountsLocked || SettingsUiDisabled;

    // Where the config was loaded from (or null if we fell back to defaults). For logging.
    [JsonIgnore] public string? SourcePath { get; set; }

    // Non-fatal complaints gathered during Load(), surfaced by Program.cs once the logger exists.
    // Load() runs before the host is built, so it can't log for itself.
    [JsonIgnore] public List<string> LoadWarnings { get; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string MachineConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OneDriveAsADrive", "config.json");

    public static string UserConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneDriveAsADrive", "config.json");

    [SupportedOSPlatform("windows")]
    public static MountConfig Load()
    {
        var cfg = new MountConfig();

        var machine = ReadFile(MachineConfigPath, cfg.LoadWarnings);
        var user = ReadFile(UserConfigPath, cfg.LoadWarnings);
        var policy = PolicySettings.Load(w => cfg.LoadWarnings.Add($"[policy] {w}"));

        // An admin who deployed a machine config and ticked allowUserOverride:false gets the old
        // wholesale-enforcement behaviour without needing to touch the registry.
        var machineLocks = machine?.AllowUserOverride == false;

        var (port, portSrc, portLocked) =
            Resolve(policy.Port, user?.Port, machine?.Port, DefaultPort, machineLocks);
        var (account, acctSrc, acctLocked) =
            Resolve(policy.Account, Blank(user?.Account), Blank(machine?.Account), null, machineLocks);
        var (mounts, mountsSrc, mountsLocked) =
            Resolve(policy.Mounts, user?.Mounts, machine?.Mounts, null, machineLocks);

        cfg.Port = port;
        cfg.PortSource = portSrc;
        cfg.PortLocked = portLocked;

        cfg.Account = account;
        cfg.AccountSource = acctSrc;
        cfg.AccountLocked = acctLocked;

        cfg.Mounts = mounts ?? [new Mount { Letter = "Z", Type = "onedrive", Name = "OneDrive" }];
        cfg.MountsSource = mounts == null ? SettingSource.Default : mountsSrc;
        // AllowUserMounts=0 locks the mount list even when the admin didn't pin a specific list —
        // "keep whatever you've got, but you may not add or remove drives".
        cfg.MountsLocked = mountsLocked || !policy.AllowUserMounts;

        cfg.SettingsUiDisabled = policy.SettingsUiDisabled;

        cfg.SourcePath = DescribeSources(machine != null, user != null, policy.AnyPolicyPresent, policy.Hive);
        cfg.Sanitize();
        return cfg;
    }

    // The whole merge in one place. A machine value is a seed used only when the user hasn't
    // spoken; policy (or an explicitly locking machine config) short-circuits ahead of both.
    private static (T? Value, SettingSource Source, bool Locked) Resolve<T>(
        T? policy, T? user, T? machine, T? fallback, bool machineLocks) where T : class
    {
        if (policy != null) return (policy, SettingSource.Policy, true);
        if (machineLocks && machine != null) return (machine, SettingSource.Machine, true);
        if (user != null) return (user, SettingSource.User, false);
        if (machine != null) return (machine, SettingSource.Machine, false);
        return (fallback, SettingSource.Default, false);
    }

    private static (int Value, SettingSource Source, bool Locked) Resolve(
        int? policy, int? user, int? machine, int fallback, bool machineLocks)
    {
        if (policy.HasValue) return (policy.Value, SettingSource.Policy, true);
        if (machineLocks && machine.HasValue) return (machine.Value, SettingSource.Machine, true);
        if (user.HasValue) return (user.Value, SettingSource.User, false);
        if (machine.HasValue) return (machine.Value, SettingSource.Machine, false);
        return (fallback, SettingSource.Default, false);
    }

    // "" and "   " in a config file mean "not set", not "set to empty". Otherwise a user who
    // clears the account box would out-rank the admin's deployed value with nothing at all.
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static ConfigFile? ReadFile(string path, List<string> warnings)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            // Bad JSON shouldn't brick the whole thing. Log-and-fall-through beats a crash loop.
            warnings.Add($"Ignoring malformed {path}: {ex.Message}");
            return null;
        }
    }

    private static string DescribeSources(bool machine, bool user, bool policy, string? hive)
    {
        var parts = new List<string>();
        if (policy) parts.Add($"policy ({hive})");
        if (user) parts.Add("user config");
        if (machine) parts.Add("machine config");
        return parts.Count == 0 ? "defaults (single OneDrive on Z:)" : string.Join(" + ", parts);
    }

    // Persist the user's own layer. Only ever writes %LOCALAPPDATA% — policy and the machine
    // config are not ours to edit, and the settings API refuses locked fields before it gets here.
    public static void SaveUser(int? port, string? account, List<Mount>? mounts)
    {
        var path = UserConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new ConfigFile { Port = port, Account = Blank(account), Mounts = mounts };

        // Write-then-replace so a crash mid-write can't leave a truncated config that the next
        // start would refuse to parse.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, WriteOpts));
        File.Move(tmp, path, overwrite: true);
    }

    // Drop anything obviously broken (blank/duplicate/non-letter drive letters) so routing
    // stays sane. A "letter" must be a single A–Z char — "Docs" would produce `net use DOCS:`
    // (which fails) and a /docs prefix nobody maps. Toss it and say so.
    private void Sanitize()
    {
        Mounts = SanitizeMounts(Mounts, LoadWarnings);
        if (Port is <= 0 or > 65535)
        {
            LoadWarnings.Add($"Port {Port} is out of range — falling back to {DefaultPort}.");
            Port = DefaultPort;
        }
    }

    // Shared with the settings API so the UI rejects a bad drive letter with a real message
    // instead of silently dropping it on the next start.
    public static List<Mount> SanitizeMounts(List<Mount> mounts, List<string> warnings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<Mount>();
        foreach (var m in mounts)
        {
            var letter = m.Letter?.Trim() ?? "";
            if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
            {
                warnings.Add($"Dropping mount with invalid letter '{m.Letter}' — must be a single A–Z drive letter.");
                continue;
            }
            if (!seen.Add(letter))
            {
                warnings.Add($"Dropping duplicate mount for letter '{letter}'.");
                continue;
            }
            if (m.IsSharePoint && string.IsNullOrWhiteSpace(m.Site))
            {
                warnings.Add($"Dropping SharePoint mount {letter}: — it has no site address.");
                continue;
            }
            m.Letter = letter.ToUpperInvariant();
            kept.Add(m);
        }
        return kept;
    }

    public bool AnySharePoint => Mounts.Any(m => m.IsSharePoint);
}
