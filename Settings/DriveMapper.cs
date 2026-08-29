using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OneDriveAsADrive.Auth;
using OneDriveAsADrive.Config;

namespace OneDriveAsADrive.Settings;

// Turns a saved config into actual drive letters, so changing your mounts in the UI does
// something visible instead of printing a `net use` line for you to run yourself.
//
// This is the same work install.ps1 does at step 7 — deliberately mirrored, because the installer
// still owns first-run and this owns every change after it. Keep the two in step.
[SupportedOSPlatform("windows")]
public sealed partial class DriveMapper(ServerSecret secret, ILogger<DriveMapper> log)
{
    public sealed record Result(List<string> Mapped, List<string> Unmapped, List<string> Errors);

    // Matches a `net use` row pointing at THIS server, e.g. "Z:  \\localhost@40323\z".
    // Anything else on the machine is somebody else's drive and we don't touch it.
    [GeneratedRegex(@"([A-Za-z]):\s+\\\\(?:localhost|127\.0\.0\.1)@(\d+)\\(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex MappedDriveRow();

    public async Task<Result> ApplyAsync(MountConfig config)
    {
        var mapped = new List<string>();
        var unmapped = new List<string>();
        var errors = new List<string>();

        var existing = await CurrentMappingsAsync(config.Port);
        var wanted = config.Mounts.ToDictionary(m => m.Letter.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

        // Drop drives that used to be ours but aren't configured any more. Scoped to our own port
        // so we can never unmap a user's unrelated network drive.
        foreach (var (letter, prefix) in existing)
        {
            if (wanted.ContainsKey(letter) && prefix.Equals(letter, StringComparison.OrdinalIgnoreCase)) continue;
            var (ok, err) = await RunNetAsync(["use", $"{letter}:", "/delete", "/y"]);
            if (ok) { unmapped.Add(letter); log.LogInformation("Unmapped {Letter}: (no longer configured)", letter); }
            else errors.Add($"Could not unmap {letter}: {err}");
        }

        foreach (var mount in config.Mounts)
        {
            var letter = mount.Letter.ToUpperInvariant();
            var url = $"http://localhost:{config.Port}/{letter.ToLowerInvariant()}/";

            // Always delete first. Re-mapping over a live mapping fails, and a stale mapping to an
            // old port looks identical to a working one until you click it.
            if (existing.ContainsKey(letter) || Directory.Exists($"{letter}:\\"))
                await RunNetAsync(["use", $"{letter}:", "/delete", "/y"]);

            var (ok, err) = await RunNetAsync(
                ["use", $"{letter}:", url, "/user:" + secret.Username, secret.Value, "/persistent:yes"]);

            if (ok)
            {
                mapped.Add(letter);
                SetExplorerLabel(config.Port, letter, mount.DisplayName);
                log.LogInformation("Mapped {Letter}: -> {Url} ({Name})", letter, url, mount.DisplayName);
            }
            else
            {
                errors.Add($"Could not map {letter}: {err}");
                log.LogWarning("Mapping {Letter}: failed — {Error}", letter, err);
            }
        }

        return new Result(mapped, unmapped, errors);
    }

    // Take our own drive letters back down, for a deliberate shutdown.
    //
    // This has to happen BEFORE the server stops. A letter left listed with nothing serving it is
    // worse than no letter at all: it looks perfectly normal in Explorer (it's mapped
    // /persistent:yes, so Windows even restores it after a reboot), and the first click blocks in
    // the WebDAV redirector until it times out, which reads as the whole shell freezing. Removing
    // it while we can still answer costs one round trip, because the redirector gets a reply
    // instead of a timeout.
    public async Task<Result> DisconnectAsync(MountConfig config)
    {
        var unmapped = new List<string>();
        var errors = new List<string>();

        // Whatever currently points at this port, plus anything configured that has a live letter.
        // The first set catches drives from a config we no longer have; the second catches a
        // mapping `net use` listed in a shape the regex didn't match.
        var letters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var letter in (await CurrentMappingsAsync(config.Port)).Keys) letters.Add(letter);
        foreach (var mount in config.Mounts)
        {
            var letter = mount.Letter.ToUpperInvariant();
            if (Directory.Exists($"{letter}:\\")) letters.Add(letter);
        }

        foreach (var letter in letters)
        {
            var (ok, err) = await RunNetAsync(["use", $"{letter}:", "/delete", "/y"]);
            if (ok)
            {
                unmapped.Add(letter);
                log.LogInformation("Disconnected {Letter}: for shutdown", letter);
            }
            else
            {
                errors.Add($"Could not disconnect {letter}: {err}");
                log.LogWarning("Disconnecting {Letter}: failed — {Error}", letter, err);
            }
        }

        return new Result([], unmapped, errors);
    }

    // Drive letter -> mount prefix, for drives pointing at this server on this port.
    private async Task<Dictionary<string, string>> CurrentMappingsAsync(int port)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (_, output) = await RunNetAsync(["use"], captureOutput: true);
        foreach (Match m in MappedDriveRow().Matches(output))
        {
            if (!int.TryParse(m.Groups[2].Value, out var p) || p != port) continue;
            found[m.Groups[1].Value.ToUpperInvariant()] = m.Groups[3].Value.Trim('\\');
        }
        return found;
    }

    // Without this the drive shows in Explorer as "\\localhost@40323\s" instead of "S: (Finance)".
    // Cosmetic, but the raw form looks broken enough that people report it as a bug.
    private void SetExplorerLabel(int port, string letter, string label)
    {
        try
        {
            var path = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\##localhost@{port}#{letter.ToLowerInvariant()}";
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key?.SetValue("_LabelFromReg", label, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            // A missing label is cosmetic. Never fail a mapping over it.
            log.LogDebug(ex, "Could not set Explorer label for {Letter}:", letter);
        }
    }

    private static async Task<(bool Ok, string Output)> RunNetAsync(string[] args, bool captureOutput = false)
    {
        var psi = new ProcessStartInfo("net.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc == null) return (false, "could not start net.exe");

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var ok = proc.ExitCode == 0;
        if (captureOutput) return (ok, stdout);
        // net.exe puts its real complaint on stdout as often as stderr. Take whichever spoke.
        var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (ok, message.Trim());
    }
}
