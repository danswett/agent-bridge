using System.Text.Json;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Discovery;

// A live session as discovery found it, before its transcript is read.
internal sealed record DiscoveredSession(
    string Id,
    AgentKind Agent,
    string? TranscriptPath,
    string? StateDirectory,
    int? ProcessId,
    Registration? Registration = null);

// One agent-ha-bridge hook registration (<id>.json), as written by its Claude and Codex
// hooks. Only read; agent-ha-bridge prunes its own files.
internal sealed record Registration(
    string SessionId,
    int ProcessId,
    string? TranscriptPath,
    string? WorkingDirectory,
    DateTimeOffset? Updated,
    string? HookStatus,
    DateTimeOffset? HookStatusAt,
    string? Model,
    string? Status,
    bool Ended,
    string Path,
    ApprovalMarker? Approval);

// A Codex command waiting for approval (<id>.approval.json): the hook writes it on
// PermissionRequest and removes it on the next event that proves it was answered.
internal sealed record ApprovalMarker(string DecisionId, string Question, DateTimeOffset? Created);

internal static class CopilotDiscovery
{
    // Live Copilot sessions: session-state/<id>/ folders, each with the pid that owns it.
    //
    // Two sources, because neither is complete. `--session-id` on a process's command
    // line names its session outright, new or resumed, and settles the pid. The
    // inuse.<pid>.lock files cover anything whose command line could not be read. The
    // locks alone missed a resumed session entirely: resuming onto an id that already
    // has history writes no lock, and a session ran 103 turns without a card.
    public static IReadOnlyList<DiscoveredSession> Find(string copilotHome, IReadOnlyList<AgentProcess> processes)
    {
        var root = Path.Combine(copilotHome, "session-state");
        if (processes.Count == 0 || !Directory.Exists(root)) { return []; }
        var byPid = processes.ToDictionary(p => p.Id);

        var candidates = new List<Candidate>();
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            foreach (var lockFile in SafeEnumerateFiles(dir, "inuse.*.lock"))
            {
                var name = Path.GetFileName(lockFile);
                var digits = name.AsSpan("inuse.".Length, name.Length - "inuse.".Length - ".lock".Length);
                if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)) { continue; }
                if (!byPid.ContainsKey(pid)) { continue; }
                // One candidate per live lock: a folder two live CLIs both hold a lock in
                // used to yield only one of them.
                candidates.Add(Candidate.Of(dir, pid, LastWrite(lockFile)));
            }
        }

        var live = new Dictionary<string, DiscoveredSession>(StringComparer.OrdinalIgnoreCase);
        var settled = new HashSet<int>();
        foreach (var process in processes)
        {
            var named = CommandLine.CopilotSessionId(process.CommandLine);
            if (named is null) { continue; }
            var dir = Path.Combine(root, named);
            var winner = candidates.FirstOrDefault(c => c.Pid == process.Id && string.Equals(c.Id, named, StringComparison.OrdinalIgnoreCase))
                ?? (Directory.Exists(dir) ? Candidate.Of(dir, process.Id, DateTime.MinValue) : null);
            if (winner is null) { continue; }
            winner = ResolveResume(winner, candidates, process.StartedAt?.UtcDateTime);
            live[winner.Id] = winner.ToSession();
            settled.Add(process.Id);
        }

        // One CLI owns one live session. A process that resumed a different session leaves
        // its old lock behind, so the same pid can hold locks in several folders; showing
        // all of them made phantom sessions. The most recently written transcript wins.
        foreach (var group in candidates.Where(c => !settled.Contains(c.Pid)).GroupBy(c => c.Pid))
        {
            var winner = group.OrderByDescending(c => c.TranscriptWrite).First();
            live.TryAdd(winner.Id, winner.ToSession());
        }
        return [.. live.Values];
    }

    // The session a process is really in when its command line still names one it has
    // since left: /resume inside the CLI switches sessions without rewriting
    // `--session-id`, so the named one stops at its first events while the resumed
    // transcript grows under a second lock the same process holds. A lock folder
    // displaces the named one only when this run of the pid wrote it (the lock is newer
    // than the process, so not a recycled pid's leftover), and its transcript is newer
    // than both the named session's and the process start.
    static Candidate ResolveResume(Candidate named, List<Candidate> candidates, DateTime? startedAt)
    {
        if (startedAt is not { } start) { return named; }
        return candidates
            .Where(c => c.Pid == named.Pid && !string.Equals(c.Id, named.Id, StringComparison.OrdinalIgnoreCase)
                && c.HasTranscript && c.LockWrite >= start && c.TranscriptWrite > named.TranscriptWrite && c.TranscriptWrite > start)
            .OrderByDescending(c => c.TranscriptWrite)
            .FirstOrDefault() ?? named;
    }

    sealed record Candidate(string Id, string Directory, int Pid, DateTime LockWrite, bool HasTranscript, DateTime TranscriptWrite)
    {
        public static Candidate Of(string dir, int pid, DateTime lockWrite)
        {
            var transcript = Path.Combine(dir, "events.jsonl");
            var has = File.Exists(transcript);
            // A session that has not taken its first turn has no transcript yet; it is
            // still live and still shown, and loses any per-pid tie-break.
            return new Candidate(Path.GetFileName(dir), dir, pid, lockWrite, has, has ? LastWrite(transcript) : DateTime.MinValue);
        }

        public DiscoveredSession ToSession() =>
            new(Id, AgentKind.Copilot, Path.Combine(Directory, "events.jsonl"), Directory, Pid);
    }

    static DateTime LastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    internal static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try { return Directory.EnumerateFiles(path, pattern).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}

