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

    // 2: Codex's cached copies of agent-ha-bridge's plugin hooks.json are measured too (#123). A
    // version 1 baseline never hashed them, so against it they would read as added (failing
    // a strict rollback check) or pass unseen under --allow hooks; neither proves anything.
    // 3: the agents' homes are the ones installation.json records, which a migration rewrites,
    // not this process's environment or --home; a version 2 baseline of a custom codexHome
    // measured another folder's cache, or none, and cannot show that its own was put back.
    public const int Version = 3;

    public static Baseline Capture(Homes homes, ITaskReader tasks, DateTimeOffset now)
    {
        homes = Recorded(homes);
        var files = new List<FileEntry>();
        // The daemon writes its runtime state and logs as it runs, so those differ between any
        // two moments and say nothing about whether the install was put back.
        Tree(files, homes, Install, homes.BridgeHome, rel =>
            !rel.StartsWith("runtime/", StringComparison.OrdinalIgnoreCase) &&
            !rel.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) &&
            !IsLog(rel) &&
            !IsCodexHooks(rel));
        Tree(files, homes, Hooks, homes.BridgeHome, IsCodexHooks);
        // What Codex actually runs: its cached copy of the plugin, one per version, which a
        // migration re-points with the source (#123).
        Tree(files, homes, Hooks, Path.Combine(homes.CodexHome, "plugins", "cache", "agent-ha-bridge", "agent-ha-bridge"), IsCachedCodexHooks);
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
        return new Baseline(Version, now, Environment.MachineName, Environment.UserName,
            [.. Distinct(files).OrderBy(f => f.Path, StringComparer.Ordinal)], [.. taskList.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    static readonly StringComparer PathCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // A recorded home can sit inside another one measured, as a codexHome under bridgeHome does,
    // so one file can be found twice: it is measured once, filed by the later, agent-specific
    // look (its cached hooks.json is a hook, not part of the install).
    static IEnumerable<FileEntry> Distinct(List<FileEntry> files) =>
        files.Select((f, at) => (f, at)).GroupBy(e => e.f.Path, PathCase).Select(g => g.MaxBy(e => e.at).f);

    // The agents' homes as agent-ha-bridge's installation.json records them, read as a migration
    // reads them (AgentHaBridgeInstall.Read): codexHome, copilotHome and claudeHome, else under
    // its home, else under the folder above bridgeHome. No record leaves them as given (there is
    // nothing to migrate). A record that cannot say which homes they are is refused, never
    // guessed at, so a baseline cannot measure an untouched folder and call it put back.
    internal static Homes Recorded(Homes homes)
    {
        var metadata = Path.Combine(homes.BridgeHome, "installation.json");
        if (!File.Exists(metadata)) { return homes; }
        IOException Unclear(string why, Exception? inner = null) =>
            new($"{metadata} {why}, so which homes a migration would rewrite cannot be told; nothing was measured", inner);
        JsonElement record;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(metadata));
            record = doc.RootElement.Clone();
        }
        catch (JsonException e) { throw Unclear($"is not JSON ({e.Message})", e); }
        if (record.ValueKind != JsonValueKind.Object) { throw Unclear("is not a JSON object"); }
        string? Folder(string name)
        {
            if (!record.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) { return null; }
            if (value.ValueKind != JsonValueKind.String) { throw Unclear($"has a {name} that is not a full path"); }
            string path;
            // Known to be a string, so this throws only when its escapes are not text, such as
            // half a surrogate pair (\uD800 alone), which JsonDocument.Parse lets through.
            try { path = value.GetString()!; }
            catch (InvalidOperationException e) { throw Unclear($"has a {name} that is not readable text ({e.Message})", e); }
            if (!Path.IsPathFullyQualified(path)) { throw Unclear($"has a {name} that is not a full path"); }
            // Normalised, so a file it shares with another home is named alike from both. A full
            // path can still be no path at all (it holds a NUL, or is too long), and that is refused too.
            try { return Path.GetFullPath(path); }
            catch (Exception e) when (e is ArgumentException or PathTooLongException) { throw Unclear($"has a {name} that is not a valid path ({e.Message})", e); }
        }
        var home = Folder("home") ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(homes.BridgeHome)))!;
        return homes with
        {
            CopilotHome = Folder("copilotHome") ?? Path.Combine(home, ".copilot"),
            ClaudeHome = Folder("claudeHome") ?? Path.Combine(home, ".claude"),
            CodexHome = Folder("codexHome") ?? Path.Combine(home, ".codex"),
        };
    }

    // Codex's hook registration. agent-ha-bridge's installer keeps its Codex plugin, and the
    // plugin's hooks.json, in its own folder, and a migration re-points those hooks as it does
    // Copilot's and Claude's; filed as install, D-04 could never pass (#115).
    static bool IsCodexHooks(string rel)
    {
        var parts = rel.Split('/');
        return parts.Length == 4
            && parts[0].Equals("codex-bridge", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("plugins", StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals("hooks.json", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsCachedCodexHooks(string rel)
    {
        var parts = rel.Split('/');
        return parts.Length == 2 && parts[1].Equals("hooks.json", StringComparison.OrdinalIgnoreCase);
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
            // Filed as this tool files it now, so a baseline saved by an older one is judged by the
            // current rule (#115).
            else if (n.Sha256 != f.Sha256) { diffs.Add(new(n.Category, f.Path, $"changed ({f.Bytes} -> {n.Bytes} bytes)")); }
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
        if (before.Version != Version)
        {
            return Outcome.Error($"the baseline is version {before.Version}, and this tool measures version {Version} (Codex's cached plugin hooks.json, #123, in the Codex home installation.json records), so it cannot be compared. "
                + "Before migrating, save the baseline again with this tool. If the machine has already been migrated it is too late: the cache as it was before migrating was never measured, and a baseline saved now does not prove it");
        }
        var unexpected = diffs.Where(d => !allowed.Contains(d.Category)).ToList();
        var data = new { baseline = before.CapturedAt, unexpected, allowed = diffs.Where(d => allowed.Contains(d.Category)).ToList() };
        if (unexpected.Count == 0)
        {
            return Outcome.Pass(diffs.Count == 0 ? "identical to the baseline, byte for byte" : $"only allowed changes: {diffs.Count} in {string.Join(", ", allowed.Order(StringComparer.Ordinal))}", data);
        }
        var outside = allowed.Count == 0 ? "the baseline" : $"{string.Join(", ", allowed.Order(StringComparer.Ordinal))}";
        return Outcome.Fail($"{unexpected.Count} change(s) outside {outside}: {string.Join("; ", unexpected.Take(5).Select(d => $"{d.Path} {d.Change}"))}", data);
    }

    public static Baseline Load(string file)
    {
        var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(file), Evidence.Json) ?? throw new IOException($"{file} is not a baseline");
        // An earlier version 3 tool measured a file inside two recorded homes twice; which of its
        // hashes, and which filing, is the one to compare cannot be told.
        if (baseline.Files.GroupBy(f => f.Path, PathCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new IOException($"{file} holds {twice.Key} {twice.Count()} times, so it cannot be compared. Before migrating, save the baseline again with this tool. "
                + "If the machine has already been migrated it is too late: a baseline saved now does not prove the files as they were before migrating");
        }
        return baseline;
    }

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
