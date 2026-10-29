using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Shadow;

// Shadow mode (design §6.4, docs/host-shadow.md): this host and agent-ha-bridge's daemon watch the
// same sessions on one machine, and a diff of what each shows is the evidence a machine is ready
// to leave the daemon. Both views are reduced to the same shape here, then compared field by
// field. Nothing a person wrote survives the reduction into a report: what reaches one is a kind
// of disagreement, a hashed session id and counts or enum values.

// One side's view of one session, in memory only. Text is held as hashes, but for the start of a
// question.
public sealed record ShadowSession
{
    public required string Id { get; init; }
    public string Agent { get; init; } = "unknown";
    // As that side said it. The daemon's own vocabulary (agents, shell, offline...) is kept so a
    // report says what the daemon published; Comparable maps it onto the host's.
    public required string Status { get; init; }
    public int Agents { get; init; }
    public int Shells { get; init; }
    public string? Model { get; init; }
    public int Compactions { get; init; }
    public string? ResponseTail { get; init; }
    public ShadowQuestion? Question { get; init; }
    // The host only: a Copilot permission prompt is pending, which it offers to approve and so
    // reads as waiting, where agent-ha-bridge only mirrors it and goes on saying working.
    public bool PermissionPending { get; init; }
    // The daemon's question marker existed but could not be read this time (a hook was writing
    // it). Neither "asked" nor "not asked", so the question is not compared this sample.
    public bool QuestionUnknown { get; init; }
}

public sealed record ShadowQuestion
{
    // The agent's id for the call, when that side has it: the host always, the daemon for Claude.
    public string? CallId { get; init; }
    public DateTimeOffset? AskedAt { get; init; }
    // The first 64 characters, whitespace collapsed, held in memory only and never written:
    // agent-ha-bridge appends its own hints to a question, so its start is compared as a prefix.
    public string? MessageHead { get; init; }
    // One side cannot drive this prompt from another device (agent-ha-bridge's terminalOnly, the
    // host's answerAtMachine). Each then shapes it its own way, so only that it is asked is compared.
    public bool AtMachine { get; init; }
    public IReadOnlyList<ShadowField> Fields { get; init; } = [];
}

public sealed record ShadowField(string Type, int Options, string? OptionsHash);

public sealed record ShadowView
{
    // Null when the view could be read; otherwise why not, from a fixed vocabulary.
    public string? Problem { get; init; }
    public IReadOnlyDictionary<string, ShadowSession> Sessions { get; init; } = new Dictionary<string, ShadowSession>();
    public string? Version { get; init; }
    public bool? Dev { get; init; }
    // The host only: whether it sees agent-ha-bridge running (design G5), so it does not deliver.
    public bool? SeesAgentHaBridge { get; init; }
    public bool Available => Problem is null;
}

// One disagreement in one sample. Session is a hash; Detail comes from a fixed vocabulary with
// counts, enum values and model names in it, never anything a person or an agent wrote.
public sealed record ShadowDifference(string Kind, string? Session, string Detail);

public sealed record ShadowComparison(bool Compared, int DaemonSessions, int HostSessions, IReadOnlyList<ShadowDifference> Differences, IReadOnlyList<string> SessionsSeen);

public static class ShadowKinds
{
    public const string MachineDaemon = "machine.daemon";
    public const string MachineHost = "machine.host";
    public const string MachineG5 = "machine.g5";
    public const string MissingOnHost = "session.missingOnHost";
    public const string MissingOnDaemon = "session.missingOnDaemon";
    public const string Agent = "agent";
    public const string Status = "status";
    public const string BackgroundAgents = "background.agents";
    public const string BackgroundShells = "background.shells";
    public const string Model = "model";
    public const string Compactions = "compactions";
    public const string QuestionMissingOnHost = "question.missingOnHost";
    public const string QuestionMissingOnDaemon = "question.missingOnDaemon";
    public const string QuestionIdentity = "question.identity";
    public const string QuestionMessage = "question.message";
    public const string QuestionFields = "question.fields";
    public const string QuestionFieldType = "question.fieldType";
    public const string QuestionOptions = "question.options";
    public const string Response = "activity.response";

    // What the daemon's files cannot show, so a clean report says nothing about it. Listed in
    // every report so a week of "clean" is never read as covering them.
    public static readonly IReadOnlyList<string> NotCompared =
    [
        "usage (agent-ha-bridge keeps it only in Home Assistant)",
        "resumable sessions (agent-ha-bridge keeps them only in Home Assistant)",
        "titles (agent-ha-bridge names sessions for Home Assistant)",
        "reasoning and tool history (bounded differently, parity §1)",
        "driver and the purple edge (missing here, #46)",
        "notifications",
    ];

