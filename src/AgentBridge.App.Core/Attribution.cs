using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The main agent's own words (v1-sessions.md, Activity, the `own` row).
//   ResponseTruncated: the start of the response was cut to fit.
//   ResponseDropped: the main agent had a response, but the session budget left it out.
//   Attributed: the host says whose each field is, so the words can be labelled "Main agent".
public sealed record MainVoice(string? Response, bool ResponseTruncated, bool ResponseDropped, string? Reasoning, bool Attributed);

// A delegate's state, as its glyph (ui.md §2.1) and its word.
public sealed record DelegateState(StatusGlyph Glyph, string Words, bool Running);

// One listed delegate as a list shows it: nested Depth deep under its parent, and the name of the
// delegate that started it when that one is listed, so a screen reader hears what the indent shows.
public sealed record DelegateEntry(Subagent Agent, int Depth, DelegateState State, string? ParentName)
{
    public string Id => Agent.Id;
    public string Name => string.IsNullOrEmpty(Agent.Name) ? "Delegate" : Agent.Name;
}

// The session's delegates: Active (running ones first, then idle ones, each nested under its
// parent), Finished (ended ones, folded into "N finished", nested the same way), and More, the
// entries the host left out (subagentsMore).
public sealed record DelegateGroups(IReadOnlyList<DelegateEntry> Active, IReadOnlyList<DelegateEntry> Finished, int More);

// Who said what in a session: the main agent's own response and reasoning, and its delegates
// (docs/design/subagents.md §5; ui.md §1 principle 3, "One voice per line"). The PWA's rules,
// case for case (pwa/js/attribution.js, pwa/test/unit/attribution.test.mjs; AttributionTests):
// the card, the strip, the Delegates tab and the inspector all show the same voices.
//
// A host's top-level response and reasoning are last-writer-wins, kept for older clients: a
// delegate's thinking can be there in place of the main agent's answer. activity.source says
// which delegate wrote each, and activity.own carries the main agent's own text beside it.
public static class Attribution
{
    // For each of response and reasoning, own's when source names a delegate for it, otherwise
    // the top-level text, or own's when that is absent. Never a delegate's words.
    public static MainVoice MainVoiceOf(Session s)
    {
        var a = s.Activity;
        var source = a.Source;
        var own = a.Own;
        string? response;
        bool truncated;
        if (!string.IsNullOrEmpty(source?.Response) || string.IsNullOrEmpty(a.Response))
        {
            // A field source names but own lacks: the main agent has none, unless the budget dropped it.
            response = string.IsNullOrEmpty(own?.Response) ? null : own.Response;
            truncated = own?.ResponseTruncated == true;
        }
        else
        {
            response = a.Response;
            truncated = a.ResponseTruncated == true;
        }
        var reasoning = !string.IsNullOrEmpty(source?.Reasoning) ? own?.Reasoning : string.IsNullOrEmpty(a.Reasoning) ? own?.Reasoning : a.Reasoning;
        return new MainVoice(
            response,
            response is not null && truncated,
            response is null && truncated,
            string.IsNullOrEmpty(reasoning) ? null : reasoning,
            source is not null || own is not null || s.Subagents is not null || s.SubagentsMore > 0);
    }

    // Waiting is a question of its own; idle, a background delegate between runs, which a message
    // can wake; ended, done or failed from how its last run went, or plain ended when the host
    // never saw it end.
    public static DelegateState StateOf(Subagent a) => a.Status switch
    {
        SubagentStatus.Working => new(StatusGlyph.Working, "Working", true),
        SubagentStatus.Waiting => new(StatusGlyph.Needs, "Asks", true),
        SubagentStatus.Ended => a.Outcome switch
        {
            SubagentOutcome.Failed => new(StatusGlyph.Failed, "Failed", false),
            SubagentOutcome.Done => new(StatusGlyph.Done, "Done", false),
            _ => new(StatusGlyph.Ended, "Ended", false),
        },
        _ => new(StatusGlyph.Idle, "Idle", false),
    };

