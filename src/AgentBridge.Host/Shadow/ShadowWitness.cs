using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Shadow;

// The evidence for a difference agent-ha-bridge's daemon is known to cause. The first live runs
// found five daemon bugs (agent-ha-bridge#258 to #262, docs/host-shadow.md); each leaves the
// daemon's background agents, shells or model different from the host's in a way that has a
// signature in the session's transcript. A difference is put down to one of them only when the
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
                    _ => null,
                };
            }
            result.Add(known is null ? d : d with { Known = known });
        }
        return result;
    }

    static bool Explainable(ShadowDifference d) =>
        d.Known is null && d.Session is not null && d.Kind is ShadowKinds.BackgroundAgents or ShadowKinds.BackgroundShells or ShadowKinds.Model;

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

    sealed record Start(long Offset, bool Subagent, StartKind Kind);

    sealed class Shell
    {
        // The latest few of each: a shell polled for an hour says "still running" at every poll.
        const int Recent = 32;
        public List<Start> Starts { get; } = [];
        public List<long> Ends { get; } = [];
        public long Last;

        public void Started(Start s) { Starts.Add(s); if (Starts.Count > Recent) { Starts.RemoveAt(0); } }

        public void Ended(long at) { Ends.Add(at); if (Ends.Count > Recent) { Ends.RemoveAt(0); } }
    }

    // A background agent's runs, the way agent-bridge's host follows them (BackgroundWork).
    sealed class Agent
    {
        public bool Working;
        public bool AskedForTools;
        public bool Completed;
        public int IdleOwed;
        public long Last;
    }

    readonly Dictionary<string, Shell> shells = new(StringComparer.Ordinal);
    readonly Dictionary<string, Agent> agents = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> agentByCall = new(StringComparer.Ordinal);
    // Each tool call's first subagent.completed, where the daemon stops counting its agent.
    readonly Dictionary<string, long> completed = new(StringComparer.Ordinal);
    // The latest models named by assistant messages, and whose they were.
    readonly List<(long Offset, string Model, bool Subagent)> models = [];
    string? ownModel;
    long offset;

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
        offset = 0;
    }

    static readonly byte[][] Wanted =
    [
        "\"agentId\""u8.ToArray(), "\"subagent."u8.ToArray(), "shellId"u8.ToArray(), "command with id: "u8.ToArray(),
        "\"system.notification\""u8.ToArray(), "\"session.shutdown\""u8.ToArray(), "\"assistant.message\""u8.ToArray(),
        "\"session.model_change\""u8.ToArray(), "\"session.start\""u8.ToArray(), "\"session.resume\""u8.ToArray(),
    ];

    void Line(ReadOnlySpan<byte> bytes, long at)
    {
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
                        break;
                }
                break;
            case "session.shutdown" when agentId is null:
                foreach (var a in agents.Values) { a.Working = false; a.AskedForTools = false; a.IdleOwed = 0; }
                break;
            case "abort" or "session.shutdown" when agentId is not null && agents.TryGetValue(agentId, out var ended):
                EndRun(ended);
                ended.AskedForTools = false;
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
                        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("toolRequests", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) { talking.AskedForTools = true; }
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
        foreach (Match m in Completed().Matches(content)) { ShellFor(m.Groups[1].Value, at).Ended(at); }
        if (Stopped().Match(whole) is { Success: true } stop) { ShellFor(stop.Groups[1].Value, at).Ended(at); return; }
        var last = whole[(whole.LastIndexOf('\n') + 1)..].TrimEnd('\r');
        if (Started().Match(content) is { Success: true } started)
        {
            var id = started.Groups[1].Value.Trim();
            var proper = StartedWhole().IsMatch(whole);
            ShellFor(id, at).Started(new(at, subagent, proper ? StartKind.Proper : StartKind.QuotedOther));
            return;
        }
        if (StillRunning().Match(content) is { Success: true } running)
        {
            var id = running.Groups[1].Value;
            var proper = StillRunningLast().Match(last) is { Success: true } l && l.Groups[1].Value == id;
            ShellFor(id, at).Started(new(at, subagent, proper ? StartKind.Proper : StartKind.QuotedStillRunning));
            return;
        }
        if (Moved().Match(whole) is { Success: true } moved) { ShellFor(moved.Groups[1].Value, at).Started(new(at, subagent, StartKind.Proper)); }
    }

    // The daemon lists more (or fewer) background shells than the host. Known when every listed
    // shell the host does not count is one of:
    //   #259  an end the daemon has read past and not applied;
    //   #261  only a subagent ever started it (since its last end);
    //   #262  besides a subagent's, only a quoted "still running" line started it;
    // and what is left is exactly the host's count.
    public string? Shells(ShadowSession daemon, ShadowSession host)
    {
        if (daemon.Offset is not { } through) { return null; }
        var issues = new SortedSet<int>();
        var explained = 0;
        foreach (var id in daemon.ShellIds.Distinct(StringComparer.Ordinal))
        {
            if (!shells.TryGetValue(id, out var shell)) { continue; }
            var starts = shell.Starts.Where(s => s.Offset < through).ToList();
            var ends = shell.Ends.Where(o => o < through).ToList();
            if (starts.Count == 0) { continue; }
            if (ends.Count > 0 && ends.Max() > starts.Max(s => s.Offset)) { issues.Add(259); explained++; continue; }
            var lastEnd = ends.Count > 0 ? ends.Max() : -1;
            var holding = starts.Where(s => s.Offset > lastEnd).ToList();
            if (holding.Count == 0 || holding.Any(s => !s.Subagent && s.Kind != StartKind.QuotedStillRunning)) { continue; }
            if (holding.Any(s => s.Subagent)) { issues.Add(261); }
            if (holding.Any(s => !s.Subagent)) { issues.Add(262); }
            explained++;
        }
        return explained > 0 && daemon.ShellIds.Distinct(StringComparer.Ordinal).Count() - explained == host.Shells ? Name(issues) : null;
    }

    // The daemon counts fewer (or more) background agents than the host. Known when the host's
    // extra agents are exactly those working again after their first subagent.completed, which
    // the daemon stops counting there (#258), and the daemon's extra ones exactly those whose
    // subagent.completed it has read past (#259).
    public string? Agents(ShadowSession daemon, ShadowSession host)
    {
        if (daemon.Offset is not { } through) { return null; }
        var listed = daemon.AgentIds.Distinct(StringComparer.Ordinal).ToList();
        var missed = listed.Count(c => completed.TryGetValue(c, out var at) && at < through);
        var woken = agents.Where(a => a.Value.Working && a.Value.Completed).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
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

    static void Wake(Agent agent, long at)
    {
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