    public static bool IsMachine(string kind) => kind.StartsWith("machine.", StringComparison.Ordinal);
}

public static partial class ShadowHash
{
    // Stable across days and runs, so one session's disagreements line up over the week, and
    // short enough to read. Someone at the machine finds a session by hashing its id the same way.
    public static string Session(string id) => "s" + Hex("agent-bridge-shadow\n" + id)[..12];

    public static string Text(string text) => Hex(text)[..16];

    static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    public static string Normalize(string text) => Space().Replace(text, " ").Trim();

    // The last 200 characters, whitespace collapsed: both sides bound long text, and differently
    // (parity §1, "Long text is bounded"), but each keeps the end a person reads last.
    public static string? Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var n = Normalize(text);
        return Text(n.Length <= 200 ? n : n[^200..]);
    }

    // The first 64 characters: agent-ha-bridge appends its own hints to a question, and the host
    // cuts a long one at 8 KiB, but both start with what the agent asked. Compared as a prefix in
    // memory; a report says only whether it matched.
    public static string? Head(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var n = Normalize(text);
        return n.Length <= 64 ? n : n[..64];
    }

    public static string Options(IEnumerable<string> values) => Text(string.Join('\u001f', values));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/()\- ]{0,63}$")]
    private static partial Regex SafeToken();

    // Values that go into a report verbatim (statuses, agents, model names) are short tokens from a
    // fixed vocabulary; anything else is reported as "(other)" rather than echoed.
    public static string Token(string? value) =>
        string.IsNullOrEmpty(value) ? "(none)" : SafeToken().IsMatch(value) ? value : "(other)";
}

public static class ShadowCompare
{
    // The host's status for one of the daemon's. agents and shell are the daemon's idle with
    // background work outstanding; the host says idle and counts that work beside it (parity §1,
    // "An idle session waiting for background agents..."), and the counts are compared on their own.
    public static string Comparable(string daemonStatus) => daemonStatus switch
    {
        "agents" or "shell" => "idle",
        "ended" => "ending",
        _ => daemonStatus,
    };

    public static string HostStatus(SessionStatus status) => status switch
    {
        SessionStatus.Working => "working",
        SessionStatus.Waiting => "waiting",
        SessionStatus.Ending => "ending",
        _ => "idle",
    };

    public static ShadowComparison Compare(ShadowView daemon, ShadowView host)
    {
        var diffs = new List<ShadowDifference>();
        if (!daemon.Available) { diffs.Add(new(ShadowKinds.MachineDaemon, null, daemon.Problem!)); }
        if (!host.Available) { diffs.Add(new(ShadowKinds.MachineHost, null, host.Problem!)); }
        if (!daemon.Available || !host.Available) { return new(false, daemon.Sessions.Count, host.Sessions.Count, diffs, []); }

        // Two writers typing into one prompt is how answers get doubled, so while the daemon runs
        // the host must know it does (design G5).
        if (host.SeesAgentHaBridge == false) { diffs.Add(new(ShadowKinds.MachineG5, null, "the daemon is running and the host does not see it")); }

        var seen = new List<string>();
        foreach (var id in daemon.Sessions.Keys.Union(host.Sessions.Keys).Order(StringComparer.Ordinal))
        {
            var s = ShadowHash.Session(id);
            seen.Add(s);
            daemon.Sessions.TryGetValue(id, out var d);
            host.Sessions.TryGetValue(id, out var h);
            if (h is null) { diffs.Add(new(ShadowKinds.MissingOnHost, s, $"daemon {ShadowHash.Token(d!.Agent)} {ShadowHash.Token(d.Status)}")); continue; }
            if (d is null) { diffs.Add(new(ShadowKinds.MissingOnDaemon, s, $"host {ShadowHash.Token(h.Agent)} {ShadowHash.Token(h.Status)}")); continue; }
            Session(d, h, s, diffs);
        }
        return new(true, daemon.Sessions.Count, host.Sessions.Count, diffs, seen);
    }

