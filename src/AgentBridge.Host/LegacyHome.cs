using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Migration;

namespace AgentBridge.Host;

// What a leftover agent-ha-bridge folder is, for Set up (#3644). A leftover used to block Codex's
// Set up for good, even a dead one a removed install had left behind (Nov 21). Now:
//   FullInstall: it has installation.json, so it was a whole installation. Migrate owns it; Set up
//     never touches it.
//   Dead: nothing says it is in use. Set up renames it aside, never deletes it, and records it.
//   Uncertain: anything else. Refused, naming why. A file that cannot be read is uncertain too.
// The host still reads the registration roots under it (runtime\agent-bridge-claude and
// \agent-bridge-codex), so "dead" means no registration there in the last 24 hours.
internal enum LegacyKind { FullInstall, Dead, Uncertain }

internal sealed record LegacyVerdict(LegacyKind Kind, string Path, string Reason)
{
    // The App parses these two messages (AgentWords.SetupFailed); keep their shape.
    public const string Prefix = "agent-ha-bridge folder at ";

    public string Refusal => Kind switch
    {
        LegacyKind.FullInstall => $"{Prefix}{Path}: full installation (installation.json); use Migrate",
        LegacyKind.Uncertain => $"{Prefix}{Path}: may still be in use ({Reason})",
        _ => "an agent-ha-bridge directory exists and may retain legacy state; Set up retires an unused one, but not while removing hooks",
    };
}

internal static class LegacyHome
{
    const string RecordPrefix = "legacy-retired-";
    static readonly TimeSpan Recent = TimeSpan.FromHours(24);
    static readonly System.Text.Json.JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static LegacyVerdict Assess(string path, IntakeState.AgentHomes homes, DateTimeOffset now, Func<string, string?> processUsing)
    {
        LegacyVerdict Uncertain(string why) => new(LegacyKind.Uncertain, path, why);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { return Uncertain("it is a link, not a folder"); }
            if (File.Exists(Path.Combine(path, "installation.json"))) { return new(LegacyKind.FullInstall, path, ""); }
            if (processUsing(path) is { } holder) { return Uncertain($"a running process uses it: {holder}"); }
            foreach (var agent in new[] { "claude", "codex" })
            {
                var roots = Path.Combine(path, "runtime", $"agent-bridge-{agent}");
                if (!Directory.Exists(roots)) { continue; }
                var fresh = Directory.EnumerateFiles(roots, "*", SearchOption.AllDirectories)
                    .Count(f => File.GetLastWriteTimeUtc(f) > now.UtcDateTime - Recent);
                if (fresh > 0) { return Uncertain($"{fresh} session registration(s) under runtime\\agent-bridge-{agent} were written in the last 24 hours"); }
            }
            // An agent whose config names the folder may run commands from it.
            foreach (var config in new[]
            {
                Path.Combine(homes.Codex, "config.toml"), Path.Combine(homes.Codex, "hooks.json"),
                Path.Combine(homes.Claude, "settings.json"), Path.Combine(homes.Copilot, "config.json"),
                Path.Combine(homes.Copilot, "hooks", "decision-notifier.json"),
            })
            {
                if (File.Exists(config) && Names(File.ReadAllText(config), path)) { return Uncertain($"{config} refers to it"); }
            }
            return new(LegacyKind.Dead, path, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Uncertain($"it could not be read: {e.Message}");
        }
    }

    // The folder's own path in either slash style, or agent-ha-bridge's well-known folder name.
    static bool Names(string text, string path) =>
        text.Contains(".agent-ha-bridge", StringComparison.OrdinalIgnoreCase)
        || text.Contains(path, StringComparison.OrdinalIgnoreCase)
        || text.Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
        || text.Contains(path.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);

    // A running process that runs from the folder or names it on its command line: the old
    // bridge's scripts run under pwsh, so the executable alone would miss them. Null when none.
    public static string? ProcessUsing(string path)
    {
        if (!OperatingSystem.IsWindows()) { return null; }
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) { continue; }
                string? line = null, exe = null;
                try { line = SystemProcessInspector.ReadCommandLine(process.Id); } catch (Exception e) when (e is not OutOfMemoryException) { }
                try { exe = process.MainModule?.FileName; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                if ((line is not null && Names(line, path)) || (exe is not null && exe.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    return $"PID {process.Id} {process.ProcessName}";
                }
            }
        }
        return null;
    }

    public static string Target(string path, DateTimeOffset now) => $"{path}.legacy-{now.UtcDateTime:yyyyMMdd-HHmmss}";

    // Renames the folder aside and records it. Windows refuses the move while a process holds a file
    // inside, which is a second test the folder is idle; the record is written after, and the move
    // undone if it cannot be, so a recorded rename is always a real one.
    public static string Retire(string path, string profile, DateTimeOffset now)
    {
        var target = Target(path, now);
        if (Directory.Exists(target) || File.Exists(target)) { throw new InvalidOperationException($"{LegacyVerdict.Prefix}{path}: not renamed, {target} already exists"); }
        try { Directory.Move(path, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{LegacyVerdict.Prefix}{path}: may still be in use (it could not be renamed: {e.Message})");
        }
        try
        {
            var record = new JsonObject
            {
                ["version"] = 1, ["from"] = path, ["to"] = target, ["retiredAt"] = now.UtcDateTime.ToString("O"),
                ["rollback"] = $"Rename-Item -LiteralPath '{target}' -NewName '{Path.GetFileName(path)}'",
            };
            var file = Path.Combine(profile, $"{RecordPrefix}{now.UtcDateTime:yyyyMMdd-HHmmss}.json");
            DurableFile.Replace(file, Encoding.UTF8.GetBytes(record.ToJsonString(Pretty) + Environment.NewLine), MigrationRecord.Hash(file));
        }
        catch
        {
            Directory.Move(target, path);
            throw;
        }
        return target;
    }

    // Puts the newest retired folder back, if nothing has taken its name since.
    public static string Restore(string path, string profile)
    {
        var record = Directory.Exists(profile) ? Directory.GetFiles(profile, RecordPrefix + "*.json").Order().LastOrDefault() : null;
        if (record is null) { throw new InvalidOperationException("nothing is recorded as retired by Set up; no agent-ha-bridge folder was renamed"); }
        var saved = JsonNode.Parse(File.ReadAllText(record)) as JsonObject;
        var origin = (string?)saved?["from"];
        var moved = (string?)saved?["to"];
        if (origin is null || moved is null || !StandaloneHooks.Same(origin, path))
        {
            throw new InvalidOperationException($"{record} does not describe {path}; it was left alone");
        }
        if (Directory.Exists(origin) || File.Exists(origin)) { throw new InvalidOperationException($"{origin} already exists; nothing was restored"); }
        if (!Directory.Exists(moved)) { throw new InvalidOperationException($"{moved} is gone; nothing was restored"); }
        Directory.Move(moved, origin);
        DurableFile.Remove(record, MigrationRecord.Hash(record));
        return origin;
    }
}
