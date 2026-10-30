using System.Collections.Concurrent;
using System.Text;
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
// hooks. Only read here: agent-ha-bridge prunes its own files, and on a machine migrated from
// it the host's intake writes and prunes them in the same shapes (Intake/HookHandlers).
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
    //
    // Anything it could not read is added to holds, in words: a folder it could not list may
    // hold the lock of a session that is still running.
    //
    // unidentified are the runtimes whose command line could not be read: a lock one of them
    // wrote is a live session discovery cannot see, so it holds rather than being dropped.
    public static IReadOnlyList<DiscoveredSession> Find(string copilotHome, IReadOnlyList<AgentProcess> processes, ICollection<string>? holds = null, IReadOnlyList<AgentProcess>? unidentified = null)
    {
        unidentified ??= [];
        var root = Path.Combine(copilotHome, "session-state");
        if (processes.Count == 0 && unidentified.Count == 0) { return []; }
        switch (ProbeFolder(root))
        {
            case Folder.Absent: return [];
            case Folder.Link: holds?.Add("copilot's session folder is a link"); return [];
            case Folder.Unreadable: holds?.Add("copilot's session folders could not be read"); return [];
        }
        var byPid = processes.ToDictionary(p => p.Id);

        var candidates = new List<Candidate>();
        if (!TryEnumerateFolders(root, out var folders)) { holds?.Add("copilot's session folders could not be listed"); }
        // A linked session folder is not one Copilot made, and what it points at is unknown: its
        // locks may be a live session's, or another folder's seen twice (agent-ha-bridge's
        // workspace usage refuses on one too).
        if (folders.Any(f => f.Link)) { holds?.Add("a copilot session folder is a link"); }
        foreach (var dir in folders.Where(f => !f.Link).Select(f => f.Path))
        {
            if (!TryEnumerateFiles(dir, "inuse.*.lock", out var locks)) { holds?.Add("a copilot session folder could not be listed"); }
            foreach (var lockFile in locks)
            {
                var name = Path.GetFileName(lockFile);
                var digits = name.AsSpan("inuse.".Length, name.Length - "inuse.".Length - ".lock".Length);
                if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)) { continue; }
                var lockWrite = LastWrite(lockFile);
                if (!byPid.TryGetValue(pid, out var process))
                {
                    if (unidentified.FirstOrDefault(u => u.Id == pid) is { } unread && OfGeneration(lockWrite, unread))
                    {
                        holds?.Add($"a copilot process that could not be read (pid {pid})");
                    }
                    continue;
                }
                if (!OfGeneration(lockWrite, process)) { continue; }
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
            var winner = candidates.FirstOrDefault(c => c.Pid == process.Id && string.Equals(c.Id, named, StringComparison.OrdinalIgnoreCase));
            if (winner is null)
            {
                switch (ProbeFolder(dir))
                {
                    case Folder.Present: winner = Candidate.Of(dir, process.Id, DateTime.MinValue); break;
                    case Folder.Link: holds?.Add("a copilot session folder is a link"); break;
                    case Folder.Unreadable: holds?.Add("a copilot session folder could not be read"); break;
                }
            }
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

    // A lock written before the process now holding its pid started is a dead CLI's leftover,
    // not evidence about this process: pids are reused, and accepting it brought an ended
    // session back (or let its newer transcript win this pid's tie-break over the session it
    // is really in).
    static bool OfGeneration(DateTime lockWrite, AgentProcess process) =>
        process.StartedAt is not { } started || lockWrite == DateTime.MinValue || lockWrite >= started.UtcDateTime - TimeSpan.FromSeconds(1);

    static DateTime LastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    internal static IEnumerable<string> SafeEnumerateDirectories(string path) => TryEnumerateDirectories(path, out var found) ? found : [];

    internal enum Folder { Absent, Present, Link, Unreadable }

    // What is at a path discovery is about to read. Only the OS saying it is not there is
    // absence: Directory.Exists is false for a folder it may not look into as well, which read
    // an unreadable registration root as an agent with no sessions and retired them all.
    // Anything there but a plain folder is Unreadable.
    internal static Folder ProbeFolder(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) { return Folder.Link; }
            return attributes.HasFlag(FileAttributes.Directory) ? Folder.Present : Folder.Unreadable;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Folder.Absent; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return Folder.Unreadable; }
    }

    // The subfolders, each with whether it is a link. The attributes come with the listing.
    internal static bool TryEnumerateFolders(string path, out IReadOnlyList<(string Path, bool Link)> found)
    {
        try
        {
            found = new DirectoryInfo(path).EnumerateDirectories().Select(d => (d.FullName, d.Attributes.HasFlag(FileAttributes.ReparsePoint))).ToList();
            return true;
        }
        catch (DirectoryNotFoundException) { found = []; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { found = []; return false; }
    }

    // False when the folder exists but could not be listed: not the same as empty. One that
    // has gone meanwhile is empty.
    internal static bool TryEnumerateDirectories(string path, out IReadOnlyList<string> found)
    {
        try { found = Directory.EnumerateDirectories(path).ToList(); return true; }
        catch (DirectoryNotFoundException) { found = []; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { found = []; return false; }
    }

    internal static bool TryEnumerateFiles(string path, string pattern, out IReadOnlyList<string> found)
    {
        try { found = Directory.EnumerateFiles(path, pattern).ToList(); return true; }
        catch (DirectoryNotFoundException) { found = []; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { found = []; return false; }
    }
}

internal static class ClaudeSessionDiscovery
{
    // Live Claude sessions from Claude Code's own sessions/<pid>.json, written by every
    // interactive CLI (seen with 2.1.312) whether or not any hook is installed. Without
    // this, a Claude session had no card at all unless agent-ha-bridge's hooks had
    // registered it, and the new host registers none.
    //
    // Into holds and accounted as RegistrationDiscovery: a file for a running (or unreadable)
    // process of its generation that could not be read is a session discovery cannot see, and
    // one that says it is an SDK or print-mode run accounts for its process as nobody's session.
    public static IReadOnlyList<DiscoveredSession> Find(string claudeHome, IReadOnlyList<AgentProcess> processes, Func<string, string?> findTranscript,
        ICollection<string>? holds = null, ISet<int>? accounted = null, IReadOnlyList<AgentProcess>? unidentified = null)
    {
        var root = Path.Combine(claudeHome, "sessions");
        unidentified ??= [];
        if (processes.Count == 0 && unidentified.Count == 0) { return []; }
        switch (CopilotDiscovery.ProbeFolder(root))
        {
            case CopilotDiscovery.Folder.Absent: return [];
            case CopilotDiscovery.Folder.Link: holds?.Add("claude's sessions folder is a link"); return [];
            case CopilotDiscovery.Folder.Unreadable: holds?.Add("claude's sessions folder could not be read"); return [];
        }
        var running = processes.ToDictionary(p => p.Id);
        var unread = unidentified.ToDictionary(p => p.Id);
        var live = new List<DiscoveredSession>();
        if (!CopilotDiscovery.TryEnumerateFiles(root, "*.json", out var files)) { holds?.Add("claude's sessions folder could not be listed"); }
        foreach (var file in files)
        {
            var parsed = Parse(file, out var filePid);
            var holder = running.TryGetValue(filePid, out var p) ? p : unread.TryGetValue(filePid, out var u) ? u : null;
            if (holder is null) { continue; }
            if (parsed is null)
            {
                // Unreadable, or not a whole file of its own process: the file's time is all
                // there is to tell whether it is this process's or a crashed one's leftover.
                if (Written(file) is not { } written || holder.StartedAt is not { } began || written >= began - TimeSpan.FromSeconds(1))
                {
                    holds?.Add($"a claude session file could not be read (pid {filePid})");
                }
                continue;
            }
            var entry = parsed;
            // A pid is reused, and a CLI that crashed leaves its file behind: one written by
            // a session that began before the process now holding the pid started is not
            // that process's session. A second of slack covers start times the OS rounds.
            if (holder.StartedAt is { } started && (entry.StartedAt ?? entry.UpdatedAt) is { } at && at < started - TimeSpan.FromSeconds(1)) { continue; }
            if (!running.ContainsKey(filePid))
            {
                holds?.Add($"a claude process that could not be read (pid {filePid})");
                continue;
            }
            accounted?.Add(filePid);
            if (!entry.Interactive) { continue; }
            live.Add(new DiscoveredSession(entry.SessionId, AgentKind.Claude, findTranscript(entry.SessionId), null, entry.Pid, ProcessStartedAt: holder.StartedAt));
        }
        return live;
    }

    static DateTimeOffset? Written(string path)
    {
        try { return File.GetLastWriteTimeUtc(path) is var t && t.Year > 1601 ? new DateTimeOffset(t, TimeSpan.Zero) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal sealed record Entry(int Pid, string SessionId, DateTimeOffset? StartedAt, DateTimeOffset? UpdatedAt, bool Interactive = true);

    internal static Entry? Parse(string path) => Parse(path, out _) is { Interactive: true } entry ? entry : null;

    // Null when the file could not be read whole, or is not one of its own process; pid is
    // the process its name gives, 0 when it names none. A file of a run that is not interactive
    // is returned, so its process is known to be nobody's session.
    internal static Entry? Parse(string path, out int pid)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pid) || pid <= 0) { pid = 0; return null; }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024) { return null; }
            using var doc = JsonDocument.Parse(stream);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) { return null; }
            // Named after its own process; one that is not is a stray or a copy.
            if (Json.Int(e, "pid") != pid) { return null; }
            var id = Json.Str(e, "sessionId");
            if (string.IsNullOrWhiteSpace(id) || RegistrationDiscovery.SafeKey(id) != id) { return null; }
            // Only a CLI at a console can be typed into; an SDK or print-mode run is not
            // a session a person drives.
            var interactive = Json.Str(e, "kind") is not { } kind || kind == "interactive";
            return new Entry(pid, id, Millis(e, "startedAt"), Millis(e, "updatedAt"), interactive);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { pid = 0; return null; }
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
        Dictionary<string, CodexLockHolding> known, Func<string, string?> findTranscript, DateTimeOffset now, ICollection<string>? holds = null)
    {
        var root = Path.Combine(codexHome, "thread-writer-locks");
        // Only a terminal can hold a session's lock for it. With none running (the daemon and
        // its helpers, or processes not yet read), nobody is asked about any lock.
        var terminals = processes.Where(p => IsTerminal(p.CommandLine)).OrderBy(p => p.Id).ToList();
        if (terminals.Count == 0) { known.Clear(); return []; }
        // A folder or lock that cannot be read may be a running terminal's session: it holds,
        // as for registrations, rather than reading as none.
        switch (CopilotDiscovery.ProbeFolder(root))
        {
            case CopilotDiscovery.Folder.Absent: known.Clear(); return [];
            case CopilotDiscovery.Folder.Link: holds?.Add("codex's thread lock folder is a link"); return [];
            case CopilotDiscovery.Folder.Unreadable: holds?.Add("codex's thread lock folder could not be read"); return [];
        }
        var signature = string.Join(',', terminals.Select(p => $"{p.Id}@{p.StartedAt?.ToUnixTimeMilliseconds()}"));
        var running = processes.ToDictionary(p => p.Id);
        var held = new List<(string Id, AgentProcess Process)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!CopilotDiscovery.TryEnumerateFiles(root, "*.lock", out var files)) { holds?.Add("codex's thread lock folder could not be listed"); }
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParse(id, out _) || RegistrationDiscovery.SafeKey(id) != id) { continue; }
            seen.Add(file);
            DateTime created;
            try { created = File.GetCreationTimeUtc(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { holds?.Add("a codex thread lock could not be read"); continue; }

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
        // rollout is its own top-level thread, written since it started; otherwise none. Even
        // a lone lock is not its session when its rollout says it is a sub-agent's (its own
        // thread's lock may be the one shared, and so named nobody); one not yet written is.
        var live = new List<DiscoveredSession>();
        foreach (var group in held.GroupBy(h => h.Process.Id))
        {
            var candidates = group.Select(h => (h.Id, h.Process, Transcript: findTranscript(h.Id))).ToList();
            if (candidates.Count > 1)
            {
                candidates = [.. candidates.Where(c => c.Transcript is { } t && CodexRollout.IsOwnThread(t, c.Id, c.Process.StartedAt))];
                if (candidates.Count != 1) { continue; }
            }
            else if (candidates[0].Transcript is { } t && CodexRollout.Thread(t, candidates[0].Id) == CodexThread.Other) { continue; }
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

    // A Codex process known to be a terminal a person drives: its command line read, not a
    // server, and the interactive CLI (no subcommand, a prompt, `resume` or `fork`). `exec`,
    // `review`, `migrate-rollouts` and the like can hold a thread's lock too, with no screen
    // to type into.
    internal static bool IsTerminal(string? commandLine) => commandLine is { Length: > 0 } && !IsAppServer(commandLine) && IsInteractive(commandLine);

    // Codex's app-server or MCP server: the daemon, its updater, or one an IDE runs. Never a
    // terminal. Global options may come before the subcommand (`codex -c k=v app-server`), so
    // any argument naming one counts: a terminal mistaken for a server is only not found, while
    // a server mistaken for a terminal could be stopped, ending every session it serves.
    internal static bool IsAppServer(string? commandLine) =>
        CommandLine.Split(commandLine).Skip(1).Any(a => a is "app-server" or "mcp-server");

    // Global options that take a value (codex-rs cli/src/main.rs and tui/src/cli.rs), so a value
    // is never read as the subcommand.
    static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-c", "--config", "--enable", "--disable", "--remote", "--remote-auth-token-env", "-a", "--ask-for-approval",
        "-m", "--model", "-p", "--profile", "-s", "--sandbox", "-C", "--cd", "-i", "--image", "--add-dir", "--local-provider",
    };

    // Every subcommand but the interactive ones, with their aliases. The first argument that is
    // not an option is the subcommand if it names one, else the prompt, as clap reads it; after
    // `--` everything is the prompt.
    static readonly HashSet<string> Subcommands = new(StringComparer.Ordinal)
    {
        "agents", "tcp-tunnel", "exec", "e", "review", "login", "logout", "mcp", "mcp-server", "plugin", "app-server",
        "remote-control", "app", "completion", "update", "doctor", "sandbox", "debug", "execpolicy", "apply", "a", "queue",
        "archive", "delete", "migrate-rollouts", "migrate-nfs-db", "unarchive", "cloud", "cloud-tasks",
        "responses-api-proxy", "stdio-to-uds", "exec-server", "features", "help",
    };

    internal static bool IsInteractive(string commandLine)
    {
        var args = CommandLine.Split(commandLine).Skip(1).ToList();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--") { return true; }
            if (a.StartsWith('-'))
            {
                if (ValueOptions.Contains(a)) { i++; }
                continue;
            }
            return !Subcommands.Contains(a);
        }
        return true;
    }
}