internal static class RegistrationDiscovery
{
    // Live Claude or Codex sessions from agent-ha-bridge's hook registrations. A session
    // is live while its recorded process is still a running agent process, however long
    // it has been quiet (an idle limit retired sessions waiting out a usage cap), and,
    // for Codex, until its SessionEnd marked the registration Ended.
    public static IReadOnlyList<DiscoveredSession> Find(
        AgentKind agent, IReadOnlyList<string> roots, IReadOnlyList<AgentProcess> processes, Func<string, string?> findTranscript)
    {
        var running = processes.ToDictionary(p => p.Id);
        var best = new Dictionary<string, Registration>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var registration in Read(agent, root))
            {
                // The same session registered under both the runtime and the legacy temp
                // root: the one written last is the current one.
                if (best.TryGetValue(registration.SessionId, out var other) && (other.Updated ?? DateTimeOffset.MinValue) >= (registration.Updated ?? DateTimeOffset.MinValue)) { continue; }
                best[registration.SessionId] = registration;
            }
        }

        var live = new List<DiscoveredSession>();
        foreach (var r in best.Values)
        {
            if (r.Ended || r.ProcessId <= 0 || !running.TryGetValue(r.ProcessId, out var process)) { continue; }
            // A pid is reused. A registration last written before the process now holding
            // its pid started belongs to a session that is gone, and would otherwise come
            // back, old question and all, for as long as the newcomer runs. A second of
            // slack covers start times the OS rounds.
            if (r.Updated is { } updated && process.StartedAt is { } started && updated < started - TimeSpan.FromSeconds(1)) { continue; }
            // Claude creates its transcript at the first message, so a missing file is a
            // live session with nothing to show yet, not a dead one.
            var transcript = string.IsNullOrWhiteSpace(r.TranscriptPath) ? findTranscript(r.SessionId) : r.TranscriptPath;
            live.Add(new DiscoveredSession(r.SessionId, agent, transcript, null, r.ProcessId, r));
        }
        return live;
    }

    // Lists the root itself, never its parent: agent-ha-bridge's hook spool sits beside
    // these folders and must not even be enumerated.
    static IEnumerable<Registration> Read(AgentKind agent, string root)
    {
        DirectoryInfo directory;
        try
        {
            directory = new DirectoryInfo(root);
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) { yield break; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { yield break; }

        foreach (var file in CopilotDiscovery.SafeEnumerateFiles(root, "*.json"))
        {
            var name = Path.GetFileName(file);
            // Codex keeps approval markers in the same folder; read with their session.
            if (name.EndsWith(".approval.json", StringComparison.OrdinalIgnoreCase)) { continue; }
            var registration = Parse(agent, file);
            if (registration is not null) { yield return registration; }
        }
    }

    internal static Registration? Parse(AgentKind agent, string path)
    {
        using var doc = ReadJson(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) { return null; }
        var e = doc.RootElement;
        var id = Json.Str(e, "SessionId");
        if (string.IsNullOrWhiteSpace(id)) { return null; }
        // A registration is named after its own session; one that is not is a stray or a
        // copy, and believing it would put one session's state on another's card.
        var expected = SafeKey(id) + ".json";
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFileName(path), expected, comparison)) { return null; }

        var pid = Json.Int(e, "ProcessId") ?? 0;
        ApprovalMarker? approval = null;
        if (agent == AgentKind.Codex)
        {
            var markerPath = Path.Combine(Path.GetDirectoryName(path)!, SafeKey(id) + ".approval.json");
            using var marker = File.Exists(markerPath) ? ReadJson(markerPath) : null;
            if (marker is { RootElement.ValueKind: JsonValueKind.Object } m
                && Json.Str(m.RootElement, "DecisionId") is { Length: > 0 } decision)
            {
                approval = new ApprovalMarker(decision, Json.Str(m.RootElement, "Question") ?? "", Json.Time(m.RootElement, "Created"));
            }
        }
        return new Registration(
            id,
            pid,
            Json.Str(e, "TranscriptPath"),
            Json.Str(e, "WorkingDirectory"),
            Json.Time(e, "Updated"),
            Json.Str(e, "HookStatus"),
            Json.Time(e, "HookStatusAt"),
            Json.Str(e, "Model"),
            Json.Str(e, "Status"),
            e.TryGetProperty("Ended", out var ended) && ended.ValueKind == JsonValueKind.True,
            path,
            approval);
    }

    static JsonDocument? ReadJson(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) { return null; }
            return JsonDocument.Parse(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // agent-ha-bridge's file-safe session key: so a hostile id cannot escape the folder.
    internal static string SafeKey(string sessionId)
    {
        var clean = new string(sessionId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray()).TrimStart('.');
        if (string.IsNullOrWhiteSpace(clean)) { return "unknown"; }
        return clean.Length > 96 ? clean[..96] : clean;
    }
}
