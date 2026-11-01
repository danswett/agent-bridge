using System.Text.Json;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// The usage readings this host has from the account's other machines (#172), so it can serve an
// account's allowance after the machine that read it has gone offline: agent-ha-bridge's usage
// sensors were retained in MQTT, and Home Assistant went on showing a machine's last reading.
//
// One reading and one failure per allowance (agent and account), as clients merge them: the
// newest of each kind is kept, so a failure never replaces a reading, and anything not strictly
// newer than what is held is ignored. That is what keeps two hosts passing readings back and
// forth from going round: each copy carries the machine that took it (readBy), a host takes none
// of its own back, and the newest reading anywhere is where they all settle.
//
// Kept in the profile's own private folder (usage/peers.json), for one account: readings taken for
// another account are never served to this one. Readings older than 14 days are dropped.
public sealed class PeerUsage
{
    public const string FileName = "peers.json";
    public static readonly TimeSpan Kept = TimeSpan.FromDays(14);
    // A reading stamped further ahead of this clock than this is refused: kept, it would outrank
    // every true reading of that allowance for as long as the stamp is in the future.
    public static readonly TimeSpan Ahead = TimeSpan.FromMinutes(10);
    // Allowances held at most; past this, the one read longest ago goes.
    public const int MaxAllowances = 32;

    readonly string? folder;
    readonly string me;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly object gate = new();
    readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    string? account;
    long version;

    sealed class Slot
    {
        public AgentUsage? Reading;
        public AgentUsage? Failure;
        public DateTimeOffset Newest => new[] { Reading?.MeasuredAt, Failure?.MeasuredAt }.Max() ?? DateTimeOffset.MinValue;
    }

    sealed record Saved(int V, string? Account, List<AgentUsage>? Readings);

    PeerUsage(string? folder, string me, string? account, TimeProvider? time, Action<string>? log)
    {
        this.folder = folder;
        this.me = me;
        this.account = account;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
    }

    // Raised (outside the lock) whenever what Entries returns has changed.
    public event Action? Changed;

    // Goes up by one with each change.
    public long Version { get { lock (gate) { return version; } } }

