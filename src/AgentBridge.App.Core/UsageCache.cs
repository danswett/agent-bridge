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
                entries = [.. (s.Readings ?? []).Where(e => e is not null).Take(MaxAllowances)];
                saved = Serialize();
            }
        }
        // A nicety: usage still shows what the machines online report without it.
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
    }

    // live, every machine's readings now, with what is kept for the account. account: the one this
    // machine's host is signed in to, "" when it is signed out (nothing kept is shown), null while
    // that is not known yet (what is kept is shown, and nothing is written). Returns the merge.
    public UsageReport Merge(UsageReport live, string? account)
    {
        if (account is "") { return live; }
        var oldest = time.GetUtcNow() - Kept;
        if (account is not null && account != this.account)
        {
            this.account = account;
            entries = [];
        }
        else if (account is null && this.account is null) { return live; }
        // Live first: of two readings taken at the same moment, the one a machine is offering now.
        var merged = UsageMerge.Combine([live, new UsageReport(entries)]).Where(e => e.MeasuredAt >= oldest)
            .OrderByDescending(e => e.MeasuredAt).Take(MaxAllowances).ToList();
        if (account is null) { return new UsageReport(merged); }
        entries = merged;
        Save();
        return new UsageReport(merged);
    }

    string Serialize() => JsonSerializer.Serialize(new Saved(1, account, entries), ProtocolJson.Options);

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
