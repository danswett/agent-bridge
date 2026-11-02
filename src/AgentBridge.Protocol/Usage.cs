// Bridge Protocol v1, agent usage: how much of each agent's allowance is gone. The contract
// is docs/protocol/v1-usage.md; these records mirror it and serialize to exactly that JSON
// with ProtocolJson.Options.
namespace AgentBridge.Protocol;

// Where a reading came from. A client tells a reading from a failure by its windows, not by
// this, so a source it does not know changes nothing it shows.
[UnknownAs(None)]
public enum UsageSource { Api, Cache, Transcript, None }

// One metered window. Percent is always how much is gone, whatever the vendor reports.
public sealed record UsageWindow
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required double Percent { get; init; }
    public double? Used { get; init; }
    public double? Limit { get; init; }
    public string? Unit { get; init; }
    // Both ends, when known, so a client works out how far through the window it is at the
    // moment it draws, rather than when the host read it.
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}

// One agent's allowance as this machine last read it: a reading (windows), or a failure
// (no windows, an error).
public sealed record AgentUsage
{
    public required AgentKind Agent { get; init; }
    // What to call it, so a client can show an agent it does not know.
    public required string Label { get; init; }
    // The account as the vendor names it (a GitHub login), when it names one.
    public string? Account { get; init; }
    // An opaque key for an account no vendor names (Claude's), so clients can tell two
    // accounts apart without being told who either is.
    public string? AccountKey { get; init; }
    public string? Plan { get; init; }
    public required UsageSource Source { get; init; }
    public IReadOnlyList<UsageWindow> Windows { get; init; } = [];
    public string? Error { get; init; }
    // When the figure was taken; for a failure, when the attempt was made.
    public required DateTimeOffset MeasuredAt { get; init; }
    // A further account the machine was asked to report beside the one the agent is signed in as
    // (usage.copilotAccounts), by its place: 2 for the first listed. Absent for the signed-in one.
    // Stable while the list is, so a Home Assistant sensor keeps its account (usage_copilot_<slot>).
    public int? Slot { get; init; }
    // The machine that took the reading, on one a host passes on from another of the account's
    // machines (usage.shared, #172). Absent on the host's own readings.
    public UsageReader? ReadBy { get; init; }
}

// Which machine took a reading: its device id and the name it gave the relay.
public sealed record UsageReader(string Id, string Name);

// usage.get's and usage.subscribe's request. shared: the other machines' readings too, where the
// host offers usage.shared; a host without it ignores the field and sends its own.
public sealed record UsageRequest
{
    public bool Shared { get; init; }
}

// usage.get's result, usage.subscribe's result and every usage.update. Agents that have never
// run on this machine are absent: "no data" and "none used" are opposites.
public sealed record UsageReport(IReadOnlyList<AgentUsage> Agents)
{
    public static readonly UsageReport Empty = new([]);
}

public static class UsageProtocol
{
    public const string Capability = "usage";
    public const string Get = "usage.get";
    public const string Subscribe = "usage.subscribe";
    public const string Update = "usage.update";
    // usage.get and usage.subscribe take { shared: true } and add the readings this host keeps
    // from the account's other machines, each with readBy (docs/protocol/v1-usage.md, #172).
    public const string SharedCapability = "usage.shared";

    // Field limits, in UTF-8 bytes of the text.
    public const int Label = 64;
    public const int ReaderName = 64;
    public const int Account = 100;
    public const int AccountKey = 64;
    public const int Plan = 64;
    public const int Error = 512;
    public const int WindowKey = 32;
    public const int Unit = 16;
    public const int MaxWindows = 6;
    public const int MaxAgents = 8;
    // Every report as sent fits this, which a usage.update event always can.
    public const int MaxReportBytes = 24 * 1024;
    // A shared report: the host's own readings as above, then other machines' up to these.
    public const int MaxSharedAgents = 16;
    public const int MaxSharedReportBytes = 48 * 1024;

