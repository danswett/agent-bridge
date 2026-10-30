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
    // The daemon only, in memory only: the ids behind Agents and Shells (the agents' tool calls,
    // the shells' ids) and how far into the transcript it has read, so a difference in them can
    // be checked against its known bugs (ShadowWitness).
    public IReadOnlyList<string> AgentIds { get; init; } = [];
    public IReadOnlyList<string> ShellIds { get; init; } = [];
    public long? Offset { get; init; }
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
    // Each agent's allowance by client (copilot, claude, codex), or null when this side's could
    // not be read this time, with why in UsageProblem. The daemon's comes from Home Assistant.
    public IReadOnlyDictionary<string, ShadowUsage>? Usage { get; init; }
    public string? UsageProblem { get; init; }
    // The resume list's labels, newest first, whitespace collapsed; in memory only. Null when
    // not read, with why in ResumableProblem.
    public IReadOnlyList<string>? Resumable { get; init; }
    public string? ResumableProblem { get; init; }
    // When this side's process started, so a restart between two samples is seen: the daemon's
    // from its runtime/daemon.process.json, the host's from host.status.
    public DateTimeOffset? StartedAt { get; init; }
    public bool Available => Problem is null;
}

// One agent's allowance as one side last read it. Account names are compared, never reported.
public sealed record ShadowUsage
{
    public bool Reading { get; init; }
    public string? Account { get; init; }
    public string? AccountKey { get; init; }
    public IReadOnlyDictionary<string, double> Windows { get; init; } = new Dictionary<string, double>();
    public DateTimeOffset? MeasuredAt { get; init; }
    // The daemon's only: until when its figure is known still to have held, which can be well
    // after MeasuredAt because it republishes an unchanged figure only now and then
    // (DaemonUsagePace). Null when no later than MeasuredAt.
    public DateTimeOffset? ConfirmedAt { get; init; }
    // The daemon's only: since when the same figure has stood, read after read, when earlier than
    // MeasuredAt (it republishes an unchanged figure with a new measured_at).
    public DateTimeOffset? HeldSince { get; init; }
    // The daemon's only: its figure is older than its own pace allows (DaemonUsagePace.Lapsed). It
    // asks the vendor about an account on one machine and stands down on the rest while that one's
    // readings are current (agent-ha-bridge#142's Test-BridgeUsageCovered), so this machine's sensor
    // then keeps the last figure it read itself, hours old. That is not a reading to compare: on
    // DEV-VM1 it was 11 hours old in every sample of a day, compared all the same, and no reading
    // was ever close enough to count (#40).
    public bool Lapsed { get; init; }
}

// One disagreement in one sample. Session is a hashed session id, or for a usage kind the agent
// (copilot, claude, codex); Detail comes from a fixed vocabulary with counts, enum values and
// model names in it, never anything a person or an agent wrote.
//
// Known: set when the evidence matches a known cause, which a report then counts apart from
// mismatches: an agent-ha-bridge issue ("agent-ha-bridge#261") whose bug signature this
// difference has, or "timing" when one side is showing what the other showed a moment ago.
public sealed record ShadowDifference(string Kind, string? Session, string Detail)
{
    public string? Known { get; init; }
}

