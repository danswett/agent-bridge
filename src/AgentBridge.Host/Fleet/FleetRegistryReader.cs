using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Host.Fleet;

// One machine as the reader holds it. Live needs the relay's online flag AND a record whose `at` is
// within 60 s of the reader's clock; anything else is a stale cached copy (or none).
public sealed record RegistryMachine(string Id, string? Name, bool Online, bool Live, bool Verified, DateTimeOffset? LastSeen, JsonElement? Record);

// The host as a registry reader (docs/design/fleet-session-registry.md, slice RC; docs/protocol/roster.md,
// "Freshness and replay"). Pure state: frames in, a snapshot out, no sockets. `verify` is the seam for
// hostSign (#1776): without it a record is never Verified; with it, a record it refuses is not kept.
public sealed class FleetRegistryReader(TimeProvider clock, Func<string, JsonElement, bool>? verify = null)
{
    public static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CacheAge = TimeSpan.FromHours(24);

    sealed record Held(string Epoch, long Seq, DateTimeOffset At, JsonElement Record, bool Verified, long Seen);
    sealed class Slot { public string? Name; public bool Online; public DateTimeOffset? LastSeen; public bool Listed; public Held? Held; }

    readonly Dictionary<string, Slot> machines = new(StringComparer.Ordinal);
    readonly object gate = new();

    // A registry-get reply: one frame per machine holding a record, then `known`. A machine the reply
    // lists in neither is gone from the account, so it is dropped.
    public void ApplyGet(IEnumerable<JsonElement> frames)
    {
        lock (gate)
        {
            var list = frames.ToList();
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in list.Where(f => Id(f) is not null))
            {
                var id = Id(f)!;
                listed.Add(id);
                var e = Entry(id);
                e.Listed = true;
                e.Name = Text(f, "name") ?? e.Name;
                e.LastSeen = Time(f, "lastSeen") ?? e.LastSeen;
                e.Online = Flag(f, "online");
                if (e.Online && f.TryGetProperty("record", out var rec)) { Accept(id, e, rec); }
            }
            // A machine's own frame takes precedence over its `known` metadata.
            foreach (var k in list.Where(f => f.TryGetProperty("known", out var a) && a.ValueKind == JsonValueKind.Array).SelectMany(f => f.GetProperty("known").EnumerateArray()))
            {
                if (Id(k) is not { } id || !listed.Add(id)) { continue; }
                var e = Entry(id);
                e.Listed = true;
                e.Name = Text(k, "name") ?? e.Name;
                e.LastSeen = Time(k, "lastSeen") ?? e.LastSeen;
                e.Online = Flag(k, "online");
            }
            foreach (var id in machines.Keys.Where(id => !listed.Contains(id)).ToList()) { machines.Remove(id); }
        }
    }

    // One registry-changed frame: a record, offline, or removed.
    public void ApplyChanged(JsonElement frame)
    {
        if (Id(frame) is not { } id) { return; }
        lock (gate)
        {
            if (Flag(frame, "removed")) { machines.Remove(id); return; }
            var known = machines.TryGetValue(id, out var held);
            var e = held ?? new Slot();
            if (!Flag(frame, "online")) { e.Online = false; return; }
            if (frame.TryGetProperty("record", out var rec) && Accept(id, e, rec))
            {
                e.Online = true;
                if (!known) { machines[id] = e; }
            }
        }
    }

    public IReadOnlyList<RegistryMachine> Snapshot()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var (id, e) in machines.ToList())
            {
                if (e.Held is { } h && clock.GetElapsedTime(h.Seen) >= CacheAge) { e.Held = null; }
                if (e.Held is null && !e.Listed) { machines.Remove(id); }
            }
            return [.. machines.Select(m => new RegistryMachine(m.Key, m.Value.Name, m.Value.Online,
                m.Value.Online && m.Value.Held is { } h && Within(h.At, now), m.Value.Held?.Verified ?? false, m.Value.LastSeen, m.Value.Held?.Record))];
        }
    }

    Slot Entry(string id) => machines.TryGetValue(id, out var e) ? e : machines[id] = new Slot();

    static bool Within(DateTimeOffset at, DateTimeOffset now) => (at - now).Duration() <= LiveWindow;

    // Schema of what the order rule needs, then the 60 s rule, then order, then the verifier.
    // A refused record leaves the last accepted one in place, stale in time.
    bool Accept(string id, Slot e, JsonElement rec)
    {
        if (rec.ValueKind != JsonValueKind.Object || Text(rec, "machine") != id || Text(rec, "epoch") is not { Length: 16 } epoch
            || !epoch.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            || !rec.TryGetProperty("seq", out var s) || !s.TryGetInt64(out var seq) || seq < 1 || Time(rec, "at") is not { } at
            || !Within(at, clock.GetUtcNow())) { return false; }
        // Equal is accepted: the relay serves the same snapshot until the host's next heartbeat.
        if (e.Held is { } old && (old.Epoch == epoch ? seq < old.Seq : at <= old.At)) { return false; }
        if (verify is not null && !verify(id, rec)) { return false; }
        e.Held = new Held(epoch, seq, at, rec.Clone(), verify is not null, clock.GetTimestamp());
        return true;
    }

    static string? Id(JsonElement f) => Text(f, "machine") is { Length: > 0 and <= 64 } id ? id : null;
    static string? Text(JsonElement f, string name) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Flag(JsonElement f, string name) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static DateTimeOffset? Time(JsonElement f, string name) =>
        Text(f, name) is { Length: <= 40 } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
