using AgentBridge.Transport;

namespace AgentBridge.Host;

// security.roster on each relay connection (#3450, brief 181). Off is today's one-shot report and never
// touches the retained reader. Observe feeds the relay's roster to HostRosterReader.Update on connect and
// again about every five minutes while connected, because the relay sends hosts no notice when the device
// roster changes. Read-only: roster-pin.json is never created, written or advanced here, and the only
// decision this moves is what HostRosterReader.Pinned hands the fleet gate. A failed fetch, signature,
// account or generation leaves an unverified reader unverified, so the gate keeps refusing.
internal sealed class HostRosterObserve(HostRosterReader reader, Func<string> mode, Func<TimeSpan> clock, Action<string> log,
    TimeProvider? time = null, Func<double>? random = null)
{
    static readonly HashSet<string> Codes = ["verified", "no-roster", "pin-mismatch", "head-regress", "pin-unreadable"];
    readonly object sync = new();
    string? state;

    // Plus or minus 20 percent so hosts do not fetch in step; doubles per failed pass, up to 40 minutes.
    public static TimeSpan Next(int failures, double random) =>
        TimeSpan.FromMinutes(Math.Min(40, 5 * (1 << Math.Min(failures, 3)) * (0.8 + 0.4 * random)));

    public async Task ConnectedAsync(Func<CancellationToken, Task<RelayRoster?>> get, string account, Task closed, CancellationToken ct)
    {
        var current = mode();
        if (current != SecuritySettings.Observe)
        {
            var check = await HostRoster.SyncAsync(get, account, clock, ct);
            log($"device roster (security.roster {current}): {check.Summary}");
            return;
        }
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Preserve the one-shot pass for an already-completed lifetime; watch live connections.
        var ended = closed.IsCompleted ? null : closed;
        var watching = ended is null ? Task.CompletedTask : CancelOnCloseAsync();
        try
        {
            var failures = 0;
            while (!connection.IsCancellationRequested)
            {
                failures = await PassAsync(get, account, ended, connection.Token) ? 0 : Math.Min(failures + 1, 3);
                if (closed.IsCompleted || connection.IsCancellationRequested) { return; }
                await Task.WhenAny(Task.Delay(Next(failures, random?.Invoke() ?? Random.Shared.NextDouble()),
                    time ?? TimeProvider.System, connection.Token), closed);
                if (closed.IsCompleted) { return; }
            }
        }
        finally { connection.Cancel(); await watching; }

        async Task CancelOnCloseAsync()
        {
            await Task.WhenAny(closed, Task.Delay(Timeout.InfiniteTimeSpan, connection.Token));
            connection.Cancel();
        }
    }

    // One line per change of state, with a closed reason code and never the roster's contents.
    async Task<bool> PassAsync(Func<CancellationToken, Task<RelayRoster?>> get, string account, Task? closed, CancellationToken ct)
    {
        string code;
        try
        {
            var roster = await get(ct);
            if (ct.IsCancellationRequested || closed?.IsCompleted == true) { return false; }
            var check = reader.Update(roster, account);
            code = check.Reasons.Contains("unpinned") ? "unpinned" : Codes.Contains(check.Outcome) ? check.Outcome : "not-verified";
        }
        catch (Exception) when (ct.IsCancellationRequested || closed?.IsCompleted == true) { return false; }
        catch (Exception) { code = "unavailable"; }
        lock (sync)
        {
            if (code == state) { return code is "verified" or "unpinned"; }
            state = code;
        }
        log($"device roster (security.roster observe): {code}");
        return code is "verified" or "unpinned";
    }
}