// UsageCompared and ResumableCompared: whether those could be compared in this sample, and if
// not, why (NotCompared), so a clean day says how much of it they were looked at.
public sealed record ShadowComparison(bool Compared, int DaemonSessions, int HostSessions, IReadOnlyList<ShadowDifference> Differences, IReadOnlyList<string> SessionsSeen)
{
    public bool UsageCompared { get; init; }
    public bool ResumableCompared { get; init; }
    public IReadOnlyList<string> NotCompared { get; init; } = [];
    // The agents whose percentages were judged on a fresh pair of readings in this sample.
    public IReadOnlyList<string> UsageFresh { get; init; } = [];
}

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
    public const string UsageMissingOnHost = "usage.missingOnHost";
    public const string UsageMissingOnDaemon = "usage.missingOnDaemon";
    public const string UsageAccount = "usage.account";
    public const string UsageWindows = "usage.windows";
    public const string UsagePercent = "usage.percent";
    public const string ResumableCount = "resumable.count";
    public const string ResumableMissingOnHost = "resumable.missingOnHost";
    public const string ResumableMissingOnDaemon = "resumable.missingOnDaemon";

    // What neither side's records can show, so a clean report says nothing about it. Listed in
    // every report so a "clean" run is never read as covering them.
    public static readonly IReadOnlyList<string> NotCompared =
    [
        "usage of extra Copilot accounts (usage.copilotAccounts; only usage_copilot, usage_claude and usage_codex are read)",
        "titles (agent-ha-bridge names sessions for Home Assistant)",
        "reasoning and tool history (bounded differently, parity §1)",
        "driver and the purple edge (missing here, #46)",
        "notifications",
    ];

    public static bool IsMachine(string kind) => kind.StartsWith("machine.", StringComparison.Ordinal);

    // The least a disagreement of a kind must last to be a mismatch, whatever --grace says.
    // Either side down: the three missed beats agent-ha-bridge allows before calling a machine
    // offline, since both restart for updates. Usage: each side asks the vendor on its own two
    // minute pace. Resumable sessions: the daemon refreshes its list every three minutes
    // (ResumeCacheSeconds), so a session that just ended is resumable here first.
    public static TimeSpan MinimumGrace(string kind) =>
        IsMachine(kind) ? TimeSpan.FromSeconds(180)
        : kind.StartsWith("usage.", StringComparison.Ordinal) || kind.StartsWith("resumable.", StringComparison.Ordinal) ? TimeSpan.FromSeconds(300)
        : TimeSpan.Zero;
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

// What a run remembers between samples: each agent's last verdict on its percentages. The two
// sides read on their own paces (the daemon every two minutes, the host every fifteen while the
// daemon runs), so only now and then are both readings fresh enough to compare; in between,
// the last verdict stands, so a lasting disagreement is seen to last rather than flickering
// into timing skew, and polling lag never becomes one.
public sealed class ShadowMemory
{
    internal sealed class PercentVerdict
    {
        public (DateTimeOffset Daemon, DateTimeOffset Host)? Pair;
        public int Disagreeing;
        public string? Detail;
    }

    internal Dictionary<string, PercentVerdict> Percent { get; } = new(StringComparer.Ordinal);

    // Each session's latest-response tails as each side showed them over the last few minutes,
    // newest last, so one side showing what the other showed a moment ago is told from a
    // difference (ShadowCompare.Lagging).
    internal Dictionary<string, (List<(DateTimeOffset At, string Tail)> Daemon, List<(DateTimeOffset At, string Tail)> Host)> Responses { get; } = new(StringComparer.Ordinal);
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

    // The status agent-ha-bridge shows for a session, or null when its question marker was caught
    // half written. Nothing in it publishes waiting for a Copilot question: its status entity stays
    // working, from the transcript, for as long as ask_user is pending. Both its cards say "waiting
    // for you" whenever the session's decision entity carries a question, though, and the hook that
    // arms the marker is what puts one there, so an armed marker is what its user sees as waiting.
    // Seen live as the host's waiting against the daemon's working for minutes, while a session
    // asked its user a question with background agents running (#40).
    public static string? DaemonStatus(ShadowSession d) =>
        d.QuestionUnknown ? null : d.Question is not null ? "waiting" : Comparable(d.Status);

    // The daemon's status as judged against the host's: with its marker half written, whichever of
    // its own status and waiting the host shows, so that sample is not a disagreement either way.
    public static string ComparedDaemonStatus(ShadowSession d, string hostStatus) =>
        DaemonStatus(d) ?? (hostStatus == "waiting" ? "waiting" : Comparable(d.Status));

    public static string HostStatus(SessionStatus status) => status switch
    {
        SessionStatus.Working => "working",
        SessionStatus.Waiting => "waiting",
        SessionStatus.Ending => "ending",
        _ => "idle",
    };

