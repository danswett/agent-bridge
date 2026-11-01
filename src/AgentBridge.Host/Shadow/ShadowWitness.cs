using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Shadow;

// The evidence for a difference agent-ha-bridge's daemon is known to cause. The first live runs
// found five daemon bugs (agent-ha-bridge#258 to #262, docs/host-shadow.md); each leaves the
// daemon's background agents, shells or model different from the host's in a way that has a
// signature in the session's transcript. A sixth, under #259 (filed as #263, its duplicate), leaves an answered question on the
// daemon's card, and with it its waiting. A difference is put down to one of them only when the
// transcript shows that signature for every id it involves and the counts then add up exactly,
// never because of the field it is in: a difference of the same kind with any other cause stays
// a mismatch.
//
// Copilot sessions only, which is where those bugs are. Each session's transcript is read once
// from the start, then only what is appended, and only while one of its differences needs it;
// what is kept is ids, offsets and model names, in memory only.
public sealed class ShadowWitness(AgentHaBridgePaths paths)
{
    readonly Dictionary<string, TranscriptEvidence> evidence = new(StringComparer.Ordinal);

    public static string Issue(int n) => $"agent-ha-bridge#{n}";

    public IReadOnlyList<ShadowDifference> Explain(ShadowView daemon, ShadowView host, IReadOnlyList<ShadowDifference> differences)
    {
        foreach (var gone in evidence.Keys.Where(k => !daemon.Sessions.ContainsKey(k)).ToList()) { evidence.Remove(gone); }
        if (!differences.Any(Explainable)) { return differences; }
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in daemon.Sessions.Keys) { ids.TryAdd(ShadowHash.Session(id), id); }
        var result = new List<ShadowDifference>(differences.Count);
        foreach (var d in differences)
        {
            string? known = null;
            if (Explainable(d) && ids.TryGetValue(d.Session!, out var id)
                && daemon.Sessions.TryGetValue(id, out var ds) && host.Sessions.TryGetValue(id, out var hs)
                && ds.Agent == "copilot" && Evidence(id) is { } e)
            {
                known = d.Kind switch
                {
                    ShadowKinds.BackgroundAgents => e.Agents(ds, hs),
                    ShadowKinds.BackgroundShells => e.Shells(ds, hs),
                    ShadowKinds.Model => e.Model(ds, hs),
                    ShadowKinds.QuestionMissingOnHost => e.AnsweredQuestion(ds, hs),
                    // Only the waiting the daemon's question gives it: its own status must be the host's.
                    ShadowKinds.Status when ShadowCompare.Comparable(ds.Status) == hs.Status => e.AnsweredQuestion(ds, hs),
                    _ => null,
                };
            }
            // The daemon's question is one it should have cleared (#259), but its own status is not
            // the host's either: that is the disagreement, so it is what the mismatch says.
            if (known is null && d.Kind == ShadowKinds.Status && ids.TryGetValue(d.Session!, out var sid) && daemon.Sessions.TryGetValue(sid, out var dss)
                && host.Sessions.TryGetValue(sid, out var hss) && dss.Agent == "copilot" && Evidence(sid)?.AnsweredQuestion(dss, hss) is not null)
            {
                result.Add(d with { Detail = $"daemon={ShadowHash.Token(dss.Status)} host={ShadowHash.Token(hss.Status)}" });
                continue;
            }
            result.Add(known is null ? d : d with { Known = known });
        }
        return result;
    }

    static bool Explainable(ShadowDifference d) =>
        d.Known is null && d.Session is not null && d.Kind is ShadowKinds.BackgroundAgents or ShadowKinds.BackgroundShells or ShadowKinds.Model
            or ShadowKinds.QuestionMissingOnHost or ShadowKinds.Status;

    TranscriptEvidence? Evidence(string id)
    {
        if (paths.CopilotTranscript(id) is not { } path) { return null; }
        if (!evidence.TryGetValue(id, out var e)) { evidence[id] = e = new TranscriptEvidence(path); }
        return e.Catch() ? e : null;
    }
}

