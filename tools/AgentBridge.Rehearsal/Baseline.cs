using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace AgentBridge.Rehearsal;

// Where one user's agents and agent-ha-bridge keep their files. From the environment as each
// agent resolves it, or entirely under --home (another user's profile, or a test's folder).
internal sealed record Homes(string Home, string BridgeHome, string CopilotHome, string ClaudeHome, string CodexHome, string? RoamingAppData)
{
    public static Homes For(string? homeOverride)
    {
        if (homeOverride is { } h)
        {
            var home = Path.GetFullPath(h);
            return new(home, Path.Combine(home, ".agent-ha-bridge"), Path.Combine(home, ".copilot"), Path.Combine(home, ".claude"),
                Path.Combine(home, ".codex"), OperatingSystem.IsWindows() ? Path.Combine(home, "AppData", "Roaming") : null);
        }
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Root(string variable, string folder) =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set ? set : Path.Combine(user, folder);
        return new(user, Path.Combine(user, ".agent-ha-bridge"), Root("COPILOT_HOME", ".copilot"), Root("CLAUDE_CONFIG_DIR", ".claude"),
            Root("CODEX_HOME", ".codex"), OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : null);
    }
}

internal sealed record FileEntry(string Category, string Path, string Sha256, long Bytes);

internal sealed record TaskEntry(string Name, bool Enabled, string State, string Sha256);

internal sealed record Baseline(int Version, DateTimeOffset CapturedAt, string Machine, string User, IReadOnlyList<FileEntry> Files, IReadOnlyList<TaskEntry> Tasks);

internal sealed record Difference(string Category, string Path, string Change);

internal interface ITaskReader
{
    // agent-ha-bridge's scheduled tasks (or LaunchAgent), or null when they cannot be read.
    IReadOnlyList<TaskEntry>? Read();
}

// What agent-ha-bridge installed or registered for one user, as hashes: enough to show that a
// rollback put back every byte, and never a byte of content (config.json holds a token).
internal static class BaselineCapture
{
    public const string Install = "install", Hooks = "hooks", Agents = "agents", Tasks = "tasks";
    public static readonly string[] Categories = [Install, Hooks, Agents, Tasks];

    public static Baseline Capture(Homes homes, ITaskReader tasks, DateTimeOffset now)
    {
        var files = new List<FileEntry>();
        // The daemon writes its runtime state and logs as it runs, so those differ between any
        // two moments and say nothing about whether the install was put back.
        Tree(files, homes, Install, homes.BridgeHome, rel =>
            !rel.StartsWith("runtime/", StringComparison.OrdinalIgnoreCase) &&
            !rel.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) &&
            !IsLog(rel));
        Tree(files, homes, Hooks, Path.Combine(homes.CopilotHome, "hooks"), _ => true);
        One(files, homes, Hooks, Path.Combine(homes.CopilotHome, "instructions", "agent-ha-bridge.instructions.md"));
        One(files, homes, Agents, Path.Combine(homes.ClaudeHome, "settings.json"));
        One(files, homes, Agents, Path.Combine(homes.ClaudeHome, "CLAUDE.md"));
        One(files, homes, Agents, Path.Combine(homes.CodexHome, "config.toml"));
        One(files, homes, Agents, Path.Combine(homes.CodexHome, "AGENTS.md"));
        One(files, homes, Agents, Path.Combine(homes.CodexHome, "agent-ha-bridge-owner.json"));
        if (homes.RoamingAppData is { } roaming) { One(files, homes, Agents, Path.Combine(roaming, "Claude", "claude_desktop_config.json")); }
        One(files, homes, Agents, Path.Combine(homes.Home, "Library", "Application Support", "Claude", "claude_desktop_config.json"));
        One(files, homes, Tasks, Path.Combine(homes.Home, "Library", "LaunchAgents", "com.agent-ha-bridge.daemon.plist"));
        var taskList = tasks.Read() ?? throw new IOException("agent-ha-bridge's scheduled tasks could not be read");
        return new Baseline(1, now, Environment.MachineName, Environment.UserName,
            [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)], [.. taskList.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    static bool IsLog(string rel)
    {
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        var at = name.IndexOf(".log", StringComparison.OrdinalIgnoreCase);
        return at > 0 && (at + 4 == name.Length || name[at + 4] == '.');
    }

    static void Tree(List<FileEntry> files, Homes homes, string category, string root, Func<string, bool> keep)
    {
        if (!Directory.Exists(root)) { return; }
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (keep(rel)) { One(files, homes, category, file); }
        }
    }