    // The session's delegates, or null when the host lists none (Claude and Codex, or a host
    // before sub-agents: status rule 8 says what it can). A list the session budget emptied but
    // counted in subagentsMore still says how many.
    public static DelegateGroups? DelegatesOf(Session s)
    {
        var list = s.Subagents ?? [];
        var more = s.SubagentsMore ?? 0;
        if (list.Count == 0 && more <= 0) { return null; }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in list) { names.TryAdd(a.Id, string.IsNullOrEmpty(a.Name) ? "Delegate" : a.Name); }
        var rows = list.Select(a => (Agent: a, State: StateOf(a), ParentName: a.Parent is { } p && p != a.Id ? names.GetValueOrDefault(p) : null)).ToList();
        var running = rows.Where(r => r.State.Running);
        var idle = rows.Where(r => !r.State.Running && r.Agent.Status != SubagentStatus.Ended);
        var ended = rows.Where(r => r.Agent.Status == SubagentStatus.Ended);
        return new DelegateGroups(Nest([.. running, .. idle]), Nest([.. ended]), Math.Max(0, more));
    }

    // Each entry under its parent when the parent is in the same group, in the order given; the
    // rest at the top. A parent link that loops (a broken host) still lists every entry once.
    static List<DelegateEntry> Nest(List<(Subagent Agent, DelegateState State, string? ParentName)> group)
    {
        var ids = group.Select(r => r.Agent.Id).ToHashSet(StringComparer.Ordinal);
        bool Nested((Subagent Agent, DelegateState, string?) r) => r.Agent.Parent is { } p && p != r.Agent.Id && ids.Contains(p);
        var children = new Dictionary<string, List<(Subagent Agent, DelegateState State, string? ParentName)>>(StringComparer.Ordinal);
        foreach (var r in group.Where(Nested))
        {
            if (!children.TryGetValue(r.Agent.Parent!, out var c)) { children[r.Agent.Parent!] = c = []; }
            c.Add(r);
        }
        var output = new List<DelegateEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit((Subagent Agent, DelegateState State, string? ParentName) r, int depth)
        {
            if (!seen.Add(r.Agent.Id)) { return; }
            output.Add(new DelegateEntry(r.Agent, depth, r.State, r.ParentName));
            foreach (var c in children.GetValueOrDefault(r.Agent.Id) ?? []) { Visit(c, depth + 1); }
        }
        foreach (var r in group.Where(r => !Nested(r))) { Visit(r, 0); }
        foreach (var r in group) { Visit(r, 0); }
        return output;
    }

    // How long a running delegate has been at it ("4m"), or when it finished ("finished 3m ago").
    public static string TimeOf(Subagent a, DateTimeOffset now)
    {
        if (a.Status is SubagentStatus.Working or SubagentStatus.Waiting) { return a.StartedAt is { } s ? Words.Duration(s, now) : ""; }
        return a.EndedAt is { } e ? $"finished {Words.Ago(e, now)}" : "finished";
    }

    // session.get { id, agent } (v1-sessions.md): one delegate in full, with its own response,
    // reasoning and tool history; null once the session or the delegate has gone. Anything but
    // that delegate is refused rather than shown as its words: a host before sub-agents ignores
    // agent and answers with the session, whose response may be the main agent's.
    public static async Task<Subagent?> FetchDelegateAsync(HostConnection host, string session, string agent, TimeSpan? timeout = null)
    {
        var result = await host.CallAsync(Ops.SessionGet, new { id = session, agent }, timeout ?? TimeSpan.FromSeconds(15));
        if (result.ValueKind != JsonValueKind.Object || result.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.True) { return null; }
        var entry = result.Deserialize<Subagent>(ProtocolJson.Options);
        return entry is not null && entry.Id == agent ? entry : throw new HostErrorException("the machine answered with something other than that delegate");
    }
}
