namespace AgentBridge.Host;

public sealed partial class AgentLimits
{
    internal sealed record SavedHit(DateTimeOffset At, Guid? Charge);
    internal sealed record SavedWindow(string Origin, SavedHit[] Hits);
    internal sealed record SavedLive(string? Session, DateTimeOffset? IdleSince, bool PersonTyped, Guid Charge);
    internal sealed record SavedRequest(string Origin, string Request, string Hash, DateTimeOffset At, SavedLive? Live);
    internal sealed record SavedLaunchState(int V, SavedRequest[] Requests, SavedWindow[] Origins,
        SavedWindow[] Attempts, SavedHit[] Machine, DateTimeOffset Last);

    internal SavedLaunchState SnapshotLaunches()
    {
        lock (gate)
        {
            var now = LaunchTime();
            var tags = new Dictionary<object, Guid>(ReferenceEqualityComparer.Instance);
            foreach (var live in liveLaunches.Values) { tags.Add(live.Charge!, Guid.NewGuid()); }
            var requests = launchRequests.Select(p =>
            {
                var live = liveLaunches.GetValueOrDefault(p.Key);
                return new SavedRequest(p.Key.Origin, p.Key.Request, p.Value.Hash, p.Value.At,
                    live is null ? null : new(live.Session, live.IdleSince, live.PersonTyped, tags[live.Charge!]));
            }).ToArray();
            SavedWindow[] Windows(Dictionary<string, Window> windows) =>
                windows.Select(p => new SavedWindow(p.Key, p.Value.Snapshot(now, tags))).ToArray();
            return new(1, requests, Windows(originLaunches), Windows(launchAttempts), machineLaunches.Snapshot(now, tags), now);
        }
    }

    internal void RestoreLaunches(SavedLaunchState state)
    {
        static bool Text(string? text) => text is { Length: > 0 and <= 256 };
        if (state.V != 1 || state.Last == default || state.Requests is null || state.Origins is null
            || state.Attempts is null || state.Machine is null || state.Requests.Length > MaxKeys
            || state.Requests.Any(r => r is null || !Text(r.Origin) || !Text(r.Request) || !Text(r.Hash) || r.At == default
                || r.Live is { } l && (l.Charge == Guid.Empty || l.Session is not null && !Text(l.Session)))
            || state.Requests.Select(r => (r.Origin, r.Request)).Distinct().Count() != state.Requests.Length)
        { throw new InvalidDataException("Invalid launch request ledger."); }
        var live = state.Requests.Where(r => r.Live is not null).ToArray();
        if (live.Select(r => r.Live!.Charge).Distinct().Count() != live.Length
            || live.Where(r => r.Live!.Session is not null).GroupBy(r => r.Live!.Session).Any(g => g.Count() > 1))
        { throw new InvalidDataException("Invalid live launch ledger."); }
        var tags = live.ToDictionary(r => r.Live!.Charge, _ => new object());
        bool Hits(SavedHit[]? hits) => hits is not null && hits.Length <= MaxKeys
            && hits.All(h => h is not null && h.At != default && (h.Charge is null || tags.ContainsKey(h.Charge.Value)))
            && hits.Select(h => h.At).SequenceEqual(hits.Select(h => h.At).Order());
        foreach (var windows in new[] { state.Origins, state.Attempts })
        {
            if (windows.Length > MaxKeys || windows.Any(w => w is null || !Text(w.Origin) || !Hits(w.Hits))
                || windows.Select(w => w.Origin).Distinct().Count() != windows.Length)
            { throw new InvalidDataException("Invalid hourly launch ledger."); }
        }
        if (!Hits(state.Machine)) { throw new InvalidDataException("Invalid machine launch ledger."); }
        foreach (var row in state.Requests)
        {
            var key = (row.Origin, row.Request);
            launchRequests.Add(key, (row.Hash, row.At));
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
