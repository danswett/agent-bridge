using System.Text;
using AgentBridge.Roster;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// What this host found when it read the account's signed device roster from the relay and
// verified it itself (docs/protocol/devices.md, "The host's verifier"). Off mode: a report and
// nothing else; no connection is refused because of it.
public sealed record RosterCheck(
    string Outcome,
    string RelayMode,
    int Entries,
    int Accepted,
    int Quarantined,
    int Duplicate,
    int Refused,
    int Unreadable,
    IReadOnlyList<string> Heads,
    IReadOnlyList<string> Reasons)
{
    public string Summary => Outcome switch
    {
        "verified" => $"verified {Entries} entries from the relay ({Accepted} accepted, {Quarantined} quarantined, {Refused} refused, {Unreadable} unreadable); heads {string.Join(",", Heads)}; enforcing nothing",
        _ => $"not verified: {Outcome}; enforcing nothing",
    };
}

public static class HostRoster
{
    // The relay is an untrusted store, so nothing it says is taken: the generation is learned from
    // the first genesis it holds for this account, and every entry is verified against the signed
    // log on this host's own clock. In off mode the generation is not pinned, so this protects
    // nothing against a hostile relay; pinning and enforcing are later slices.
    public static RosterCheck Verify(RelayRoster relay, string account, Func<TimeSpan> clock)
    {
        var entries = new List<Entry>();
        var unreadable = 0;
        foreach (var text in relay.Entries)
        {
            try { entries.Add(Entry.Parse(Encoding.UTF8.GetBytes(text))); }
            catch (RosterFormatException) { unreadable++; }
        }
        if (entries.Count == 0) { return Empty(relay, unreadable, relay.Entries.Count == 0 ? "the relay holds no roster yet" : "no entry could be read"); }
        var genesis = entries.FirstOrDefault(e => e.Body.Op == EntryOp.Genesis && e.Body.Account == account);
        if (genesis is null) { return Empty(relay, unreadable, "the relay holds no genesis for this account"); }

        var verifier = new RosterVerifier(account, genesis.Body.Generation, clock);
        var offered = verifier.Offer(entries);
        int Count(Verdict v) => offered.Count(o => o.Verdict == v);
        return new RosterCheck(
            "verified", relay.Mode, relay.Entries.Count,
            Count(Verdict.Accepted), Count(Verdict.Quarantined), Count(Verdict.Duplicate), Count(Verdict.Refused), unreadable,
            [.. verifier.Heads.Select(h => Convert.ToHexString(h)[..12].ToLowerInvariant())],
            [.. offered.Where(o => o.Reason is not null).Select(o => o.Reason!).Distinct().Order()]);
    }

    static RosterCheck Empty(RelayRoster relay, int unreadable, string why) =>
        new(why, relay.Mode, relay.Entries.Count, 0, 0, 0, 0, unreadable, [], []);

    // Reads and verifies once; never throws, so a relay that cannot answer, or says something this
    // host cannot make sense of, costs a log line and nothing else.
    public static async Task<RosterCheck> SyncAsync(RelayClient relay, Func<TimeSpan> clock, CancellationToken ct)
    {
        try
        {
            var roster = await relay.GetRosterAsync(ct);
            return roster is null
                ? new RosterCheck("the relay gave no roster", "", 0, 0, 0, 0, 0, 0, [], [])
                : Verify(roster, relay.AccountId, clock);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new RosterCheck("the host is stopping or reconnecting", "", 0, 0, 0, 0, 0, 0, [], []);
        }
        catch (Exception e)
        {
            return new RosterCheck($"the roster could not be read ({e.GetType().Name})", "", 0, 0, 0, 0, 0, 0, [], []);
        }
    }

    public static TimeSpan Now() => DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch;
}