    public static ShadowComparison Compare(ShadowView daemon, ShadowView host, ShadowMemory? memory = null, DateTimeOffset? at = null)
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
            Session(d, h, s, diffs, memory is null || at is null ? null : Lagging(memory, s, d.ResponseTail, h.ResponseTail, at.Value));
        }
        if (memory is not null) { foreach (var gone in memory.Responses.Keys.Where(k => !seen.Contains(k)).ToList()) { memory.Responses.Remove(gone); } }
        var notCompared = new List<string>();
        var fresh = new List<string>();
        var usage = Usage(daemon, host, diffs, notCompared, memory, fresh);
        var resumable = Resumable(daemon, host, diffs, notCompared);
        return new(true, daemon.Sessions.Count, host.Sessions.Count, diffs, seen) { UsageCompared = usage, ResumableCompared = resumable, NotCompared = notCompared, UsageFresh = fresh };
    }

    // How long a response one side showed is remembered, to recognise the other side catching up
    // to it. A session whose background agents talk all the time changes its latest response every
    // few seconds; the daemon writes it on its own pass and the host within a second, so for as
    // long as the talk goes on the two show different words, each the other's a moment before or
    // after, and that ran past the grace (#40, seen live).
    static readonly TimeSpan ResponseMemory = TimeSpan.FromMinutes(3);

    // Whether the two latest responses differ only because one side is showing what the other
    // showed within the last few minutes. Records this sample's tails either way.
    static bool Lagging(ShadowMemory memory, string session, string? daemon, string? host, DateTimeOffset at)
    {
        if (!memory.Responses.TryGetValue(session, out var seen)) { memory.Responses[session] = seen = ([], []); }
        // Each entry is when that side last showed it: one shown for an hour was shown a moment ago.
        static void Note(List<(DateTimeOffset At, string Tail)> list, string? tail, DateTimeOffset at)
        {
            if (tail is not null && list.Count > 0 && list[^1].Tail == tail) { list[^1] = (at, tail); }
            else if (tail is not null) { list.Add((at, tail)); }
            list.RemoveAll(x => at - x.At > ResponseMemory);
            if (list.Count > 64) { list.RemoveAt(0); }
        }
        Note(seen.Daemon, daemon, at);
        Note(seen.Host, host, at);
        if (daemon is null || host is null || daemon == host) { return false; }
        // One is behind when what it shows is something the other showed and has since moved on from.
        return seen.Host.Take(seen.Host.Count - 1).Any(x => x.Tail == daemon) || seen.Daemon.Take(seen.Daemon.Count - 1).Any(x => x.Tail == host);
    }

    // Percent gone may move between two reads; within this it is the same reading, and further
    // apart in time it is not compared at all. Close together because the two read on their own
    // paces (the daemon every two minutes, the host every fifteen while the daemon runs), and a
    // host reading thirteen minutes old would otherwise be a mismatch for its whole age. The
    // daemon's figure is as of any moment from when it first stood (HeldSince) to its confirmation
    // (ConfirmedAt), since it republishes an unchanged one only now and then.
    const double PercentTolerance = 5;
    static readonly TimeSpan ReadingsApart = TimeSpan.FromMinutes(3);

    public const string UsageLapsed = "usage: the daemon is not reading it on this machine (its figure is older than its own pace; another machine may read the account)";

    static bool Usage(ShadowView daemon, ShadowView host, List<ShadowDifference> diffs, List<string> notCompared, ShadowMemory? memory, List<string> judgedFresh)
    {
        if (daemon.Usage is null) { notCompared.Add("usage: " + (daemon.UsageProblem ?? "the daemon's was not read")); }
        if (host.Usage is null) { notCompared.Add("usage: " + (host.UsageProblem ?? "the host's was not read")); }
        if (daemon.Usage is null || host.Usage is null) { return false; }
        var judged = new HashSet<string>(StringComparer.Ordinal);
        // Compared when any agent's readings were, or neither side had one to compare.
        var (readings, compared) = (false, false);
        foreach (var client in daemon.Usage.Keys.Union(host.Usage.Keys).Order(StringComparer.Ordinal))
        {
            var a = daemon.Usage.GetValueOrDefault(client);
            var b = host.Usage.GetValueOrDefault(client);
            var name = ShadowHash.Token(client);
            if (a?.Reading != true && b?.Reading != true) { continue; }
            readings = true;
            // Before either side's missing reading: a lapsed figure says nothing about now, whatever
            // the host has.
            if (a is { Reading: true, Lapsed: true })
            {
                if (!notCompared.Contains(UsageLapsed)) { notCompared.Add(UsageLapsed); }
                continue;
            }
            compared = true;
            if (b?.Reading != true) { diffs.Add(new(ShadowKinds.UsageMissingOnHost, name, "the daemon has a reading and the host none")); continue; }
            if (a?.Reading != true) { diffs.Add(new(ShadowKinds.UsageMissingOnDaemon, name, "the host has a reading and the daemon none")); continue; }
            if (Differs(a.Account, b.Account) || Differs(a.AccountKey, b.AccountKey))
            {
                diffs.Add(new(ShadowKinds.UsageAccount, name, "the account differs"));
            }
            var keys = (a.Windows.Keys.Order(StringComparer.Ordinal).ToList(), b.Windows.Keys.Order(StringComparer.Ordinal).ToList());
            if (!keys.Item1.SequenceEqual(keys.Item2))
            {
                diffs.Add(new(ShadowKinds.UsageWindows, name, $"daemon={string.Join(',', keys.Item1.Select(ShadowHash.Token))} host={string.Join(',', keys.Item2.Select(ShadowHash.Token))}"));
            }
            string? detail = null;
            var (sa, sb) = (Held(a), Held(b));
            var fresh = sa is { } x && sb is { } y && Apart(x, y) <= ReadingsApart;
            if (fresh)
            {
                judgedFresh.Add(client);
                var off = keys.Item1.Intersect(keys.Item2)
                    .Where(k => Math.Abs(a.Windows[k] - b.Windows[k]) > PercentTolerance)
                    .Select(k => $"{ShadowHash.Token(k)} daemon={Math.Round(a.Windows[k])} host={Math.Round(b.Windows[k])}")
                    .ToList();
                if (off.Count > 0) { detail = string.Join("; ", off.Take(4)); }
            }
            if (memory is null)
            {
                if (detail is not null) { diffs.Add(new(ShadowKinds.UsagePercent, name, detail)); }
                continue;
            }
            // A run: judged once per new pair of fresh readings, a disagreement counted only once
            // two pairs in a row show one, and the verdict standing until a fresh pair agrees.
            judged.Add(client);
            if (!memory.Percent.TryGetValue(client, out var verdict)) { memory.Percent[client] = verdict = new(); }
            if (fresh && (sa!.Value.To, sb!.Value.To) is var pair && verdict.Pair != pair)
            {
                if (detail is null)
                {
                    verdict.Pair = pair;
                    verdict.Disagreeing = 0;
                    verdict.Detail = null;
                }
                // Evidence only when both sides have read again: a daemon update against the same
                // host reading is the host not having read the change yet, not a second witness.
                else if (verdict.Pair is not { } last || (pair.Item1 > last.Daemon && pair.Item2 > last.Host))
                {
                    verdict.Pair = pair;
                    verdict.Disagreeing++;
                    verdict.Detail = detail;
                }
            }
            if (verdict.Disagreeing >= 2) { diffs.Add(new(ShadowKinds.UsagePercent, name, verdict.Detail!)); }
        }
        // An agent no longer read on both sides starts afresh when it is.
        if (memory is not null) { foreach (var gone in memory.Percent.Keys.Where(k => !judged.Contains(k)).ToList()) { memory.Percent.Remove(gone); } }
        return compared || !readings;

        // When a side's figure is known to have held: from its measurement to its confirmation.
        static (DateTimeOffset From, DateTimeOffset To)? Held(ShadowUsage u) =>
            u.MeasuredAt is { } m ? (u.HeldSince is { } s && s < m ? s : m, u.ConfirmedAt is { } c && c > m ? c : m) : null;

        static TimeSpan Apart((DateTimeOffset From, DateTimeOffset To) x, (DateTimeOffset From, DateTimeOffset To) y) =>
            x.From > y.To ? x.From - y.To : y.From > x.To ? y.From - x.To : TimeSpan.Zero;

        // Empty on either side is not a disagreement: some agents name no account.
        static bool Differs(string? x, string? y) =>
            !string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y) && !string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    // The labels each side offers, which the host writes exactly as agent-ha-bridge does
    // ("<summary> - <folder>", the agent named when there are several kinds). Each side cuts
    // its list to its own resume count, so when the lengths differ only the shorter list's
    // labels are looked for in the longer one.
    static bool Resumable(ShadowView daemon, ShadowView host, List<ShadowDifference> diffs, List<string> notCompared)
    {
        if (daemon.Resumable is null) { notCompared.Add("resumable: " + (daemon.ResumableProblem ?? "the daemon's was not read")); }
        if (host.Resumable is null) { notCompared.Add("resumable: " + (host.ResumableProblem ?? "the host's was not read")); }
        if (daemon.Resumable is not { } d || host.Resumable is not { } h) { return false; }
        var (ds, hs) = (d.ToHashSet(StringComparer.Ordinal), h.ToHashSet(StringComparer.Ordinal));
        if (d.Count != h.Count) { diffs.Add(new(ShadowKinds.ResumableCount, null, $"daemon={d.Count} host={h.Count}")); }
        if (d.Count <= h.Count && d.Count(l => !hs.Contains(l)) is > 0 and var missingHere)
        {
            diffs.Add(new(ShadowKinds.ResumableMissingOnHost, null, $"{missingHere} of the daemon's {d.Count}"));
        }
        if (h.Count <= d.Count && h.Count(l => !ds.Contains(l)) is > 0 and var missingThere)
        {
            diffs.Add(new(ShadowKinds.ResumableMissingOnDaemon, null, $"{missingThere} of the host's {h.Count}"));
        }
        return true;
    }

    static void Session(ShadowSession d, ShadowSession h, string s, List<ShadowDifference> diffs, bool? responseLagging = null)
    {
        void Pair(string kind, string? a, string? b) => diffs.Add(new(kind, s, $"daemon={ShadowHash.Token(a)} host={ShadowHash.Token(b)}"));

        if (d.Agent != h.Agent) { Pair(ShadowKinds.Agent, d.Agent, h.Agent); }
        var expected = ComparedDaemonStatus(d, h.Status);
        // parity §3: a pending permission prompt is beyond parity, so the host's waiting is the
        // daemon's working.
        if (expected != h.Status && !(h.PermissionPending && h.Status == "waiting" && expected == "working")) { Pair(ShadowKinds.Status, d.Question is not null ? "waiting" : d.Status, h.Status); }
        if (d.Agents != h.Agents) { Pair(ShadowKinds.BackgroundAgents, d.Agents.ToString(), h.Agents.ToString()); }
        if (d.Shells != h.Shells) { Pair(ShadowKinds.BackgroundShells, d.Shells.ToString(), h.Shells.ToString()); }
        if (!string.Equals(d.Model ?? "", h.Model ?? "", StringComparison.Ordinal)) { Pair(ShadowKinds.Model, d.Model, h.Model); }
        if (d.Compactions != h.Compactions) { Pair(ShadowKinds.Compactions, d.Compactions.ToString(), h.Compactions.ToString()); }
        // Only when both have one: an empty response is not something either side shows, and
        // either may still hold the last turn's words for a moment.
        if (d.ResponseTail is not null && h.ResponseTail is not null && d.ResponseTail != h.ResponseTail)
        {
            diffs.Add(new(ShadowKinds.Response, s, "the end of the latest response differs") { Known = responseLagging == true ? Timing : null });
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
        else if (!SameQuestion(d, h))
        {
            diffs.Add(new(ShadowKinds.QuestionIdentity, s, $"asked {(long)(d.AskedAt!.Value - h.AskedAt!.Value).Duration().TotalSeconds} s apart"));
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

    public const string Timing = "timing";

    // Whether two sides' questions are the same one: the same call when both know it (Claude);
    // otherwise, since agent-ha-bridge names a Copilot question after its hook's clock and not the
    // call, one asked within two minutes of the other; and the same when nothing tells them apart.
    public static bool SameQuestion(ShadowQuestion d, ShadowQuestion h) =>
        d.CallId is { Length: > 0 } dc && h.CallId is { Length: > 0 } hc ? dc == hc
        : d.AskedAt is not { } da || h.AskedAt is not { } ha || (da - ha).Duration() <= TimeSpan.FromMinutes(2);

    // A question's kind for coverage: a multiple-choice field makes it multi, any other choice
    // makes it choice, and one with only free text (or nothing to fill in) is text.
    public static string QuestionKind(ShadowQuestion q) =>
        q.Fields.Any(f => f.Type == "multi") ? "multi" : q.Fields.Any(f => f.Type == "choice") ? "choice" : "text";

    // A question whose only field is free text is the same question as one with none:
    // agent-ha-bridge asks it in its reply box, the host as a text field.
    public static IReadOnlyList<ShadowField> Canonical(IReadOnlyList<ShadowField> fields) =>
        fields is [{ Type: "text" }] ? [] : fields;
}
