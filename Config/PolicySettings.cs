using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace OneDriveAsADrive.Config;

// Admin policy, read from the registry. This is the ONLY thing that can overrule a user's own
// settings — and it is opt-in: an admin who deploys nothing here gets the friendly default where
// the user owns their own drives.
//
// Why the registry and not just a file in %ProgramData%: the Policies hive is ACL'd to admins by
// construction. %ProgramData%\OneDriveAsADrive\ is not — default ProgramData ACLs let a standard
// user create the subfolder first, and CREATOR OWNER then hands them control of it. A "lock" a
// user can win a race for isn't a lock. This hive is also what Intune ADMX ingestion and GPO
// write to natively, so admins target it with the tools they already have.
//
// Presence IS enforcement, the same way Chrome and Edge policies work: a value that's here is
// locked and the settings UI greys it out. A value that isn't here, the user owns.
[SupportedOSPlatform("windows")]
public sealed class PolicySettings
{
    public const string KeyPath = @"SOFTWARE\Policies\OneDriveAsADrive";

    // Null = not set by policy = the user owns this setting.
    public int? Port { get; private init; }
    public string? Account { get; private init; }
    public List<Mount>? Mounts { get; private init; }

    // Defaults are the permissive ones on purpose. No policy deployed = nothing restricted.
    public bool AllowUserMounts { get; private init; } = true;
    public bool SettingsUiDisabled { get; private init; }

    // Which hive supplied something, for the log line and the UI's "managed by" text.
    public string? Hive { get; private init; }

    public bool AnyPolicyPresent =>
        Port.HasValue || Account != null || Mounts != null || !AllowUserMounts || SettingsUiDisabled;

    private static readonly JsonSerializerOptions MountsJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    // HKLM beats HKCU, matching how every other Windows policy resolves: a machine-targeted
    // Intune profile wins over a user-targeted one. We read the user hive first and let the
    // machine hive paint over it.
    public static PolicySettings Load(Action<string>? warn = null)
    {
        var merged = new PolicySettings();
        foreach (var (hive, label) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            try
            {
                using var key = hive.OpenSubKey(KeyPath);
                if (key == null) continue;
                merged = ReadFrom(key, label, merged, warn);
            }
            catch (Exception ex)
            {
                // A broken policy key must not brick the app — it degrades to "no policy".
                warn?.Invoke($"Could not read {label}\\{KeyPath}: {ex.Message}");
            }
        }
        return merged;
    }

    private static PolicySettings ReadFrom(RegistryKey key, string label, PolicySettings fallback, Action<string>? warn)
    {
        List<Mount>? mounts = fallback.Mounts;
        if (key.GetValue("Mounts") is string raw && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<Mount>>(raw, MountsJson);
                // An empty array is a legitimate policy ("this machine gets no drives"), but
                // unparseable JSON is a mistake — say so rather than silently enforcing nothing.
                if (parsed != null) mounts = parsed;
            }
            catch (JsonException ex)
            {
                warn?.Invoke($"{label} policy 'Mounts' is not valid JSON and was ignored: {ex.Message}");
            }
        }

        return new PolicySettings
        {
            Port = ReadDword(key, "Port") ?? fallback.Port,
            Account = key.GetValue("Account") as string is { Length: > 0 } acct ? acct : fallback.Account,
            Mounts = mounts,
            AllowUserMounts = ReadBool(key, "AllowUserMounts") ?? fallback.AllowUserMounts,
            SettingsUiDisabled = ReadBool(key, "DisableSettingsUi") ?? fallback.SettingsUiDisabled,
            Hive = label
        };
    }

    private static int? ReadDword(RegistryKey key, string name) =>
        key.GetValue(name) switch
        {
            int i => i,
            // REG_SZ holding a number: admins hand-editing .reg files do this constantly.
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };

    private static bool? ReadBool(RegistryKey key, string name) =>
        ReadDword(key, name) switch { null => null, 0 => false, _ => true };
}
