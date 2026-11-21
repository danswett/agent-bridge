using AgentBridge.Transport;

namespace AgentBridge.Host;

// security.roster on each relay connection (#3450, brief 181). Off is today's one-shot report and never
// touches the retained reader. Observe feeds the relay's roster to HostRosterReader.Update on connect and
// again about every five minutes while connected, because the relay sends hosts no notice when the device
// roster changes. Read-only: roster-pin.json is never created, written or advanced here, and the only
// decision this moves is what HostRosterReader.Pinned hands the fleet gate. A failed fetch, signature,
// account or generation leaves an unverified reader unverified, so the gate keeps refusing.
internal sealed class HostRosterObserve(HostRosterReader reader, Func<string> mode, Func<TimeSpan> clock, Action<string> log)
{
    static readonly HashSet<string> Codes = ["verified", "no-roster", "pin-mismatch", "head-regress", "pin-unreadable"];
    readonly object sync = new();
    string? state;

    // Plus or minus 20 percent so hosts do not fetch in step; doubles per failed pass, up to 40 minutes.
    public static TimeSpan Next(int failures, double random) =>
        TimeSpan.FromMinutes(5 * (1 << Math.Min(failures, 3)) * (0.8 + 0.4 * random));

    public async Task ConnectedAsync(Func<CancellationToken, Task<RelayRoster?>> get, string account, Task closed, CancellationToken ct)
    {
        var current = mode();
        if (current != SecuritySettings.Observe)
        {
            var check = await HostRoster.SyncAsync(get, account, clock, ct);
            log($"device roster (security.roster {current}): {check.Summary}");
            return;
        }
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            failures = await PassAsync(get, account, ct) ? 0 : failures + 1;
            if (closed.IsCompleted) { return; }
            await Task.WhenAny(Task.Delay(Next(failures, Random.Shared.NextDouble()), ct), closed);
            if (closed.IsCompleted) { return; }
        }
    }

    // One line per change of state, with a closed reason code and never the roster's contents.
    async Task<bool> PassAsync(Func<CancellationToken, Task<RelayRoster?>> get, string account, CancellationToken ct)
    {
        string code;
        try
        {
            var check = reader.Update(await get(ct), account);
            code = check.Reasons.Contains("unpinned") ? "unpinned" : Codes.Contains(check.Outcome) ? check.Outcome : "not-verified";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception) { code = "unavailable"; }
        lock (sync)
        {
            if (code == state) { return code == "verified"; }
            state = code;
        }
        log($"device roster (security.roster observe): {code}");
        return code == "verified";
    }
}