    static void Session(ShadowSession d, ShadowSession h, string s, List<ShadowDifference> diffs)
    {
        void Pair(string kind, string? a, string? b) => diffs.Add(new(kind, s, $"daemon={ShadowHash.Token(a)} host={ShadowHash.Token(b)}"));

        if (d.Agent != h.Agent) { Pair(ShadowKinds.Agent, d.Agent, h.Agent); }
        var expected = Comparable(d.Status);
        // parity §3: a pending permission prompt is beyond parity, so the host's waiting is the
        // daemon's working.
        if (expected != h.Status && !(h.PermissionPending && h.Status == "waiting" && expected == "working")) { Pair(ShadowKinds.Status, d.Status, h.Status); }
        if (d.Agents != h.Agents) { Pair(ShadowKinds.BackgroundAgents, d.Agents.ToString(), h.Agents.ToString()); }
        if (d.Shells != h.Shells) { Pair(ShadowKinds.BackgroundShells, d.Shells.ToString(), h.Shells.ToString()); }
        if (!string.Equals(d.Model ?? "", h.Model ?? "", StringComparison.Ordinal)) { Pair(ShadowKinds.Model, d.Model, h.Model); }
        if (d.Compactions != h.Compactions) { Pair(ShadowKinds.Compactions, d.Compactions.ToString(), h.Compactions.ToString()); }
        // Only when both have one: an empty response is not something either side shows, and
        // either may still hold the last turn's words for a moment.
        if (d.ResponseTail is not null && h.ResponseTail is not null && d.ResponseTail != h.ResponseTail)
        {
            diffs.Add(new(ShadowKinds.Response, s, "the end of the latest response differs"));
        }
        if (!d.QuestionUnknown) { Question(d.Question, h.Question, d.Agent, s, diffs); }
    }

    static void Question(ShadowQuestion? d, ShadowQuestion? h, string agent, string s, List<ShadowDifference> diffs)
    {
        if (d is null && h is null) { return; }
        if (h is null) { diffs.Add(new(ShadowKinds.QuestionMissingOnHost, s, $"daemon fields={d!.Fields.Count}")); return; }
        if (d is null) { diffs.Add(new(ShadowKinds.QuestionMissingOnDaemon, s, $"host fields={h.Fields.Count}")); return; }

        if (d.CallId is { Length: > 0 } dc && h.CallId is { Length: > 0 } hc)
        {
            if (dc != hc) { diffs.Add(new(ShadowKinds.QuestionIdentity, s, "the call ids differ")); }
        }
        // agent-ha-bridge names a Copilot question after its hook's clock, not the call, so the
        // same question is one asked within two minutes of the host's.
        else if (d.AskedAt is { } da && h.AskedAt is { } ha && (da - ha).Duration() > TimeSpan.FromMinutes(2))
        {
            diffs.Add(new(ShadowKinds.QuestionIdentity, s, $"asked {(long)(da - ha).Duration().TotalSeconds} s apart"));
        }
        if (d.AtMachine || h.AtMachine) { return; }

        // Claude's message is composed differently on each side (the host lists the options'
        // descriptions under it), so only Copilot's is compared.
        if (agent == "copilot" && d.MessageHead is not null && h.MessageHead is not null && !d.MessageHead.StartsWith(h.MessageHead, StringComparison.Ordinal))
        {
            diffs.Add(new(ShadowKinds.QuestionMessage, s, "the start of the message differs"));
        }
        if (d.Fields.Count != h.Fields.Count)
        {
            diffs.Add(new(ShadowKinds.QuestionFields, s, $"daemon={d.Fields.Count} host={h.Fields.Count}"));
            return;
        }
        var types = new List<string>();
        var options = new List<string>();
        for (var i = 0; i < d.Fields.Count; i++)
        {
            var (a, b) = (d.Fields[i], h.Fields[i]);
            if (a.Type != b.Type) { types.Add($"field {i + 1}: daemon={a.Type} host={b.Type}"); continue; }
            if (a.Options != b.Options) { options.Add($"field {i + 1}: daemon={a.Options} host={b.Options}"); }
            else if (a.OptionsHash != b.OptionsHash) { options.Add($"field {i + 1}: {a.Options} options, not the same ones"); }
        }
        if (types.Count > 0) { diffs.Add(new(ShadowKinds.QuestionFieldType, s, string.Join("; ", types.Take(4)))); }
        if (options.Count > 0) { diffs.Add(new(ShadowKinds.QuestionOptions, s, string.Join("; ", options.Take(4)))); }
    }

    // A question whose only field is free text is the same question as one with none:
    // agent-ha-bridge asks it in its reply box, the host as a text field.
    public static IReadOnlyList<ShadowField> Canonical(IReadOnlyList<ShadowField> fields) =>
        fields is [{ Type: "text" }] ? [] : fields;
}
