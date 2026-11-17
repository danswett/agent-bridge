namespace AgentBridge.Host.Fleet;

internal sealed record VerifiedRecord(FleetMachine Machine, string Epoch, long Seq, DateTimeOffset At);
internal sealed record CachedRecord(FleetMachine Machine, bool Stale, bool Online);

internal sealed class FleetRegistryCache
{
    readonly TimeProvider time;
    readonly string account;
    readonly object gate = new();
    readonly Dictionary<string, VerifiedRecord> records = new(StringComparer.Ordinal);
    readonly HashSet<string> removed = new(StringComparer.Ordinal);
    readonly HashSet<string> received = new(StringComparer.Ordinal);
    bool optedIn;

    public FleetRegistryCache(TimeProvider time, string account)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        this.time = time;
        this.account = account;
    }

    // Called at the send boundary of get/subscribe, never from an incoming frame.
    public void OptIn()
    {
        lock (gate) { optedIn = true; received.Clear(); }
    }

    public bool Accept(VerifiedRecord record, string frameAccount)
    {
        lock (gate)
        {
            var machine = record.Machine;
            if (!optedIn || !string.Equals(frameAccount, account, StringComparison.Ordinal)
                || !machine.Verified || removed.Contains(machine.Id)) { return false; }
            if (records.TryGetValue(machine.Id, out var previous)
                && (previous.Epoch == record.Epoch ? record.Seq <= previous.Seq : record.At <= previous.At)) { return false; }
            records[machine.Id] = record with
            {
                Machine = machine with { Sessions = Array.AsReadOnly(machine.Sessions.ToArray()) },
            };
            received.Add(machine.Id);
            return true;
        }
    }

    public void Remove(string machine)
    {
        lock (gate) { RemoveCore(machine); }
    }

    void RemoveCore(string machine)
    {
        records.Remove(machine);
        received.Remove(machine);
        removed.Add(machine);
    }

    // Complete registry-get only: listed is the union of registry and known IDs.
    public void Reconcile(IReadOnlySet<string> listed)
    {
        lock (gate)
        {
            if (!optedIn) { return; }
            foreach (var id in records.Keys.ToArray())
            {
                if (!listed.Contains(id)) { RemoveCore(id); }
                else if (!received.Contains(id))
                {
                    var record = records[id];
                    records[id] = record with
                    {
                        Machine = record.Machine with { Online = false, Sessions = [], HasRegistryRecord = false },
                    };
                }
            }
            // Removal clears ordering history; only a subsequent listing admits it again.
            removed.ExceptWith(listed);
            received.Clear();
        }
    }

    public void Purge()
    {
        lock (gate) { PurgeCore(time.GetUtcNow()); }
    }

    void PurgeCore(DateTimeOffset now)
    {
        foreach (var id in records.Where(p => now - p.Value.At > TimeSpan.FromHours(24)).Select(p => p.Key).ToArray())
        {
            records.Remove(id);
            received.Remove(id);
        }
    }

    public IReadOnlyList<CachedRecord> Served()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            PurgeCore(now);
            return records.Values.Select(record =>
            {
                var age = now - record.At;
                var online = record.Machine.Online && age >= TimeSpan.FromSeconds(-60) && age <= TimeSpan.FromSeconds(60);
                return new CachedRecord(record.Machine with { Online = online }, !online, online);
            }).ToArray();
        }
    }
}
