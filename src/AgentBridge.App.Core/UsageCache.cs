using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The last reading of each allowance this app has seen, kept in its local state between runs
// (#172), as the PWA keeps it in IndexedDB: a reading outlives the machine that took it going
// offline, as agent-ha-bridge's retained sensors did in Home Assistant. One account's only: a
// file kept for another is ignored and replaced. Readings older than 14 days go. A device that
// has never seen a reading of an allowance shows nothing for it.
public sealed class UsageCache
{
    public static readonly TimeSpan Kept = TimeSpan.FromDays(14);
    // Allowances kept at most, as a host keeps them (PeerUsage); past this the one read longest ago goes.
    public const int MaxAllowances = 32;

    readonly string? path;
    readonly TimeProvider time;
    string? account;
    List<AgentUsage> entries = [];
    string saved = "";
    readonly HashSet<string> forgotten = new(StringComparer.Ordinal);

    sealed record Saved(int V, string? Account, List<AgentUsage>? Readings);

    public UsageCache(string? path, TimeProvider? time = null)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
        try
        {
            if (path is not null && File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), ProtocolJson.Options) is { Account: { Length: > 0 } a } s)
            {
                account = a;
                entries = [.. (s.Readings ?? []).Where(e => e is not null).Take(2 * MaxAllowances)];
                saved = Serialize();
            }
        }
        // A nicety: usage still shows what the machines online report without it.
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
    }

    // live, every machine's readings now, with what is kept for the account. account: the one this
    // machine's host is signed in to, "" when it is signed out (nothing kept is shown), null while
    // that is not known yet (what is kept is shown, and nothing is written). members: the account's
    // machines as the relay's roster has them (online or not), when it keeps one and it is this
    // account's; a reading by any other was taken by a machine forgotten, perhaps while this app
    // was closed. Returns each allowance's reading and any newer failure, not yet combined.
    public UsageReport Merge(UsageReport live, string? account, IReadOnlySet<string>? members = null)
    {
        var oldest = time.GetUtcNow() - Kept;
        // Whatever passes on a forgotten machine's readings (another host that has not heard yet)
        // brings none of them back; and none past their 14 days takes part. Both are left out before
        // anything is merged: chosen first and dropped after, either would hide one that stands.
        bool Stands(AgentUsage a) => a.MeasuredAt >= oldest
            && (a.ReadBy is not { } by || (!forgotten.Contains(by.Id) && (members is null || members.Contains(by.Id))));
        live = new UsageReport([.. live.Agents.Where(Stands)]);
        // Signed out: what was kept is the account's, and goes, file and all.
        if (account is "")
        {
            if (this.account is not null || entries.Count > 0) { Clear(); }
            return live;
        }
        if (account is not null && account != this.account)
        {
            this.account = account;
            entries = [];
            forgotten.Clear();
        }
        else if (account is null && this.account is null) { return live; }
        // Live first: of two readings taken at the same moment, the one a machine is offering now.
        // A reading and a newer failure are kept apart, each with its own time; as many allowances
        // as a host keeps, the one read longest ago going first.
        var merged = UsageMerge.Latest([live, new UsageReport([.. entries.Where(Stands)])])
            .GroupBy(UsageShare.Key).OrderByDescending(g => g.Max(e => e.MeasuredAt)).Take(MaxAllowances)
            .SelectMany(g => g).ToList();
        if (account is null) { return new UsageReport(merged); }
        entries = merged;
        Save();
        return new UsageReport(merged);
    }

    string Serialize() => JsonSerializer.Serialize(new Saved(1, account, entries), ProtocolJson.Options);

    // A machine forgotten from the account's roster (#50): what it read goes, kept or passed on,
    // until it is back. Present: machines online now, which are the account's again.
    public void Forget(string machineId)
    {
        forgotten.Add(machineId);
        if (entries.RemoveAll(e => e.ReadBy?.Id == machineId) > 0 && account is not null) { Save(); }
    }

    public void Present(IEnumerable<string> machineIds) => forgotten.ExceptWith(machineIds);

    void Clear()
    {
        account = null;
        entries = [];
        forgotten.Clear();
        saved = "";
        if (path is null) { return; }
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void Save()
    {
        if (path is null) { return; }
        var text = Serialize();
        // Every update from every machine passes through here; most change nothing kept.
        if (text == saved) { return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            saved = text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