// What one Copilot transcript says about the daemon's known bugs, read as it grows.
public sealed partial class TranscriptEvidence(string path)
{
    // Ids kept per transcript: a busy session's agents run thousands of shells, and one the daemon
    // has listed for hours must still be known (4096 forgot them). Each is a few hundred bytes.
    const int Keep = 65536;
    const int MaxLine = 64 * 1024 * 1024;

    // A shell start as agent-ha-bridge's daemon reads one (Get-ActivityFromEvents): from any tool
    // result, a subagent's included. Proper is whether agent-bridge's host reads the same result
    // as a start (ShellMarkers); a quoted "still running" line is one the daemon takes and the
    // host does not.
    enum StartKind { Proper, QuotedStillRunning, QuotedOther }

    // A run of starts of one kind from one side, with no end between them: a shell polled for an
    // hour says "still running" at every poll, and only the kinds and where they lie matter.
    sealed class Run(long first, bool subagent, StartKind kind)
    {
        public long First { get; } = first;
        public long Last { get; set; } = first;
        public bool Subagent { get; } = subagent;
        public StartKind Kind { get; } = kind;
    }

    sealed class Shell
    {
        const int Recent = 32;
        public List<Run> Runs { get; } = [];
        public List<long> Ends { get; } = [];
        // Where the latest run dropped for room ended; a shell whose dropped starts could matter
        // is not classified at all.
        public long DroppedThrough = -1;
        public long Last;

        // since: where the session last shut down; a start after it begins a run of its own, as the
        // host ends every shell there (found in review, #162).
        public void Started(long at, bool subagent, StartKind kind, long since)
        {
            if (Runs.Count > 0 && Runs[^1] is { } run && run.Subagent == subagent && run.Kind == kind && (Ends.Count == 0 || Ends[^1] < run.Last) && since < run.Last)
            {
                run.Last = at;
                return;
            }
            Runs.Add(new Run(at, subagent, kind));
            if (Runs.Count > Recent) { DroppedThrough = Math.Max(DroppedThrough, Runs[0].Last); Runs.RemoveAt(0); }
        }

        // Only the latest ends matter: an end counts against the starts before it.
        public void Ended(long at)
        {
            Ends.Add(at);
            if (Ends.Count > Recent) { EndsDroppedThrough = Math.Max(EndsDroppedThrough, Ends[0]); Ends.RemoveAt(0); }
        }

        // Where the latest end dropped for room was: no end after a start can be ruled out before it.
        public long EndsDroppedThrough = -1;

        // The ends agent-bridge's host takes (ShellMarkers.Read, the session's own results only,
        // and shell_completed): the daemon's Ends also hold quoted lines and subagents' results.
        // Only the latest matters.
        public long HostEnd = -1;
    }
    // A background agent's runs, the way agent-bridge's host follows them (BackgroundWork).
    sealed class Agent
    {
        public bool Working;
        public bool AskedForTools;
        public bool Completed;
        public int IdleOwed;
        public long Last;
        // Where its first subagent.completed is, and where it was last woken after that.
        public long CompletedAt = -1;
        public long WokenAt = -1;
    }

    readonly Dictionary<string, Shell> shells = new(StringComparer.Ordinal);
    // Where the session itself last shut down: the host ends every shell there (BackgroundWork.EndAll).
    long shutdownAt = -1;
    readonly Dictionary<string, Agent> agents = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> agentByCall = new(StringComparer.Ordinal);
    // Each tool call's first subagent.completed, where the daemon stops counting its agent.
    readonly Dictionary<string, long> completed = new(StringComparer.Ordinal);
    // The latest models named by assistant messages, and whose they were.
    readonly List<(long Offset, string Model, bool Subagent)> models = [];
    string? ownModel;
    long offset;

    // The session's own ask_user calls, latest last: where each started, when, and where the line
    // that completed it ends (-1 while it waits).
    sealed class Ask(string callId, long start, DateTimeOffset? at)
    {
        public string CallId { get; } = callId;
        public byte[] Quoted { get; } = Encoding.UTF8.GetBytes("\"" + callId + "\"");
        public long Start { get; } = start;
        public DateTimeOffset? At { get; } = at;
        public long CompletedThrough = -1;
    }
    readonly List<Ask> asks = [];
    int waiting;