    // Applies the field limits, then trims to the report budget: errors beside a reading go
    // first (the reading is what a client shows), then the last agents.
    public static UsageReport Fit(UsageReport report)
    {
        var agents = report.Agents.Take(MaxAgents).Select(FitAgent).ToList();
        var fitted = new UsageReport(agents);
        if (ProtocolJson.ByteCount(fitted) <= MaxReportBytes) { return fitted; }
        agents = [.. agents.Select(a => a.Windows.Count > 0 ? a with { Error = null } : a)];
        while (agents.Count > 1 && ProtocolJson.ByteCount(new UsageReport(agents)) > MaxReportBytes) { agents.RemoveAt(agents.Count - 1); }
        return new UsageReport(agents);
    }

    public static AgentUsage FitAgent(AgentUsage a) => a with
    {
        Label = Limits.KeepStart(a.Label, Label)!,
        Account = Limits.KeepStart(a.Account, Account),
        AccountKey = Limits.KeepStart(a.AccountKey, AccountKey),
        Plan = Limits.KeepStart(a.Plan, Plan),
        Error = Limits.KeepStart(a.Error, Error),
        Windows = [.. a.Windows.Take(MaxWindows).Select(w => w with
        {
            Key = Limits.KeepStart(w.Key, WindowKey)!,
            Label = Limits.KeepStart(w.Label, Label)!,
            Unit = Limits.KeepStart(w.Unit, Unit),
        })],
        // A reader that is not a device id names nobody, and is dropped rather than shown.
        ReadBy = a.ReadBy is { } r && TransferProtocol.IsDeviceId(r.Id)
            ? r with { Name = Limits.KeepStart(new string([.. (r.Name ?? "").Where(c => !char.IsControl(c))]), ReaderName)! }
            : null,
    };
}

// The readings a host serves with usage.shared (#172): its own, then the freshest it holds from
// the account's other machines. Grouped as clients group them (UsageMerge, pwa/js/usage.js).
public static class UsageShare
{
    // One allowance: an agent and its account.
    public static string Key(AgentUsage a) => $"{a.Agent}\0{a.AccountKey ?? (a.Account ?? "").ToLowerInvariant()}";

    // Whether a client would show a over b: a reading over a failure however new, since a
    // failure is stamped with when the attempt was made, then the later one.
    public static bool Outranks(AgentUsage a, AgentUsage? b)
    {
        if (b is null) { return true; }
        var (ra, rb) = (a.Windows.Count > 0, b.Windows.Count > 0);
        return ra != rb ? ra : a.MeasuredAt > b.MeasuredAt;
    }

    // own as the host reports it to anyone (already fitted), then each peer reading a client would
    // show over the host's own for that allowance, and each peer failure newer than anything the
    // host has for it, readings before failures and newest first, while they fit the shared limits:
    // failures go first, then the oldest readings. A peer's reading that is not newer than what
    // this machine read itself is left out: the host's own reading stands. A failure is judged by
    // its time alone, since it only lends its error to whichever reading a client shows; ranked as
    // a reading would be, it was dropped wherever the host had a reading of its own however old.
    public static UsageReport Serve(UsageReport own, IEnumerable<AgentUsage> peers)
    {
        var mine = new Dictionary<string, AgentUsage>(StringComparer.Ordinal);
        var latest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var a in own.Agents)
        {
            var k = Key(a);
            if (!mine.TryGetValue(k, out var b) || Outranks(a, b)) { mine[k] = a; }
            if (!latest.TryGetValue(k, out var t) || a.MeasuredAt > t) { latest[k] = a.MeasuredAt; }
        }
        bool Passes(AgentUsage p) => p.Windows.Count > 0
            ? Outranks(p, mine.GetValueOrDefault(Key(p)))
            : !latest.TryGetValue(Key(p), out var t) || p.MeasuredAt > t;
        var agents = own.Agents.ToList();
        foreach (var p in peers.Where(p => p.ReadBy is not null && Passes(p))
            .OrderByDescending(p => p.Windows.Count > 0).ThenByDescending(p => p.MeasuredAt))
        {
            if (agents.Count >= UsageProtocol.MaxSharedAgents) { break; }
            agents.Add(UsageProtocol.FitAgent(p));
            if (ProtocolJson.ByteCount(new UsageReport(agents)) > UsageProtocol.MaxSharedReportBytes)
            {
                agents.RemoveAt(agents.Count - 1);
                break;
            }
        }
        return new UsageReport(agents);
    }
}
