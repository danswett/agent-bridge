using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.App.Core;

public sealed record SessionGroup(string Id, string Name);
// One register, written exactly as the PWA's order record writes it (pwa/js/session-order.js): { value, counter, deviceTag }, or
// { tombstone: true, counter, deviceTag } with no value; a removed member is a null value, not a tombstone.
[JsonConverter(typeof(GroupRegisterJson))]
public sealed record GroupRegister(string? Value, long Counter, string DeviceTag, bool Tombstone = false);

// The two roaming maps, kept under "groupRecords" like the PWA's (group name by id, group id by session key).
public sealed class GroupRecordSet
{
    public Dictionary<string, GroupRegister> Groups { get; set; } = [];
    public Dictionary<string, GroupRegister> Membership { get; set; } = [];
}

sealed class GroupRegisterJson : JsonConverter<GroupRegister>
{
    // A malformed register reads as null and is dropped, as the PWA drops it, instead of failing the whole file.
    public override GroupRegister? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var o = doc.RootElement;
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("counter", out var c) || !c.TryGetInt64(out var counter)
            || !o.TryGetProperty("deviceTag", out var d) || d.ValueKind != JsonValueKind.String) { return null; }
        var tomb = o.TryGetProperty("tombstone", out var t) && t.ValueKind == JsonValueKind.True;
        var v = o.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new(tomb ? null : v, counter, d.GetString()!, tomb);
    }

    public override void Write(Utf8JsonWriter w, GroupRegister r, JsonSerializerOptions options)
    {
        w.WriteStartObject();
        if (r.Tombstone) { w.WriteBoolean("tombstone", true); } else if (r.Value is null) { w.WriteNull("value"); } else { w.WriteString("value", r.Value); }
        w.WriteNumber("counter", r.Counter);
        w.WriteString("deviceTag", r.DeviceTag);
        w.WriteEndObject();
    }
}

// Local registers already have the roaming schema; collapse is deliberately kept elsewhere.
public sealed class SessionGroups
{
    readonly Dictionary<string, GroupRegister> groups;
    readonly Dictionary<string, GroupRegister> membership;
    readonly Dictionary<string, DateTimeOffset> listed;
    readonly string deviceTag;
    readonly TimeProvider time;
    readonly Action save;
    readonly Action<string> prepend;
    readonly Action<IReadOnlyCollection<string>> lift;
    readonly Func<IReadOnlyList<string>> unpinned;
    DateTimeOffset listingSavedAt;
    internal bool ListingDirty { get; private set; }

    internal SessionGroups(Dictionary<string, GroupRegister> groups, Dictionary<string, GroupRegister> membership,
        Dictionary<string, DateTimeOffset> listed, string deviceTag, TimeProvider time, Action save, Action<string> prepend,
        Action<IReadOnlyCollection<string>> lift, Func<IReadOnlyList<string>> unpinned)
    {
        this.groups = groups;
        this.membership = membership;
        this.listed = listed;
        this.deviceTag = deviceTag;
        this.time = time;
        this.save = save;
        this.prepend = prepend;
        this.lift = lift;
        this.unpinned = unpinned;
        listingSavedAt = time.GetUtcNow();
        foreach (var map in new[] { groups, membership })
        {
            foreach (var (key, r) in map.ToArray())
            {
                // as the PWA reads them: a whole non-negative counter, any hex device tag, and a live group needs a usable name
                var bad = r is null || r.Counter < 0 || r.DeviceTag.Length == 0 || r.DeviceTag.Length % 2 != 0 || !r.DeviceTag.All(char.IsAsciiHexDigit)
                    || (map == groups && !r.Tombstone && (string.IsNullOrWhiteSpace(r.Value) || r.Value.EnumerateRunes().Count() > 40));
                if (bad) { map.Remove(key); } else if (r!.DeviceTag != r.DeviceTag.ToLowerInvariant()) { map[key] = r with { DeviceTag = r.DeviceTag.ToLowerInvariant() }; }
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
        prepend(key);
        save();
    }

    public void Delete(string id)
    {
        Require(id);
        var members = membership.Keys.Where(k => GroupOf(k) == id).ToArray();
        lift(members);  // its unpinned members return together to the top of Sessions, in their order (the PWA does the same)
        foreach (var key in members)
        {
            Write(membership, key, null);
            listed.Remove(key);
        }
        Write(groups, id, null, tombstone: true);
        save();
    }

    internal void Saved() { ListingDirty = false; listingSavedAt = time.GetUtcNow(); }

    // Keep exact listing times in memory; batch automatic saves, never the retention clock.
    internal bool Remember(IReadOnlyList<OrderEntry> entries)
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
                ListingDirty |= !stamped || last != now;
                listed[key] = now;
                changed |= !stamped;
            }
            else if (now - last > TimeSpan.FromDays(30))
            {
                Write(membership, key, null);
                listed.Remove(key);
                changed = true;
            }
        }
        return changed || ListingDirty && now - listingSavedAt >= TimeSpan.FromDays(1);
    }
}