    static void One(List<FileEntry> files, Homes homes, string category, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return; }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        files.Add(new FileEntry(category, Display(homes, path), hash, info.Length));
    }

    // Relative to the home folder, so baselines of two users or runs compare by name.
    internal static string Display(Homes homes, string path)
    {
        var rel = Path.GetRelativePath(homes.Home, path);
        return (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : "~/" + rel).Replace('\\', '/');
    }

    public static IReadOnlyList<Difference> Compare(Baseline before, Baseline after, IReadOnlyCollection<string> ignore)
    {
        var diffs = new List<Difference>();
        bool Ignored(string path) => ignore.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        var was = before.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var now = after.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var f in before.Files.Where(f => !Ignored(f.Path)))
        {
            if (!now.TryGetValue(f.Path, out var n)) { diffs.Add(new(f.Category, f.Path, "removed")); }
            else if (n.Sha256 != f.Sha256) { diffs.Add(new(f.Category, f.Path, $"changed ({f.Bytes} -> {n.Bytes} bytes)")); }
        }
        foreach (var f in after.Files.Where(f => !Ignored(f.Path) && !was.ContainsKey(f.Path)))
        {
            diffs.Add(new(f.Category, f.Path, "added"));
        }
        var tasksBefore = before.Tasks.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var tasksAfter = after.Tasks.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var t in before.Tasks)
        {
            if (!tasksAfter.TryGetValue(t.Name, out var n)) { diffs.Add(new(Tasks, t.Name, "removed")); continue; }
            if (n.Enabled != t.Enabled) { diffs.Add(new(Tasks, t.Name, n.Enabled ? "enabled" : "disabled")); }
            else if (n.Sha256 != t.Sha256) { diffs.Add(new(Tasks, t.Name, "definition changed")); }
        }
        foreach (var t in after.Tasks.Where(t => !tasksBefore.ContainsKey(t.Name))) { diffs.Add(new(Tasks, t.Name, "added")); }
        return diffs;
    }

    // Passes when nothing changed outside the categories a step allows to change.
    public static Outcome Judge(Baseline before, IReadOnlyList<Difference> diffs, IReadOnlySet<string> allowed)
    {
        var unexpected = diffs.Where(d => !allowed.Contains(d.Category)).ToList();
        var data = new { baseline = before.CapturedAt, unexpected, allowed = diffs.Where(d => allowed.Contains(d.Category)).ToList() };
        if (unexpected.Count == 0)
        {
            return Outcome.Pass(diffs.Count == 0 ? "identical to the baseline, byte for byte" : $"only allowed changes: {diffs.Count} in {string.Join(", ", allowed.Order(StringComparer.Ordinal))}", data);
        }
        var outside = allowed.Count == 0 ? "the baseline" : $"{string.Join(", ", allowed.Order(StringComparer.Ordinal))}";
        return Outcome.Fail($"{unexpected.Count} change(s) outside {outside}: {string.Join("; ", unexpected.Take(5).Select(d => $"{d.Path} {d.Change}"))}", data);
    }

    public static Baseline Load(string file) =>
        JsonSerializer.Deserialize<Baseline>(File.ReadAllText(file), Evidence.Json) ?? throw new IOException($"{file} is not a baseline");

    public static void Save(Baseline baseline, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, JsonSerializer.Serialize(baseline, new JsonSerializerOptions(Evidence.Json) { WriteIndented = true }));
    }
}

// agent-ha-bridge's tasks through schtasks, which every Windows has; nothing is changed.
internal sealed class ScheduledTasks : ITaskReader
{
    static readonly string[] Prefixes = ["AgentBridgeDaemon", "AgentBridgeDevBoxKeepAwake", "CopilotBridgeDaemon"];

    public IReadOnlyList<TaskEntry>? Read()
    {
        if (!OperatingSystem.IsWindows()) { return []; }
        var list = Run("/query", "/fo", "csv", "/nh");
        if (list is null) { return null; }
        var found = new List<TaskEntry>();
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cells = line.Split("\",\"");
            if (cells.Length < 3) { continue; }
            var name = cells[0].Trim('"');
            var leaf = name.TrimStart('\\');
            if (leaf.Contains('\\') || !Prefixes.Any(p => leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { continue; }
            if (found.Any(t => t.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase))) { continue; }
            var xml = Run("/query", "/tn", name, "/xml", "ONE");
            if (xml is null) { return null; }
            found.Add(new TaskEntry(leaf, Enabled(xml), cells[2].Trim('"'), Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml.Trim()))).ToLowerInvariant()));
        }
        return found;
    }

    internal static bool Enabled(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml.Trim());
            var settings = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Settings");
            var enabled = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value;
            return enabled is null || !enabled.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Xml.XmlException) { return true; }
    }

    static string? Run(params string[] args)
    {
        var start = new ProcessStartInfo("schtasks.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) { start.ArgumentList.Add(a); }
        using var p = Process.Start(start);
        if (p is null) { return null; }
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : null;
    }
}
