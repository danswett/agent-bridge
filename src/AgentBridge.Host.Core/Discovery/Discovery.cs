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
    Registration? Registration = null,
    // When that process started, if the OS would say: work a session began before it
    // belongs to a CLI that has gone, and can never finish.
    DateTimeOffset? ProcessStartedAt = null);

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
// Attempted: agent-ha-bridge has already typed an answer into this generation.
internal sealed record ApprovalMarker(string DecisionId, string Question, DateTimeOffset? Created, bool Attempted);

public enum ApprovalMarkerState { Absent, Unreadable, Present }

// One read of agent-ha-bridge's Codex approval marker, by the rules its own sender applies
// before typing (Set-CodexApprovalAttempt): the marker names this session, carries a
// generation (DecisionId), and an attempt it records is a boolean true. Anything else is
// Unreadable, which a caller must treat as "something may be pending", never as absent.
public sealed record ApprovalMarkerRead(ApprovalMarkerState State, string? DecisionId = null, string? Question = null, DateTimeOffset? Created = null, bool Attempted = false)
{
    public static string PathFor(string registrationPath, string sessionId) =>
        Path.Combine(Path.GetDirectoryName(registrationPath)!, RegistrationDiscovery.SafeKey(sessionId) + ".approval.json");

    public static ApprovalMarkerRead Read(string registrationPath, string sessionId)
    {
        var path = PathFor(registrationPath, sessionId);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) { return new(ApprovalMarkerState.Unreadable); }
            using var doc = JsonDocument.Parse(stream);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) { return new(ApprovalMarkerState.Unreadable); }
            if (!e.TryGetProperty("SessionId", out var owner) || owner.ValueKind != JsonValueKind.String || !string.Equals(owner.GetString(), sessionId, StringComparison.Ordinal))
            {
                return new(ApprovalMarkerState.Unreadable);
            }
            if (!e.TryGetProperty("DecisionId", out var decision) || decision.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(decision.GetString()))
            {
                return new(ApprovalMarkerState.Unreadable);
            }
            var attempted = false;
            if (e.TryGetProperty("DeliveryAttempted", out var tried))
            {
                if (tried.ValueKind != JsonValueKind.True) { return new(ApprovalMarkerState.Unreadable); }
                attempted = true;
            }
            return new(ApprovalMarkerState.Present, decision.GetString(), Json.Str(e, "Question") ?? "", Json.Time(e, "Created"), attempted);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(ApprovalMarkerState.Absent); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(ApprovalMarkerState.Unreadable); }
    }
}

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
                if (!byPid.TryGetValue(pid, out var process)) { continue; }
                var lockWrite = LastWrite(lockFile);
                // A lock written before the process now holding its pid started is a dead
                // CLI's leftover, not evidence about this process: pids are reused, and
                // accepting it brought an ended session back (or let its newer transcript
                // win this pid's tie-break over the session it is really in).
                if (process.StartedAt is { } started && lockWrite != DateTime.MinValue && lockWrite < started.UtcDateTime - TimeSpan.FromSeconds(1)) { continue; }
                // One candidate per live lock: a folder two live CLIs both hold a lock in
                // used to yield only one of them.
                candidates.Add(Candidate.Of(dir, pid, lockWrite));
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
            live[winner.Id] = winner.ToSession(process.StartedAt);
            settled.Add(process.Id);
        }

        // One CLI owns one live session. A process that resumed a different session leaves
        // its old lock behind, so the same pid can hold locks in several folders; showing
        // all of them made phantom sessions. The most recently written transcript wins.
        foreach (var group in candidates.Where(c => !settled.Contains(c.Pid)).GroupBy(c => c.Pid))
        {
            var winner = group.OrderByDescending(c => c.TranscriptWrite).First();
            live.TryAdd(winner.Id, winner.ToSession(byPid[winner.Pid].StartedAt));
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

        public DiscoveredSession ToSession(DateTimeOffset? processStartedAt) =>
            new(Id, AgentKind.Copilot, Path.Combine(Directory, "events.jsonl"), Directory, Pid, ProcessStartedAt: processStartedAt);
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

internal static class ClaudeSessionDiscovery
{
    // Live Claude sessions from Claude Code's own sessions/<pid>.json, written by every
    // interactive CLI (seen with 2.1.312) whether or not any hook is installed. Without
    // this, a Claude session had no card at all unless agent-ha-bridge's hooks had
    // registered it, and the new host registers none.
    public static IReadOnlyList<DiscoveredSession> Find(string claudeHome, IReadOnlyList<AgentProcess> processes, Func<string, string?> findTranscript)
    {
        var root = Path.Combine(claudeHome, "sessions");
        if (processes.Count == 0 || !Directory.Exists(root)) { return []; }
        var running = processes.ToDictionary(p => p.Id);
        var live = new List<DiscoveredSession>();
        foreach (var file in CopilotDiscovery.SafeEnumerateFiles(root, "*.json"))
        {
            if (Parse(file) is not { } entry || !running.TryGetValue(entry.Pid, out var process)) { continue; }
            // A pid is reused, and a CLI that crashed leaves its file behind: one written by
            // a session that began before the process now holding the pid started is not
            // that process's session. A second of slack covers start times the OS rounds.
            if (process.StartedAt is { } started && (entry.StartedAt ?? entry.UpdatedAt) is { } at && at < started - TimeSpan.FromSeconds(1)) { continue; }
            live.Add(new DiscoveredSession(entry.SessionId, AgentKind.Claude, findTranscript(entry.SessionId), null, entry.Pid, ProcessStartedAt: process.StartedAt));
        }
        return live;
    }

    internal sealed record Entry(int Pid, string SessionId, DateTimeOffset? StartedAt, DateTimeOffset? UpdatedAt);

    internal static Entry? Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var filePid) || filePid <= 0) { return null; }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024) { return null; }
            using var doc = JsonDocument.Parse(stream);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) { return null; }
            // Named after its own process; one that is not is a stray or a copy.
            if (Json.Int(e, "pid") != filePid) { return null; }
            var id = Json.Str(e, "sessionId");
            if (string.IsNullOrWhiteSpace(id) || RegistrationDiscovery.SafeKey(id) != id) { return null; }
            // Only a CLI at a console can be typed into; an SDK or print-mode run is not
            // a session a person drives.
            if (Json.Str(e, "kind") is { } kind && kind != "interactive") { return null; }
            return new Entry(filePid, id, Millis(e, "startedAt"), Millis(e, "updatedAt"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static DateTimeOffset? Millis(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms) && ms > 0 && ms < 253402300800000
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
}

// What was decided about one thread lock, and when: the terminal holding it (Pid, with its
// start), or no session (Pid 0) given the terminals running then (Terminals).
internal sealed record CodexLockHolding(DateTime Created, int Pid, DateTimeOffset? Started, DateTimeOffset Checked, string Terminals);

internal static class CodexLockDiscovery
{
    // How long a decision about a lock stands before the OS is asked again. A terminal can let
    // a thread go while it keeps running (another thread taken, the lock left behind), so even
    // a holder still running is checked again; and a lock no terminal held may be taken later.
    public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);

    // Live Codex sessions from Codex's own thread-writer-locks/<thread id>.lock, which the
    // process writing a thread's rollout holds open while it runs (seen with 0.165). Run
    // with --no-daemon, as every launch from this host is, that is the terminal codex.exe
    // itself, so the lock binds the session to the process typed into. Run the default way,
    // the shared app-server daemon holds it on behalf of any number of terminals, so it
    // names no window, and such a session is left to agent-ha-bridge's hook. Without this, a
    // Codex session launched from a device had no card on a machine without that hook.
    // A second terminal resuming a thread another holds does not take its lock: it says "This
    // conversation is open in another app" and waits (0.165, seen live).
    public static IReadOnlyList<DiscoveredSession> Find(
        string codexHome, IReadOnlyList<AgentProcess> processes, Func<string, IReadOnlyList<int>> holders,
        Dictionary<string, CodexLockHolding> known, Func<string, string?> findTranscript, DateTimeOffset now)
    {
        var root = Path.Combine(codexHome, "thread-writer-locks");
        // Only a terminal can hold a session's lock for it. With none running (the daemon and
        // its helpers, or processes not yet read), nobody is asked about any lock.
        var terminals = processes.Where(p => IsTerminal(p.CommandLine)).OrderBy(p => p.Id).ToList();
        if (terminals.Count == 0 || !Directory.Exists(root)) { known.Clear(); return []; }
        var signature = string.Join(',', terminals.Select(p => $"{p.Id}@{p.StartedAt?.ToUnixTimeMilliseconds()}"));
        var running = processes.ToDictionary(p => p.Id);
        var held = new List<(string Id, AgentProcess Process)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in CopilotDiscovery.SafeEnumerateFiles(root, "*.lock"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParse(id, out _) || RegistrationDiscovery.SafeKey(id) != id) { continue; }
            seen.Add(file);
            DateTime created;
            try { created = File.GetCreationTimeUtc(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            // A decision stands for Recheck, and only while what it rested on holds: the holder
            // still the same process (its pid *and* start time, since a pid reused by a later
            // process would otherwise inherit the session), or, where nobody was taken, the same
            // terminals running, so a new one, or one whose command line can now be read, is
            // asked about at once.
            if (!(known.TryGetValue(file, out var holding) && holding.Created == created && now - holding.Checked < Recheck
                && (holding.Pid == 0
                    ? holding.Terminals == signature
                    : running.TryGetValue(holding.Pid, out var cached) && cached.StartedAt == holding.Started)))
            {
                holding = new CodexLockHolding(created, Holder(holders(file), running) ?? 0, null, now, signature);
                if (holding.Pid != 0) { holding = holding with { Started = running[holding.Pid].StartedAt }; }
                // A holder whose start is unknown cannot be told from a successor, so it is
                // asked about afresh on every pass rather than remembered.
                if (holding.Pid == 0 || holding.Started is not null) { known[file] = holding; }
                else { known.Remove(file); }
            }
            if (holding.Pid != 0) { held.Add((id, running[holding.Pid])); }
        }
        foreach (var gone in known.Keys.Where(k => !seen.Contains(k)).ToList()) { known.Remove(gone); }

        // One terminal is one session. A terminal holding several locks may be writing a
        // sub-agent's thread or touching another thread's rollout, and a launch bound by pid
        // must not be handed the wrong one: it is a session only through the one lock whose
        // rollout is its own top-level thread, written since it started; otherwise none.
        var live = new List<DiscoveredSession>();
        foreach (var group in held.GroupBy(h => h.Process.Id))
        {
            var candidates = group.Select(h => (h.Id, h.Process, Transcript: findTranscript(h.Id))).ToList();
            if (candidates.Count > 1)
            {
                candidates = [.. candidates.Where(c => c.Transcript is { } t && CodexRollout.IsOwnThread(t, c.Id, c.Process.StartedAt))];
                if (candidates.Count != 1) { continue; }
            }
            var (id, process, transcript) = candidates[0];
            live.Add(new DiscoveredSession(id, AgentKind.Codex, transcript, null, process.Id, ProcessStartedAt: process.StartedAt));
        }
        return live;
    }

    // The one process to bind a lock to: every running Codex process holding it counts, so a
    // lock a terminal shares with the daemon, or with a Codex whose command line could not be
    // read (which may be the daemon, whose stop would end every session it serves), names
    // nobody. Exactly one holder, and a terminal.
    static int? Holder(IReadOnlyList<int> pids, Dictionary<int, AgentProcess> running)
    {
        var codex = pids.Where(running.ContainsKey).Distinct().ToList();
        return codex.Count == 1 && IsTerminal(running[codex[0]].CommandLine) ? codex[0] : null;
    }

    // A Codex process known to be a terminal: its command line read, and not an app-server.
    internal static bool IsTerminal(string? commandLine) => commandLine is { Length: > 0 } && !IsAppServer(commandLine);

    // Codex's app-server or MCP server: the daemon, its updater, or one an IDE runs. Never a
    // terminal. Global options may come before the subcommand (`codex -c k=v app-server`), so
    // any argument naming one counts: a terminal mistaken for a server is only not found, while
    // a server mistaken for a terminal could be stopped, ending every session it serves.
    internal static bool IsAppServer(string? commandLine) =>
        CommandLine.Split(commandLine).Skip(1).Any(a => a is "app-server" or "mcp-server");
}

// What a Codex rollout says about whose thread it is.
internal static class CodexRollout
{
    const int MaxHeadBytes = 256 * 1024;
    const int TailBytes = 64 * 1024;

    // Whether a rollout is a person's own top-level thread, written since `started`: its
    // session_meta names this thread (id, and session_id where given) as one a user started
    // (thread_source "user" and a plain source where given; 0.165 writes both, seen live), and
    // its last event is no older than the process. Anything unread, a sub-agent's thread, or
    // one only touched by this process, is not. The last event, not the file's write time,
    // which Windows leaves stale while the writer holds the file open (seen live).
    public static bool IsOwnThread(string path, string id, DateTimeOffset? started)
    {
        if (started is not { } since) { return false; }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (FirstLine(stream) is not { } head || !TopLevel(head, id)) { return false; }
            return LastTimestamp(stream) is { } last && last >= since - TimeSpan.FromSeconds(1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static bool TopLevel(byte[] line, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (Json.Str(e, "type") != "session_meta" || Json.Obj(e, "payload") is not { } p) { return false; }
            if (!string.Equals(Json.Str(p, "id"), id, StringComparison.OrdinalIgnoreCase)) { return false; }
            if (Json.Get(p, "session_id") is { } session && !(session.ValueKind == JsonValueKind.String && string.Equals(session.GetString(), id, StringComparison.OrdinalIgnoreCase))) { return false; }
            if (Json.Get(p, "thread_source") is { } thread && !(thread.ValueKind == JsonValueKind.String && thread.GetString() == "user")) { return false; }
            return Json.Get(p, "source") is not { } source || source.ValueKind == JsonValueKind.String;
        }
        catch (JsonException) { return false; }
    }

    static byte[]? FirstLine(FileStream stream)
    {
        var buffer = new byte[Math.Min(MaxHeadBytes, stream.Length)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
        return end < 0 ? null : buffer[..end];
    }

    static DateTimeOffset? LastTimestamp(FileStream stream)
    {
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read == 0) { return null; }
        // The last complete line: a trailing one still being written is passed over.
        var end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        while (end > 0)
        {
            var begin = Array.LastIndexOf(buffer, (byte)'\n', end - 1) + 1;
            if (begin == 0 && start > 0) { return null; }
            try
            {
                using var doc = JsonDocument.Parse(buffer.AsMemory(begin, end - begin));
                return Json.Time(doc.RootElement, "timestamp");
            }
            catch (JsonException) { end = begin - 1; }
        }
        return null;
    }
}

internal static class RegistrationDiscovery
{
    // Live Claude or Codex sessions from agent-ha-bridge's hook registrations. A session
    // is live while its recorded process is still a running agent process, however long
    // it has been quiet (an idle limit retired sessions waiting out a usage cap), and,
    // for Codex, until its SessionEnd marked the registration Ended.
    public static IReadOnlyList<DiscoveredSession> Find(
        AgentKind agent, IReadOnlyList<string> roots, IReadOnlyList<AgentProcess> processes, Func<string, string?> findTranscript,
        ISet<string>? ended = null)
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
            if (r.ProcessId <= 0 || !running.TryGetValue(r.ProcessId, out var process)) { continue; }
            // A pid is reused. A registration last written before the process now holding
            // its pid started belongs to a session that is gone, and would otherwise come
            // back, old question and all, for as long as the newcomer runs. A second of
            // slack covers start times the OS rounds.
            if (r.Updated is { } updated && process.StartedAt is { } started && updated < started - TimeSpan.FromSeconds(1)) { continue; }
            // An ended session whose process still runs is not a session, and other ways of
            // finding one (a thread lock) are told so rather than bringing it back.
            if (r.Ended) { ended?.Add(r.SessionId); continue; }
            // Claude creates its transcript at the first message, so a missing file is a
            // live session with nothing to show yet, not a dead one.
            var transcript = string.IsNullOrWhiteSpace(r.TranscriptPath) ? findTranscript(r.SessionId) : r.TranscriptPath;
            live.Add(new DiscoveredSession(r.SessionId, agent, transcript, null, r.ProcessId, r, process.StartedAt));
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
        if (agent == AgentKind.Codex
            && ApprovalMarkerRead.Read(path, id) is { State: ApprovalMarkerState.Present } marker)
        {
            approval = new ApprovalMarker(marker.DecisionId!, marker.Question ?? "", marker.Created, marker.Attempted);
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
