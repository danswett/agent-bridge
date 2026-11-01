using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// Every working directory a live agent session has, or Known false with why not. Detail never
// carries a path.
public sealed record WorktreeUsage(bool Known, IReadOnlyList<string> Directories, string Detail)
{
    public static WorktreeUsage Unknown(string detail) => new(false, [], detail);

    // Whether a live session works at or below this worktree. Unknown usage is "yes".
    public bool InUse(string worktree)
    {
        if (!Known) { return true; }
        var physical = Folders.Physical(worktree) ?? worktree;
        return Directories.Any(d => Folders.IsAtOrUnder(d, physical));
    }
}

// What the reclaimer asks before it removes anything. Injected so tests never read the real
// machine's processes.
public interface IWorktreeUsage
{
    WorktreeUsage Read();
}

// The safety snapshot, unknown while session discovery is held: a session discovery could not
// see may be working in any of the trees, and a wrong absence here deletes its work (§6.3
// invariants 6 and 8). Asked afresh for each read, so a hold that begins between two removals
// stops the second, and again after the inner read, so one that began during it (the
// reclaimer's last read, under git's locks) does too.
public sealed class HeldWorktreeUsage(ISessionSource sessions, IWorktreeUsage? inner) : IWorktreeUsage
{
    public WorktreeUsage Read()
    {
        if (sessions.Held is { } held) { return Unknown(held); }
        var usage = inner?.Read() ?? WorktreeUsage.Unknown("this host has no way to read it");
        return usage.Known && sessions.Held is { } after ? Unknown(after) : usage;
    }

    static WorktreeUsage Unknown(DiscoveryHeld held) => WorktreeUsage.Unknown($"session discovery is held, waiting on {held.Reason}");
}

// A read-only safety snapshot, as agent-ha-bridge's Get-BridgeWorktreeUsage takes it: not
// session discovery, which is allowed to skip what it cannot read. Here anything uncertain
// makes the whole snapshot unknown, and unknown usage retains every worktree:
//   * any Agency launcher running, since it cannot be told from a session still starting;
//   * an agent process (Copilot, Claude, Codex, through every adapter the host knows) whose
//     start time cannot be read, or that no session accounts for;
//   * a Copilot session a live process holds whose working directory cannot be read, or is
//     named twice; a lock file that is not inuse.<pid>.lock, or older than its process;
//   * a Claude or Codex registration that cannot be read, has no session, no process, no
//     working directory, a time outside its process's life, or names a different live process;
//   * a linked session folder, registration or registration folder.
public sealed partial class AgentWorktreeUsage : IWorktreeUsage
{
    readonly IProcessInspector processes;
    readonly string copilotHome;
    readonly IReadOnlyList<string> claudeRoots;
    readonly IReadOnlyList<string> codexRoots;
    readonly Func<bool?> agencyRunning;
    readonly Func<int, bool?> processExists;
    readonly TimeProvider time;

    public AgentWorktreeUsage(IProcessInspector processes, string copilotHome, IReadOnlyList<string> claudeRoots, IReadOnlyList<string> codexRoots,
        Func<bool?>? agencyRunning = null, Func<int, bool?>? processExists = null, TimeProvider? time = null)
    {
        this.processes = processes;
        this.copilotHome = copilotHome;
        this.claudeRoots = claudeRoots;
        this.codexRoots = codexRoots;
        this.agencyRunning = agencyRunning ?? AgencyRunning;
        this.processExists = processExists ?? ProcessExists;
        this.time = time ?? TimeProvider.System;
    }