    // folder: where to keep them (null: memory only). me: this machine's device id, whose readings
    // are its own and never taken from another. account: the account signed in now, if known.
    public static PeerUsage Load(string? folder, string me, string? account, TimeProvider? time = null, Action<string>? log = null)
    {
        var store = new PeerUsage(folder, me, account, time, log);
        if (folder is null) { return store; }
        string path;
        try { path = Path.Combine(PrivateFolder.Ensure(folder, "the usage folder", "readings from other machines are not kept across restarts"), FileName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            store.log($"usage: {e.Message}");
            return store;
        }
        if (!File.Exists(path)) { return store; }
        Saved? saved = null;
        try { saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), ProtocolJson.Options); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            store.log($"usage: other machines' readings were unreadable ({e.GetType().Name}); starting with none");
        }
        if (saved is null) { return store; }
        if (saved.Account is null || saved.Account != account)
        {
            store.log("usage: other machines' readings kept here are another account's; ignoring them");
            return store;
        }
        lock (store.gate)
        {
            foreach (var a in saved.Readings ?? []) { if (a?.ReadBy is { } by) { store.TakeLocked(a, by); } }
        }
        return store;
    }

    // The account the host is signed in as now. Readings kept for any other go.
    public void Bind(string accountId)
    {
        lock (gate)
        {
            if (account == accountId) { return; }
            var had = slots.Count > 0;
            slots.Clear();
            account = accountId;
            if (!had) { SaveLocked(); return; }
            version++;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    // A peer's usage.get, as it answered: its own readings (taken as from), and any it passed on
    // from other machines (each with its own readBy). forAccount: the account the asking connection
    // was signed in as; an answer that arrives after the host changed account is not taken. Returns
    // how many it took.
    public int Take(IEnumerable<AgentUsage> agents, UsageReader from, string? forAccount = null)
    {
        int took = 0;
        lock (gate)
        {
            if (account is null || (forAccount is not null && forAccount != account)) { return 0; }
            foreach (var a in agents.Take(UsageProtocol.MaxSharedAgents))
            {
                if (a is not null && TakeLocked(a, a.ReadBy ?? from)) { took++; }
            }
            if (took == 0) { return 0; }
            Cap();
            version++;
            SaveLocked();
        }
        Changed?.Invoke();
        return took;
    }

    // Readings that have passed 14 days, gone for good and announced, so a subscriber that has
    // nothing else changing hears they have. Returns how many went.
    public int Prune()
    {
        int dropped = 0;
        var oldest = time.GetUtcNow() - Kept;
        lock (gate)
        {
            foreach (var (key, slot) in slots.ToList())
            {
                if (slot.Reading is { } r && r.MeasuredAt < oldest) { slot.Reading = null; dropped++; }
                if (slot.Failure is { } f && f.MeasuredAt < oldest) { slot.Failure = null; dropped++; }
                if (slot.Reading is null && slot.Failure is null) { slots.Remove(key); }
            }
            if (dropped == 0) { return 0; }
            version++;
            SaveLocked();
        }
        Changed?.Invoke();
        return dropped;
    }

    bool TakeLocked(AgentUsage a, UsageReader by)
    {
        // Fitted as any reading is: a peer's error is passed on as it is, bounded, never expanded.
        // Its place among the further accounts was that machine's; here it is just its account.
        var fit = UsageProtocol.FitAgent(a with { ReadBy = by, Slot = null });
        if (fit.ReadBy is not { } reader || reader.Id == me || fit.Agent == AgentKind.Unknown) { return false; }
        var now = time.GetUtcNow();
        if (fit.MeasuredAt > now + Ahead || fit.MeasuredAt < now - Kept) { return false; }
        var key = UsageShare.Key(fit);
        if (!slots.TryGetValue(key, out var slot)) { slots[key] = slot = new Slot(); }
        if (fit.Windows.Count > 0)
        {
            if (slot.Reading is { } r && r.MeasuredAt >= fit.MeasuredAt) { return false; }
            slot.Reading = fit;
        }
        else
        {
            if (slot.Failure is { } f && f.MeasuredAt >= fit.MeasuredAt) { return false; }
            slot.Failure = fit;
        }
        return true;
    }

    void Cap()
    {
        while (slots.Count > MaxAllowances) { slots.Remove(slots.MinBy(s => s.Value.Newest).Key); }
    }

    // Readings taken by machines that are no longer the account's (forgotten from the roster):
    // keep says which machines still are. Returns how many allowances lost a reading.
    public int Forget(Func<string, bool> keep)
    {
        int dropped = 0;
        lock (gate)
        {
            foreach (var (key, slot) in slots.ToList())
            {
                var before = (slot.Reading, slot.Failure);
                if (slot.Reading?.ReadBy is { } r && !keep(r.Id)) { slot.Reading = null; }
                if (slot.Failure?.ReadBy is { } f && !keep(f.Id)) { slot.Failure = null; }
                if (before != (slot.Reading, slot.Failure)) { dropped++; }
                if (slot.Reading is null && slot.Failure is null) { slots.Remove(key); }
            }
            if (dropped == 0) { return 0; }
            version++;
            SaveLocked();
        }
        Changed?.Invoke();
        return dropped;
    }

    // Each allowance's reading, and its failure where that is newer, as separate entries: a client
    // merges them as it merges any two machines' (the failure lends the reading its error), and a
    // host passed them keeps each with its own time, so a newer failure reaches it even where it
    // already has the reading. None older than 14 days.
    public IReadOnlyList<AgentUsage> Entries
    {
        get
        {
            var oldest = time.GetUtcNow() - Kept;
            lock (gate)
            {
                var list = new List<AgentUsage>();
                foreach (var slot in slots.Values)
                {
                    var r = slot.Reading is { } x && x.MeasuredAt >= oldest ? x : null;
                    var f = slot.Failure is { } y && y.MeasuredAt >= oldest ? y : null;
                    if (r is not null) { list.Add(r); }
                    if (f is not null && (r is null || f.MeasuredAt > r.MeasuredAt)) { list.Add(f); }
                }
                return list;
            }
        }
    }

    void SaveLocked()
    {
        if (folder is null) { return; }
        try
        {
            var dir = PrivateFolder.Ensure(folder, "the usage folder", "readings from other machines are not kept across restarts");
            var oldest = time.GetUtcNow() - Kept;
            var all = slots.Values.SelectMany(s => new[] { s.Reading, s.Failure }).OfType<AgentUsage>().Where(a => a.MeasuredAt >= oldest).ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(1, account, all), ProtocolJson.Options);
            // Written whole and moved into place, so a crash mid-write never leaves half a file. New,
            // in a folder only this user can open: owner-only wherever it is written.
            var temp = Path.Combine(dir, FileName + ".tmp");
            File.Delete(temp);
            PrivateFolder.WriteNew(temp, bytes);
            File.Move(temp, Path.Combine(dir, FileName), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"usage: could not keep other machines' readings: {e.Message}");
        }
    }
}