internal enum CodexThread { Unknown, TopLevel, Other }

// What a Codex rollout says about whose thread it is.
internal static class CodexRollout
{
    // A session_meta line carries the base instructions, so it can run to tens of KiB.
    const int MaxHeadBytes = 1024 * 1024;
    // How far back the last complete record is looked for: a tool result can make one
    // record megabytes long (Jsonl's limit for one line).
    const long MaxScanBytes = 32L << 20;
    const int Chunk = 64 * 1024;
    static readonly byte[] TimestampPrefix = "{\"timestamp\":\""u8.ToArray();

    // A rollout's session_meta never changes once written, so what it said is kept.
    static readonly ConcurrentDictionary<string, CodexThread> Known = new(StringComparer.OrdinalIgnoreCase);

    // Whether a rollout is a person's own top-level thread, written since `started`: its
    // session_meta names this thread as one a user started, and its last event is no older
    // than the process. The last event, not the file's write time, which Windows leaves stale
    // while the writer holds the file open (seen live).
    public static bool IsOwnThread(string path, string id, DateTimeOffset? started)
    {
        if (started is not { } since || Thread(path, id) != CodexThread.TopLevel) { return false; }
        try
        {
            using var stream = Open(path);
            return LastTimestamp(stream) is { } last && last >= since - TimeSpan.FromSeconds(1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // TopLevel: session_meta names this thread (id, and session_id where given) as one a user
    // started (thread_source "user" and a plain source where given; 0.165 writes both, seen
    // live). Other: it names anything else, a sub-agent's thread among them. Unknown: no
    // session_meta can be read yet.
    public static CodexThread Thread(string path, string id)
    {
        if (Known.TryGetValue(path, out var kind)) { return kind; }
        try
        {
            using var stream = Open(path);
            if (FirstLine(stream) is not { } head) { return CodexThread.Unknown; }
            kind = Classify(head, id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return CodexThread.Unknown; }
        if (kind != CodexThread.Unknown)
        {
            if (Known.Count > 4096) { Known.Clear(); }
            Known[path] = kind;
        }
        return kind;
    }

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static CodexThread Classify(byte[] line, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (Json.Str(e, "type") != "session_meta" || Json.Obj(e, "payload") is not { } p) { return CodexThread.Unknown; }
            if (!string.Equals(Json.Str(p, "id"), id, StringComparison.OrdinalIgnoreCase)) { return CodexThread.Other; }
            if (Json.Get(p, "session_id") is { } session && !(session.ValueKind == JsonValueKind.String && string.Equals(session.GetString(), id, StringComparison.OrdinalIgnoreCase))) { return CodexThread.Other; }
            if (Json.Get(p, "thread_source") is { } thread && !(thread.ValueKind == JsonValueKind.String && thread.GetString() == "user")) { return CodexThread.Other; }
            return Json.Get(p, "source") is not { } source || source.ValueKind == JsonValueKind.String ? CodexThread.TopLevel : CodexThread.Other;
        }
        catch (JsonException) { return CodexThread.Unknown; }
    }

    static byte[]? FirstLine(FileStream stream)
    {
        var buffer = new List<byte>(Chunk);
        var chunk = new byte[Chunk];
        int read;
        while (buffer.Count < MaxHeadBytes && (read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            var end = Array.IndexOf(chunk, (byte)'\n', 0, read);
            if (end >= 0) { buffer.AddRange(chunk.AsSpan(0, end)); return [.. buffer]; }
            buffer.AddRange(chunk.AsSpan(0, read));
        }
        return null;
    }

    // The timestamp of the last complete record, a trailing one still being written passed
    // over. Codex writes it first in each record, so only a record's head is read; one written
    // otherwise is parsed whole if it is not too large.
    internal static DateTimeOffset? LastTimestamp(FileStream stream)
    {
        var floor = Math.Max(0, stream.Length - MaxScanBytes);
        if (PreviousNewline(stream, stream.Length, floor) is not { } end) { return null; }
        for (var records = 0; records < 8; records++)
        {
            var before = PreviousNewline(stream, end, floor);
            if (before is null && floor > 0) { return null; }
            var begin = before + 1 ?? 0;
            if (Timestamp(stream, begin, end) is { } at) { return at; }
            if (before is not { } next) { return null; }
            end = next;
        }
        return null;
    }

    // The last '\n' at or after `floor` and before `before`, read backwards a chunk at a time.
    static long? PreviousNewline(FileStream stream, long before, long floor)
    {
        var chunk = new byte[Chunk];
        for (var position = before; position > floor;)
        {
            var count = (int)Math.Min(Chunk, position - floor);
            position -= count;
            stream.Seek(position, SeekOrigin.Begin);
            var read = stream.ReadAtLeast(chunk.AsSpan(0, count), count, throwOnEndOfStream: false);
            if (read <= 0) { return null; }
            var at = Array.LastIndexOf(chunk, (byte)'\n', read - 1, read);
            if (at >= 0) { return position + at; }
        }
        return null;
    }

    static DateTimeOffset? Timestamp(FileStream stream, long begin, long end)
    {
        var length = end - begin;
        if (length <= 0) { return null; }
        stream.Seek(begin, SeekOrigin.Begin);
        var head = new byte[Math.Min(length, 512)];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var span = head.AsSpan(0, read);
        if (span.StartsWith(TimestampPrefix))
        {
            var value = span[TimestampPrefix.Length..];
            var close = value.IndexOf((byte)'"');
            return close < 0 ? null : Json.ParseTime(Encoding.UTF8.GetString(value[..close]));
        }
        if (length > Chunk * 16) { return null; }
        var line = new byte[length];
        stream.Seek(begin, SeekOrigin.Begin);
        read = stream.ReadAtLeast(line, line.Length, throwOnEndOfStream: false);
        try
        {
            using var doc = JsonDocument.Parse(line.AsMemory(0, read));
            return Json.Time(doc.RootElement, "timestamp");
        }
        catch (JsonException) { return null; }
    }
}
internal static class RegistrationDiscovery
{
    // Live Claude or Codex sessions from agent-ha-bridge's hook registrations. A session
    // is live while its recorded process is still a running agent process, however long
    // it has been quiet (an idle limit retired sessions waiting out a usage cap), and,
    // for Codex, until its SessionEnd marked the registration Ended.
    //
    // Anything it could not read is added to holds, in words, and every running process a
    // readable registration names (live, ended or stale) to accounted: agent-ha-bridge's
    // Read-DaemonAdapterRegistrations. A registration it could not read may be a live session's.
    public static IReadOnlyList<DiscoveredSession> Find(
        AgentKind agent, IReadOnlyList<string> roots, IReadOnlyList<AgentProcess> processes, Func<string, string?> findTranscript,
        ICollection<string>? holds = null, ISet<int>? accounted = null, Action<string>? afterRead = null, IReadOnlyList<AgentProcess>? unidentified = null,
        ISet<string>? ended = null)
    {
        var name = agent.ToString().ToLowerInvariant();
        var running = processes.ToDictionary(p => p.Id);
        var unread = (unidentified ?? []).ToDictionary(p => p.Id);
        var best = new Dictionary<string, Registration>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var registration in Read(agent, root, problem => holds?.Add($"a {name} {problem}"), afterRead))
            {
                // Only the generation it was written by: a process that took an old registration's
                // pid is a session yet to register, which is exactly what the hold is for.
                if (running.TryGetValue(registration.ProcessId, out var holder) && OfGeneration(registration, holder)) { accounted?.Add(registration.ProcessId); }
                // A live session whose runtime's command line could not be read: not one that ended.
                else if (!registration.Ended && unread.TryGetValue(registration.ProcessId, out var runtime) && OfGeneration(registration, runtime))
                {
                    holds?.Add($"a {name} process that could not be read (pid {registration.ProcessId})");
                }
                if (!registration.Ended && registration.ProcessId <= 0) { holds?.Add($"a {name} registration names no process"); continue; }
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
            if (!OfGeneration(r, process)) { continue; }
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

    // A pid is reused. A registration last written before the process now holding its pid
    // started belongs to a session that is gone, and would otherwise come back, old question
    // and all, for as long as the newcomer runs. A second of slack covers start times the OS
    // rounds.
    static bool OfGeneration(Registration registration, AgentProcess process) =>
        registration.Updated is not { } updated || process.StartedAt is not { } started || updated >= started - TimeSpan.FromSeconds(1);

    // Lists the root itself, never its parent: agent-ha-bridge's hook spool sits beside
    // these folders and must not even be enumerated. What it cannot read goes to problem.
    static IEnumerable<Registration> Read(AgentKind agent, string root, Action<string> problem, Action<string>? afterRead)
    {
        switch (CopilotDiscovery.ProbeFolder(root))
        {
            case CopilotDiscovery.Folder.Absent: yield break;
            // A linked folder is not one agent-ha-bridge made; what it points at is unknown.
            case CopilotDiscovery.Folder.Link: problem("registration folder is a link"); yield break;
            case CopilotDiscovery.Folder.Unreadable: problem("registration folder could not be read"); yield break;
        }

        if (!CopilotDiscovery.TryEnumerateFiles(root, "*.json", out var files)) { problem("registration folder could not be listed"); }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            // Codex keeps approval markers in the same folder; read with their session.
            if (name.EndsWith(".approval.json", StringComparison.OrdinalIgnoreCase)) { continue; }
            var registration = Parse(agent, file, out var why, afterRead);
            if (registration is not null) { yield return registration; }
            else if (why is not null) { problem(why); }
        }
    }

    internal static Registration? Parse(AgentKind agent, string path) => Parse(agent, path, out _);

    // Null with why when it could not be read as a whole registration of its own session; null
    // and no why when it has gone. afterRead runs between the read and the check that the file
    // did not change under it, for tests.
    internal static Registration? Parse(AgentKind agent, string path, out string? why, Action<string>? afterRead = null)
    {
        why = "registration could not be read";
        var before = Stamp(path);
        if (before is null) { why = null; return null; }
        if (before == Linked) { why = "registration is a link"; return null; }
        if (before == Unreadable) { return null; }
        using var doc = ReadJson(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) { return null; }
        var e = doc.RootElement;
        var id = Json.Str(e, "SessionId");
        if (string.IsNullOrWhiteSpace(id)) { return null; }
        // A registration is named after its own session; one that is not is a stray or a
        // copy, and believing it would put one session's state on another's card.
        var expected = SafeKey(id) + ".json";
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFileName(path), expected, comparison)) { why = "registration is not named for its own session"; return null; }

        var pid = Json.Int(e, "ProcessId") ?? 0;
        afterRead?.Invoke(path);
        // agent-ha-bridge's Read-DaemonRegistrationFile: a hook rewriting it while it was read
        // may have given half of each version, which is neither.
        if (Stamp(path) != before) { why = "registration changed while it was read"; return null; }
        why = null;
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

    static readonly (long, long) Unreadable = (-1, -1);
    static readonly (long, long) Linked = (-2, -2);

    // When it was last written and how long it is; null once it has gone. Gone only when the OS
    // says so: FileInfo.Exists is false for a file whose details cannot be read as well, which
    // took an unreadable registration for a deleted one and let its process age out of the hold.
    static (long Ticks, long Length)? Stamp(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) { return Linked; }
            var info = new FileInfo(path);
            return (info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Unreadable; }
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
    internal static string SafeKey(string sessionId) => AgentBridge.Hooks.SpoolContract.SafeKey(sessionId);
}
