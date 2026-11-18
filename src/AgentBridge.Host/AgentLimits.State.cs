namespace AgentBridge.Host;

public sealed partial class AgentLimits
{
    internal sealed record SavedHit(DateTimeOffset At, Guid? Charge);
    internal sealed record SavedWindow(string Origin, SavedHit[] Hits);
    internal sealed record SavedLive(string? Session, DateTimeOffset? IdleSince, bool PersonTyped, Guid Charge);
    internal sealed record SavedRequest(string Origin, string Request, string Hash, DateTimeOffset At, SavedLive? Live,
        Guid? Charge = null, bool DebitExpired = false);
    internal sealed record SavedLaunchState(int V, SavedRequest[] Requests, SavedWindow[] Origins,
        SavedWindow[] Attempts, SavedHit[] Machine, DateTimeOffset Last);

    internal SavedLaunchState SnapshotLaunches()
    {
        lock (gate)
        {
            var now = LaunchTime();
            var tags = new Dictionary<object, Guid>(ReferenceEqualityComparer.Instance);
            foreach (var request in launchRequests.Values)
            { if (request.Charge is { } charge) { tags.Add(charge, Guid.NewGuid()); } }
            // Retained debit evidence, not a clock that can roll back, determines expiry.
            var machine = machineLaunches.Snapshot(now, tags);
            var active = machine.Select(h => h.Charge).ToHashSet();
            var requests = launchRequests.Select(p =>
            {
                var live = liveLaunches.GetValueOrDefault(p.Key);
                return new SavedRequest(p.Key.Origin, p.Key.Request, p.Value.Hash, p.Value.At,
                    live is null ? null : new(live.Session, live.IdleSince, live.PersonTyped, tags[live.Charge!]),
                    p.Value.Charge is { } charge ? tags[charge] : null,
                    p.Value.Charge is { } debit && !active.Contains(tags[debit]));
            }).ToArray();
            SavedWindow[] Windows(Dictionary<string, Window> windows) =>
                windows.Select(p => new SavedWindow(p.Key, p.Value.Snapshot(now, tags))).ToArray();
            return new(1, requests, Windows(originLaunches), Windows(launchAttempts), machine, now);
        }
    }

    internal void RestoreLaunches(SavedLaunchState state)
    {
        static bool Text(string? text) => text is { Length: > 0 and <= 256 };
        if (state.V != 1 || state.Last == default || state.Requests is null || state.Origins is null
            || state.Attempts is null || state.Machine is null || state.Requests.Length > MaxKeys
            || state.Requests.Any(r => r is null || !Text(r.Origin) || !Text(r.Request) || !Text(r.Hash) || r.At == default || r.At > state.Last
                || r.Live is { } l && (l.Charge == Guid.Empty || l.Session is not null && !Text(l.Session))))
        { throw new InvalidDataException("Invalid launch request ledger."); }
        var requests = state.Requests.Select(r => r with { Request = CanonicalRequest(r.Request), Charge = r.Charge ?? r.Live?.Charge }).ToArray();
        var charged = requests.Where(r => r.Charge is not null).ToArray();
        var live = requests.Where(r => r.Live is not null).ToArray();
        if (requests.Select(r => (r.Origin, r.Request)).Distinct().Count() != requests.Length
            || requests.Any(r => r.DebitExpired && r.Charge is null)
            || charged.Any(r => r.Charge == Guid.Empty) || charged.Select(r => r.Charge).Distinct().Count() != charged.Length
            || live.Any(r => r.Live!.Charge != r.Charge)
            || live.Where(r => r.Live!.Session is not null).GroupBy(r => r.Live!.Session).Any(g => g.Count() > 1))
        { throw new InvalidDataException("Invalid live launch ledger."); }
        var tags = charged.ToDictionary(r => r.Charge!.Value, _ => new object());
        bool Hits(SavedHit[]? hits) => hits is not null && hits.Length <= MaxKeys
            && hits.All(h => h is not null && h.At != default && h.At <= state.Last && state.Last - h.At < Hour
                && (h.Charge is null || tags.ContainsKey(h.Charge.Value)))
            && hits.Select(h => h.At).SequenceEqual(hits.Select(h => h.At).Order());
        foreach (var windows in new[] { state.Origins, state.Attempts })
        {
            if (windows.Length > MaxKeys || windows.Any(w => w is null || !Text(w.Origin) || !Hits(w.Hits))
                || windows.Select(w => w.Origin).Distinct().Count() != windows.Length)
            { throw new InvalidDataException("Invalid hourly launch ledger."); }
        }
        if (!Hits(state.Machine)) { throw new InvalidDataException("Invalid machine launch ledger."); }
        bool Match(SavedHit[] hits, SavedRequest[] expected) => hits.Length == expected.Length
            && hits.Select(h => h.Charge).Distinct().Count() == hits.Length
            && hits.All(h => h.Charge is { } tag && expected.Any(r => r.Charge == tag && r.At == h.At));
        var current = charged.Where(r => !r.DebitExpired && state.Last - r.At < Hour).ToArray();
        if (!Match(state.Machine, current)
            || state.Origins.Any(w => !Match(w.Hits, current.Where(r => r.Origin == w.Origin).ToArray()))
            || current.Any(r => !state.Origins.Any(w => w.Origin == r.Origin))
            || state.Attempts.Any(w => w.Hits.Any(h => h.Charge is not null)))
        { throw new InvalidDataException("Launch requests and hourly debits disagree."); }
        foreach (var row in requests)
        {
            var key = (row.Origin, row.Request);
            launchRequests.Add(key, (row.Hash, row.At, row.Charge is { } charge ? tags[charge] : null));
            if (row.Live is { } l) { liveLaunches.Add(key, new(l.Session, l.IdleSince, l.PersonTyped, tags[l.Charge])); }
        }
        void Fill(Window window, SavedHit[] hits)
        {
            foreach (var hit in hits) { window.Hit(hit.At, hit.Charge is { } tag ? tags[tag] : null); }
        }
        foreach (var (rows, windows) in new[] { (state.Origins, originLaunches), (state.Attempts, launchAttempts) })
        {
            foreach (var row in rows) { var window = new Window(Hour); Fill(window, row.Hits); windows.Add(row.Origin, window); }
        }
        Fill(machineLaunches, state.Machine);
        lastLaunchCheck = state.Last;
    }
}
