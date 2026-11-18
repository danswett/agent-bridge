using System.Text;

namespace AgentBridge.App.Core;

public sealed record SessionGroup(string Id, string Name);
public sealed record GroupRegister(string? Value, long Counter, string DeviceTag, bool Tombstone = false);

// Local registers already have the roaming schema; collapse is deliberately kept elsewhere.
public sealed class SessionGroups
{
    readonly Dictionary<string, GroupRegister> groups;
    readonly Dictionary<string, GroupRegister> membership;
    readonly Dictionary<string, DateTimeOffset> listed;
    readonly Dictionary<string, DateTimeOffset> savedListed;
    readonly string deviceTag;
    readonly TimeProvider time;
    readonly Action save;
    readonly Func<IReadOnlyList<string>> unpinned;

    internal SessionGroups(Dictionary<string, GroupRegister> groups, Dictionary<string, GroupRegister> membership,
        Dictionary<string, DateTimeOffset> listed, string deviceTag, TimeProvider time, Action save, Func<IReadOnlyList<string>> unpinned)
    {
        this.groups = groups;
        this.membership = membership;
        this.listed = listed;
        savedListed = new(listed, StringComparer.Ordinal);
        this.deviceTag = deviceTag;
        this.time = time;
        this.save = save;
        this.unpinned = unpinned;
        foreach (var map in new[] { groups, membership })
        {
            foreach (var (key, r) in map.ToArray())
            {
                if (r is null || r.Counter < 0 || !Guid.TryParseExact(r.DeviceTag, "N", out _)) { map.Remove(key); }
            }
        }
    }

    public IReadOnlyList<SessionGroup> Items => groups.Where(p => !p.Value.Tombstone && !string.IsNullOrWhiteSpace(p.Value.Value))
        .Select(p => new SessionGroup(p.Key, p.Value.Value!)).ToList();

    public string? GroupOf(string key) => membership.TryGetValue(key, out var r) && !r.Tombstone
        && r.Value is { } id && groups.TryGetValue(id, out var g) && !g.Tombstone && !string.IsNullOrWhiteSpace(g.Value) ? id : null;

    public IReadOnlyDictionary<string, int> Counts() => Items.ToDictionary(g => g.Id, g => unpinned().Count(k => GroupOf(k) == g.Id));

    public string ValidateName(string? name, string? except = null)
    {
        var clean = name?.Trim() ?? "";
        if (clean.Length == 0 || clean.EnumerateRunes().Count() > 40
            || new[] { "Pinned", "Sessions", "Ended" }.Contains(clean, StringComparer.OrdinalIgnoreCase)
            || Items.Any(g => g.Id != except && string.Equals(g.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Group names must be unique, not reserved, and 1 to 40 characters.", nameof(name));
        }
        return clean;
    }

    void Require(string id)
    {
        if (!Items.Any(g => g.Id == id)) { throw new ArgumentOutOfRangeException(nameof(id), "Group does not exist."); }
    }

    void Write(Dictionary<string, GroupRegister> map, string key, string? value, bool tombstone = false)
    {
        var counter = checked(groups.Values.Concat(membership.Values).Select(r => r.Counter).DefaultIfEmpty().Max() + 1);
        map[key] = new(value, counter, deviceTag, tombstone);
    }

    public string Create(string name)
    {
        var clean = ValidateName(name);
        if (Items.Count >= 32) { throw new InvalidOperationException("At most 32 groups can be kept."); }
        var id = Guid.NewGuid().ToString();
        Write(groups, id, clean);
        save();
        return id;
    }

    public void Rename(string id, string name)
    {
        Require(id);
        Write(groups, id, ValidateName(name, id));
        save();
    }

    public void SetGroup(string key, string? id)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (id is not null) { Require(id); }
        if (GroupOf(key) == id) { return; }
        Write(membership, key, id);
        if (id is null) { listed.Remove(key); } else { listed[key] = time.GetUtcNow(); }
        save();
    }

    public void Delete(string id)
    {
        Require(id);
        foreach (var key in membership.Keys.Where(k => GroupOf(k) == id).ToArray())
        {
            Write(membership, key, null);
            listed.Remove(key);
        }
        Write(groups, id, null, tombstone: true);
        save();
    }

    internal bool ListingsDirty { get; private set; }

    internal void Saved()
    {
        savedListed.Clear();
        foreach (var (key, at) in listed) { savedListed[key] = at; }
        ListingsDirty = false;
    }

    internal bool Remember(IReadOnlyList<OrderEntry> entries, Func<string, bool>? settled)
    {
        var live = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var now = time.GetUtcNow();
        var changed = false;
        foreach (var key in membership.Keys.ToArray())
        {
            if (GroupOf(key) is null) { continue; }
            var stamped = listed.TryGetValue(key, out var last);
            if (live.Contains(key) || !stamped)
            {
                if (!stamped || now != last)
                {
                    listed[key] = now;
                    ListingsDirty = true;
                }
                // Batch disk writes, never the last-seen time used for expiry.
                if (!savedListed.TryGetValue(key, out var saved) || now - saved >= TimeSpan.FromDays(1)) { changed = true; }
            }
            else if (now - last > TimeSpan.FromDays(30) && settled?.Invoke(key.Split('\n', 2)[0]) != false)
            {
                Write(membership, key, null);
                listed.Remove(key);
                changed = true;
            }
        }
        return changed;
    }
}