    // How much of the transcript agent-ha-bridge reads to see whether a question was answered
    // (Get-CopilotTranscriptTailLines' MaxBytes, from Get-CopilotAskUserState).
    public const long DaemonQuestionTail = 4L * 1024 * 1024;
    // The hook arms the marker as the ask_user starts; its clock and the transcript's agree to well
    // within this.
    static readonly TimeSpan ArmedWithin = TimeSpan.FromSeconds(10);

    // Reads what was appended since the last time; false when the transcript cannot be read now.
    public bool Catch()
    {
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
            });
            if (stream.Length < offset) { Reset(); }
            stream.Position = offset;
            var buffer = new byte[1024 * 1024];
            var line = new MemoryStream();
            // A line longer than any event is not read, only stepped over.
            var skipping = false;
            var position = offset;
            var lineStart = offset;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var from = 0;
                while (true)
                {
                    var newline = Array.IndexOf(buffer, (byte)'\n', from, read - from);
                    var end = newline < 0 ? read : newline;
                    if (!skipping)
                    {
                        if (line.Length + (end - from) > MaxLine) { skipping = true; line.SetLength(0); }
                        else { line.Write(buffer, from, end - from); }
                    }
                    if (newline < 0) { break; }
                    if (!skipping) { Line(line.GetBuffer().AsSpan(0, (int)line.Length), lineStart); }
                    skipping = false;
                    line.SetLength(0);
                    lineStart = position + newline + 1;
                    from = newline + 1;
                }
                position += read;
            }
            // A line not yet ended is read again next time, whole.
            offset = lineStart;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    void Reset()
    {
        shells.Clear();
        agents.Clear();
        agentByCall.Clear();
        completed.Clear();
        models.Clear();
        ownModel = null;
        asks.Clear();
        waiting = 0;
        offset = 0;
        shutdownAt = -1;
    }

    static readonly byte[][] Wanted =
    [
        "\"agentId\""u8.ToArray(), "\"subagent."u8.ToArray(), "shellId"u8.ToArray(), "command with id: "u8.ToArray(),
        "\"system.notification\""u8.ToArray(), "\"session.shutdown\""u8.ToArray(), "\"assistant.message\""u8.ToArray(),
        "\"session.model_change\""u8.ToArray(), "\"session.start\""u8.ToArray(), "\"session.resume\""u8.ToArray(),
        "\"ask_user\""u8.ToArray(),
    ];

    void Line(ReadOnlySpan<byte> bytes, long at)
    {
        // An ask_user's completion names only its call, so while one waits each completion is looked
        // at for it: by bytes first, then parsed to be sure it is that call's and not one quoting it.
        if (waiting > 0 && bytes.IndexOf("\"tool.execution_complete\""u8) >= 0)
        {
            Ask? matched = null;
            foreach (var ask in asks) { if (ask.CompletedThrough < 0 && bytes.IndexOf(ask.Quoted) >= 0) { matched = ask; break; } }
            if (matched is not null && CompletedCall(bytes) is { } call && asks.FirstOrDefault(a => a.CompletedThrough < 0 && a.CallId == call) is { } done)
            {
                done.CompletedThrough = at + bytes.Length + 1;
                waiting--;
            }
        }
        var wanted = false;
        foreach (var w in Wanted) { if (bytes.IndexOf(w) >= 0) { wanted = true; break; } }
        if (!wanted) { return; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes.ToArray()); }
        catch (JsonException) { return; }
        using (doc)
        {
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) { return; }
            Event(e, at);
        }
    }

    void Event(JsonElement e, long at)
    {
        var type = Str(e, "type");
        var agentId = Str(e, "agentId");
        var data = e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        switch (type)
        {
            case "subagent.started" when Str(data, "executionMode") == "background":
                {
                    var call = Str(data, "toolCallId");
                    var key = agentId ?? call;
                    if (key is null) { break; }
                    if (call is not null) { agentByCall[call] = key; }
                    var agent = AgentFor(key, at);
                    agent.Working = true;
                    agent.AskedForTools = false;
                    break;
                }
            case "subagent.completed":
                {
                    if (Str(data, "toolCallId") is not { } call) { break; }
                    if (completed.Count < Keep) { completed.TryAdd(call, at); }
                    if (agentByCall.TryGetValue(call, out var key) && agents.TryGetValue(key, out var agent) && !agent.Completed)
                    {
                        agent.Completed = true;
                        agent.CompletedAt = at;
                        EndRun(agent);
                    }
                    break;
                }
            case "system.notification" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.Object:
                switch (Str(kind, "type"))
                {
                    case "agent_idle" when Str(kind, "agentId") is { } idle && agents.TryGetValue(idle, out var agent):
                        if (agent.IdleOwed > 0) { agent.IdleOwed--; } else { agent.Working = false; }
                        break;
                    case "shell_completed" when Str(kind, "shellId") is { } id:
                        ShellFor(id, at).Ended(at);
                        ShellFor(id, at).HostEnd = at;
                        break;
                }
                break;
            case "session.shutdown" when agentId is null:
                foreach (var a in agents.Values) { a.Working = false; a.AskedForTools = false; a.IdleOwed = 0; }
                shutdownAt = at;
                break;
            case "abort" or "session.shutdown" when agentId is not null && agents.TryGetValue(agentId, out var ended):
                EndRun(ended);
                ended.AskedForTools = false;
                break;
            case "tool.execution_start" when agentId is null && Str(data, "toolName") == "ask_user" && Str(data, "toolCallId") is { } askId:
                if (asks.Count >= 64) { if (asks[0].CompletedThrough < 0) { waiting--; } asks.RemoveAt(0); }
                asks.Add(new Ask(askId, at, Str(e, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var started) ? started : null));
                waiting++;
                break;
            case "user.message" or "assistant.turn_start" or "tool.execution_start" when agentId is not null && agents.TryGetValue(agentId, out var active):
                Wake(active, at);
                break;
            case "assistant.turn_end" when agentId is not null && agents.TryGetValue(agentId, out var turned):
                if (!turned.AskedForTools) { EndRun(turned); }
                turned.AskedForTools = false;
                break;
            case "assistant.message":
                {
                    if (agentId is not null && agents.TryGetValue(agentId, out var talking))
                    {
                        Wake(talking, at);
                        // The turn's latest reply decides, as in BackgroundWork (#84).
                        talking.AskedForTools = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("toolRequests", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0;
                    }
                    if (Str(data, "model") is { } model)
                    {
                        models.Add((at, model.Trim(), agentId is not null));
                        if (models.Count > 64) { models.RemoveAt(0); }
                        if (agentId is null) { ownModel = model.Trim(); }
                    }
                    break;
                }
            case "session.start" when agentId is null:
                ownModel ??= Str(data, "selectedModel");
                break;
            case "session.resume" when agentId is null:
                ownModel = Str(data, "selectedModel") ?? ownModel;
                break;
            case "session.model_change" when agentId is null:
                ownModel = Str(data, "newModel") ?? ownModel;
                break;
            case "tool.execution_complete":
                ShellMarkers(data, agentId is not null, at);
                break;
        }
    }

    // Get-ActivityFromEvents' reading of one tool result, in its order: the ends, a stop, then the
    // first start it finds.
    void ShellMarkers(JsonElement data, bool subagent, long at)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) { return; }
        if (Str(result, "content") is not { } content) { return; }
        var whole = content.Trim();
        // What the host would take from this result were it the session's own (a subagent's start
        // is still proper or quoted by the same shape), and the end it takes from the session's own.
        var (hostStart, hostEnd) = HostRead(whole);
        if (!subagent && hostEnd is not null) { ShellFor(hostEnd, at).HostEnd = at; }
        foreach (Match m in Completed().Matches(content)) { ShellFor(m.Groups[1].Value, at).Ended(at); }
        if (Stopped().Match(whole) is { Success: true } stop) { ShellFor(stop.Groups[1].Value, at).Ended(at); return; }
        // Proper when the host takes a start of the same id from the result: a quoted start line
        // before a genuine last-line poll of that id is one (found in review, #162).
        if (Started().Match(content) is { Success: true } started)
        {
            var id = started.Groups[1].Value.Trim();
            ShellFor(id, at).Started(at, subagent, hostStart == id ? StartKind.Proper : StartKind.QuotedOther, shutdownAt);
            return;
        }
        if (StillRunning().Match(content) is { Success: true } running)
        {
            var id = running.Groups[1].Value;
            ShellFor(id, at).Started(at, subagent, hostStart == id ? StartKind.Proper : StartKind.QuotedStillRunning, shutdownAt);
            return;
        }
        if (Moved().Match(whole) is { Success: true } moved)
        {
            var id = moved.Groups[1].Value;
            ShellFor(id, at).Started(at, subagent, hostStart == id ? StartKind.Proper : StartKind.QuotedOther, shutdownAt);
        }
    }

    // agent-bridge's host's reading of one tool result (ShellMarkers.Read), its length gates
    // included: a whole start, move or stop, else a last line still running or completed.
    const int HostMaxMarker = 1024;
    static (string? Started, string? Ended) HostRead(string whole)
    {
        if (whole.Length <= HostMaxMarker && whole.StartsWith("<command ", StringComparison.Ordinal))
        {
            if (StartedWhole().Match(whole) is { Success: true } a) { return (a.Groups[1].Value.Trim(), null); }
            if (Moved().Match(whole) is { Success: true } m) { return (m.Groups[1].Value, null); }
            if (Stopped().Match(whole) is { Success: true } s) { return (null, s.Groups[1].Value); }
        }
        var newline = whole.LastIndexOf('\n');
        var last = (newline < 0 ? whole : whole[(newline + 1)..]).TrimEnd('\r');
        if (last.Length > HostMaxMarker || !last.StartsWith('<')) { return (null, null); }
        if (StillRunningLast().Match(last) is { Success: true } r) { return (r.Groups[1].Value, null); }
        if (CompletedLast().Match(last) is { Success: true } c) { return (null, c.Groups[1].Value); }
        return (null, null);
    }

    // The daemon lists more (or fewer) background shells than the host. Known when every listed
    // shell the host does not count is one of:
    //   #259  an end the daemon has read past and not applied;
    //   #261  only a subagent ever started it (since its last end);
    //   #262  besides a subagent's, only a quoted "still running" line started it;
    // and, of the shells the host counts that the daemon does not list:
    //   #276  the session started it again under the same id after it ended, the daemon has read
    //         both, and folding them as one batch, it took the old end for the new start's;
    // and what is left is exactly the host's count.
    public string? Shells(ShadowSession daemon, ShadowSession host)
    {
        if (daemon.Offset is not { } through) { return null; }
        // Only when every id behind the daemon's count is known (DaemonReader keeps at most 1024).
        if (daemon.ShellIds.Distinct(StringComparer.Ordinal).Count() != daemon.Shells) { return null; }
        var issues = new SortedSet<int>();
        var explained = 0;
        var explainedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in daemon.ShellIds.Distinct(StringComparer.Ordinal))
        {
            if (!shells.TryGetValue(id, out var shell)) { continue; }
            var starts = shell.Runs.Where(s => s.First < through).ToList();
            var ends = shell.Ends.Where(o => o < through).ToList();
            if (starts.Count == 0) { continue; }
            // A run the daemon has read only part of may hold a start just short of its offset.
            var latestStart = starts.Max(s => s.Last < through ? s.Last : through - 1);
            if (ends.Count > 0 && ends.Max() > latestStart) { issues.Add(259); explained++; explainedIds.Add(id); continue; }
            var lastEnd = ends.Count > 0 ? ends.Max() : -1;
            if (shell.DroppedThrough > lastEnd) { continue; }
            var holding = starts.Where(s => s.Last > lastEnd).ToList();
            if (holding.Count == 0 || holding.Any(s => !s.Subagent && s.Kind != StartKind.QuotedStillRunning)) { continue; }
            if (holding.Any(s => s.Subagent)) { issues.Add(261); }
            if (holding.Any(s => !s.Subagent)) { issues.Add(262); }
            explained++;
            explainedIds.Add(id);
        }
        var listed = daemon.ShellIds.ToHashSet(StringComparer.Ordinal);
        var restarted = shells.Count(s => !listed.Contains(s.Key) && Restarted(s.Value, through, shutdownAt));
        if (restarted > 0)
        {
            // The host's ids are not known here, only its count, so a restart is put down to #276
            // only when every shell the daemon lists and no signature explains is one the host runs
            // too: otherwise a daemon extra and a host extra of other causes could cancel out
            // (found in review, #162).
            if (listed.Any(id => !explainedIds.Contains(id) && !(shells.TryGetValue(id, out var s) && HostRuns(s, shutdownAt)))) { return null; }
            issues.Add(276);
        }
        return explained + restarted > 0 && listed.Count - explained + restarted == host.Shells ? Name(issues) : null;
    }

    // Running again since an end of the same id, as the host counts it, and both read by the daemon:
    // an end the host takes, then a start of the session's own that both take, before the daemon's
    // offset; since that start, no end the host takes (so it still runs) and none the daemon has
    // read (an end after the start drops it for another reason), and no shutdown, which ends every
    // shell on the host. Other starts for the id since (a quoted line, a subagent's) the host
    // ignores, and any the daemon took in a later batch would have listed it again. Seen on DEV-VM1
    // (#40): a pr-watch watcher completed and was restarted 40 s later, with the daemon listing
    // nothing from then on while its process ran (agent-ha-bridge#276).
    // A shell the host counts as running: a start of the session's own that it takes, after the last
    // end it takes and the last shutdown.
    static bool HostRuns(Shell shell, long shutdownAt) =>
        shell.Runs.Any(r => !r.Subagent && r.Kind == StartKind.Proper && r.Last > shell.HostEnd && r.Last > shutdownAt);

    static bool Restarted(Shell shell, long through, long shutdownAt)
    {
        if (shell.HostEnd < 0 || shell.HostEnd >= through || shell.DroppedThrough > shell.HostEnd) { return false; }
        // An end the host takes is one the daemon takes too, at the same place; one no longer held
        // proves nothing.
        if (!shell.Ends.Contains(shell.HostEnd)) { return false; }
        // An end dropped for room after the start may have been one the daemon read (found in
        // review, #162): then nothing is known.
        return shell.Runs.Any(r => r.First > shell.HostEnd && r.First > shutdownAt && r.First < through && !r.Subagent && r.Kind == StartKind.Proper
            && shell.EndsDroppedThrough < r.First && !shell.Ends.Any(e => e > r.First && e < through));
    }

    // The daemon counts fewer (or more) background agents than the host. Known when the host's
    // extra agents are exactly those working again after their first subagent.completed, which
    // the daemon stops counting there (#258), and the daemon's extra ones exactly those whose
    // subagent.completed it has read past (#259).
    public string? Agents(ShadowSession daemon, ShadowSession host)
    {
        if (daemon.Offset is not { } through) { return null; }
        var listed = daemon.AgentIds.Distinct(StringComparer.Ordinal).ToList();
        if (listed.Count != daemon.Agents) { return null; }
        var missed = listed.Count(c => completed.TryGetValue(c, out var at) && at < through);
        // Woken after a completion and a wake the daemon has both read: one it has not read yet is
        // the daemon being behind, not #258.
        var woken = agents.Where(a => a.Value.Working && a.Value.Completed && a.Value.CompletedAt >= 0 && a.Value.CompletedAt < through && a.Value.WokenAt >= 0 && a.Value.WokenAt < through)
            .Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        // The daemon counts by tool call; none of those it lists may be one of the woken agents.
        if (listed.Any(c => agentByCall.TryGetValue(c, out var key) && woken.Contains(key))) { return null; }
        var issues = new SortedSet<int>();
        if (missed > 0) { issues.Add(259); }
        if (woken.Count > 0) { issues.Add(258); }
        return issues.Count > 0 && listed.Count - missed == host.Agents - woken.Count ? Name(issues) : null;
    }

    // The daemon's model is the one a subagent's message named, the last message it has read
    // being a subagent's, and the host's is the session's own (#260).
    public string? Model(ShadowSession daemon, ShadowSession host)
    {
        if (daemon.Offset is not { } through || daemon.Model is null) { return null; }
        var read = models.Where(m => m.Offset < through).ToList();
        return read.Count > 0 && read[^1] is { Subagent: true } last && last.Model == daemon.Model
            && host.Model is not null && host.Model == ownModel ? Name([260]) : null;
    }

    // The daemon still shows a question the host does not (and so its waiting), and that question's
    // ask_user has completed in the transcript, but the daemon could not see it complete: its start
    // lay further back than the tail the daemon reads, by the time the completion was written. The
    // daemon then never clears the question (#259; reported as #263, closed as its duplicate). The question is the marker's: by its call id
    // when it has one, else the ask_user that started as the hook armed it.
    public string? AnsweredQuestion(ShadowSession daemon, ShadowSession host)
    {
        if (daemon.Question is not { } q || host.Question is not null) { return null; }
        var ask = q.CallId is { } id
            ? asks.LastOrDefault(a => a.CallId == id)
            : q.AskedAt is { } armed ? asks.Where(a => a.At is { } s && (s - armed).Duration() <= ArmedWithin).MinBy(a => (a.At!.Value - armed).Duration()) : null;
        return ask is { CompletedThrough: >= 0 } && ask.CompletedThrough - ask.Start > DaemonQuestionTail ? Name([259]) : null;
    }

    // The call a tool.execution_complete event completes, or null when the line is not one.
    static string? CompletedCall(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes.ToArray());
            var e = doc.RootElement;
            return Str(e, "type") == "tool.execution_complete" && e.TryGetProperty("data", out var d) ? Str(d, "toolCallId") : null;
        }
        catch (JsonException) { return null; }
    }

    static string Name(SortedSet<int> issues) => string.Join(", ", issues.Select(ShadowWitness.Issue));

    Shell ShellFor(string id, long at)
    {
        if (!shells.TryGetValue(id, out var shell))
        {
            if (shells.Count >= Keep) { shells.Remove(shells.MinBy(s => s.Value.Last).Key); }
            shells[id] = shell = new Shell();
        }
        shell.Last = at;
        return shell;
    }

    Agent AgentFor(string key, long at)
    {
        if (!agents.TryGetValue(key, out var agent))
        {
            if (agents.Count >= Keep)
            {
                var drop = agents.MinBy(a => a.Value.Last).Key;
                agents.Remove(drop);
                foreach (var call in agentByCall.Where(p => p.Value == drop).Select(p => p.Key).ToList()) { agentByCall.Remove(call); }
            }
            agents[key] = agent = new Agent();
        }
        agent.Last = at;
        return agent;
    }

    // Where it was woken is where it went from not working to working, not its latest event.
    static void Wake(Agent agent, long at)
    {
        if (!agent.Working && agent.Completed) { agent.WokenAt = at; }
        agent.Working = true;
        agent.Last = at;
    }

    static void EndRun(Agent agent)
    {
        if (!agent.Working) { return; }
        agent.Working = false;
        agent.IdleOwed++;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    // agent-ha-bridge 1.33.32's patterns (Get-ActivityFromEvents), and the host's for the last line.
    [GeneratedRegex(@"(?m)^<shellId: (\S+?) completed with exit code", RegexOptions.CultureInvariant)]
    private static partial Regex Completed();

    [GeneratedRegex(@"\A<shellId: (\S+?) completed with exit code", RegexOptions.CultureInvariant)]
    private static partial Regex CompletedLast();

    [GeneratedRegex(@"\A<command with id: (\S+?) stopped>\z", RegexOptions.CultureInvariant)]
    private static partial Regex Stopped();

    [GeneratedRegex(@"(?m)^<command started in background with shellId: ([^>\r\n]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex Started();

    [GeneratedRegex(@"\A<command started in background with shellId: ([^>\r\n]+)>\z", RegexOptions.CultureInvariant)]
    private static partial Regex StartedWhole();

    [GeneratedRegex(@"(?m)^<command with shellId: (\S+) is still running", RegexOptions.CultureInvariant)]
    private static partial Regex StillRunning();

    [GeneratedRegex(@"\A<command with shellId: (\S+?) is (?:still running|running in the background)", RegexOptions.CultureInvariant)]
    private static partial Regex StillRunningLast();

    [GeneratedRegex(@"\A<command with shellId: (\S+?) moved to background by the user[^\r\n]*>\z", RegexOptions.CultureInvariant)]
    private static partial Regex Moved();
}
