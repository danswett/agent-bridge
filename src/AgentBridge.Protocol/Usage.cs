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

    // Field limits, in UTF-8 bytes of the text.
    public const int Label = 64;
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
    };
}