    public WorktreeUsage Read()
    {
        try { return Snapshot(); }
        catch (UsageUnknown e) { return WorktreeUsage.Unknown(e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return WorktreeUsage.Unknown($"session state could not be read ({e.GetType().Name})");
        }
    }

    WorktreeUsage Snapshot()
    {
        switch (agencyRunning())
        {
            case null: throw new UsageUnknown("whether an Agency launcher is running cannot be read");
            case true: throw new UsageUnknown("an Agency launcher is active, so a session still starting cannot be ruled out");
        }

        var directories = new List<string>();
        var agents = new Dictionary<AgentKind, Dictionary<int, AgentProcess>>();
        foreach (var kind in new[] { AgentKind.Copilot, AgentKind.Claude, AgentKind.Codex })
        {
            var observed = processes.Observe(kind);
            if (!observed.Known) { throw new UsageUnknown($"a possible {Name(kind)} process could not be identified"); }
            var byPid = new Dictionary<int, AgentProcess>();
            foreach (var p in observed.Processes)
            {
                // Codex's app-server daemon is no session and works in no folder, and it outlives
                // the session that started it: counted here, every finished worktree was kept for
                // as long as it ran, which is from a machine's first Codex session on (#154).
                // Discovery leaves it out the same way.
                if (kind == AgentKind.Codex && CommandLine.IsCodexAppServer(p.CommandLine)) { continue; }
                if (p.StartedAt is null) { throw new UsageUnknown($"a live {Name(kind)} process has no readable start time"); }
                byPid[p.Id] = p;
            }
            agents[kind] = byPid;
        }

        Copilot(agents[AgentKind.Copilot], directories);
        Registrations(AgentKind.Claude, claudeRoots, agents[AgentKind.Claude], directories);
        Registrations(AgentKind.Codex, codexRoots, agents[AgentKind.Codex], directories);
        return new WorktreeUsage(true, directories, "");
    }

    void Copilot(Dictionary<int, AgentProcess> live, List<string> directories)
    {
        var named = new Dictionary<int, string>();
        foreach (var p in live.Values)
        {
            if (CommandLine.CopilotSessionId(p.CommandLine) is { } id) { named[p.Id] = id; }
        }
        var accounted = new HashSet<int>();
        var root = Path.Combine(copilotHome, "session-state");
        if (Directory.Exists(root))
        {
            foreach (var session in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (session.Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new UsageUnknown("a linked Copilot session folder cannot establish complete usage"); }
                var owners = named.Where(n => string.Equals(n.Value, session.Name, StringComparison.OrdinalIgnoreCase)).Select(n => n.Key).ToHashSet();
                foreach (var lockFile in session.EnumerateFiles("inuse.*.lock"))
                {
                    var match = LockName().Match(lockFile.Name);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                    {
                        throw new UsageUnknown("an invalid Copilot process lock prevents cleanup");
                    }
                    if (!live.TryGetValue(pid, out var process)) { continue; }
                    var namesIt = named.TryGetValue(pid, out var other);
                    if (namesIt && !string.Equals(other, session.Name, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (!namesIt && lockFile.LastWriteTimeUtc < process.StartedAt!.Value.UtcDateTime)
                    {
                        throw new UsageUnknown("a Copilot lock predates its process, so usage is uncertain");
                    }
                    owners.Add(pid);
                }
                if (owners.Count == 0) { continue; }
                var cwd = CopilotCwd(Path.Combine(session.FullName, "workspace.yaml"));
                directories.Add(Resolve(cwd, "a live Copilot or Agency session"));
                accounted.UnionWith(owners);
            }
        }
        if (live.Keys.Any(pid => !accounted.Contains(pid)))
        {
            throw new UsageUnknown("a live Copilot or Agency process has no session with a working directory");
        }
    }

    // agent-ha-bridge's Read-BridgeCopilotWorkspace: flat key: value lines, and a cwd named
    // twice is no cwd at all.
    static string CopilotCwd(string path)
    {
        string? cwd = null;
        foreach (var line in File.ReadAllLines(path))
        {
            var m = YamlLine().Match(line);
            if (!m.Success || m.Groups[1].Value != "cwd") { continue; }
            if (cwd is not null) { throw new UsageUnknown("a Copilot session has ambiguous working directories"); }
            cwd = m.Groups[2].Value.Trim().Trim('"', '\'');
        }
        return cwd ?? "";
    }

    void Registrations(AgentKind kind, IReadOnlyList<string> roots, Dictionary<int, AgentProcess> live, List<string> directories)
    {
        var name = Name(kind);
        var accounted = new HashSet<int>();
        foreach (var root in roots)
        {
            var folder = new DirectoryInfo(root);
            if (!folder.Exists) { continue; }
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new UsageUnknown($"a linked {name} registration folder prevents cleanup"); }
            foreach (var file in folder.EnumerateFiles("*.json"))
            {
                if (kind == AgentKind.Codex && file.Name.EndsWith(".approval.json", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new UsageUnknown($"a linked {name} registration prevents cleanup"); }
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("SessionId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    throw new UsageUnknown($"an incomplete {name} registration prevents cleanup");
                }
                if (kind == AgentKind.Codex && e.TryGetProperty("Ended", out var ended))
                {
                    if (ended.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw new UsageUnknown("an invalid Codex ended state prevents cleanup"); }
                    if (ended.ValueKind == JsonValueKind.True) { continue; }
                }
                if (Pid(e) is not { } pid) { throw new UsageUnknown($"a {name} registration has no process"); }
                if (!live.TryGetValue(pid, out var process))
                {
                    if (processExists(pid) != false) { throw new UsageUnknown($"a {name} registration names a different live process, so ownership is uncertain"); }
                    continue;
                }
                if (!e.TryGetProperty("Updated", out var u) || u.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated))
                {
                    throw new UsageUnknown($"a {name} registration has no valid time");
                }
                if (updated < process.StartedAt!.Value || updated > time.GetUtcNow())
                {
                    throw new UsageUnknown($"a {name} registration does not belong to the process now holding its id");
                }
                var cwd = e.TryGetProperty("WorkingDirectory", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "";
                directories.Add(Resolve(cwd, $"a live {name} session"));
                accounted.Add(pid);
            }
        }
        if (live.Keys.Any(pid => !accounted.Contains(pid)))
        {
            throw new UsageUnknown($"a live {name} process has no registration");
        }
    }

    static int? Pid(JsonElement e)
    {
        if (!e.TryGetProperty("ProcessId", out var p)) { return null; }
        var ok = p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String => int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
        return ok > 0 ? ok : null;
    }

    // Where it really is, every link resolved; a working directory that is gone or cannot be
    // read is not evidence that the session is elsewhere.
    static string Resolve(string cwd, string what)
    {
        if (cwd.Length == 0) { throw new UsageUnknown($"{what} has no working directory"); }
        string? physical = null;
        try { if (Path.IsPathFullyQualified(cwd)) { physical = Folders.Physical(cwd); } }
        catch (ArgumentException) { }
        return physical ?? throw new UsageUnknown($"{what} has a working directory that cannot be resolved");
    }

    static string Name(AgentKind kind) => kind switch { AgentKind.Copilot => "Copilot", AgentKind.Claude => "Claude", _ => "Codex" };

    static bool? AgencyRunning()
    {
        try
        {
            var found = Process.GetProcessesByName("agency");
            foreach (var p in found) { p.Dispose(); }
            return found.Length > 0;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return null; }
    }

    static bool? ProcessExists(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return true; }
        catch (ArgumentException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    [GeneratedRegex(@"^inuse\.(\d+)\.lock$", RegexOptions.CultureInvariant)]
    private static partial Regex LockName();

    [GeneratedRegex(@"^(\w+):\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex YamlLine();

    sealed class UsageUnknown(string message) : Exception(message);
}
