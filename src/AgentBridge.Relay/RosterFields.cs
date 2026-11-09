using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Relay;

// The session facts the relay carries for an account's own devices (docs/protocol/roster.md,
// "Session registry", design docs/design/fleet-session-registry.md). A host publishes one
// snapshot of its sessions; the relay keeps the newest per machine in memory only and hands it
// to the same account's devices. Nothing here reaches the stored roster, a log or a metric.
//
// The allow-list is the point. Session names and coarse states are new relay-visible metadata, so
// a record is refused outright when it holds any member not listed here, rather than trimmed:
// a host that sends a prompt, question, path or token is broken and should hear about it.
public static class RosterFields
{
    public const int MaxRecordBytes = 16 * 1024;
    public const int MaxFrameBytes = 24 * 1024;
    public const int MaxSessions = 64;
    public const int MaxNameLength = 64;
    // A host that has not published for this long is shown offline, whatever its socket says:
    // a half-open socket is the case this covers.
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromSeconds(60);

    static readonly HashSet<string> RecordMembers = ["v", "machine", "epoch", "seq", "at", "truncated", "total", "sessions", "sig"];
    static readonly HashSet<string> SessionMembers = ["id", "node", "name", "state", "since"];
    static readonly HashSet<string> States = ["working", "waiting", "idle", "ending"];

    // Null when the record is acceptable, else the reason; epoch, seq and at come back for ordering.
    public static string? Check(JsonElement record, string machine, out string epoch, out long seq, out DateTimeOffset at)
    {
        epoch = "";
        seq = 0;
        at = default;
        if (record.ValueKind != JsonValueKind.Object) { return "bad-record"; }
        var seen = new HashSet<string>();
        foreach (var p in record.EnumerateObject())
        {
            if (!RecordMembers.Contains(p.Name) || !seen.Add(p.Name)) { return "bad-record"; }
        }
        if (!seen.IsSupersetOf(["v", "machine", "epoch", "seq", "at", "truncated", "sessions"])) { return "bad-record"; }
        if (!Int(record.GetProperty("v"), out var v) || v != 1) { return "bad-record"; }
        // A host speaks only for itself.
        if (!Is(record.GetProperty("machine"), JsonValueKind.String) || record.GetProperty("machine").GetString() != machine) { return "bad-record"; }
        if (!Is(record.GetProperty("epoch"), JsonValueKind.String) || record.GetProperty("epoch").GetString() is not { } e || !IsHex16(e)) { return "bad-record"; }
        epoch = e;
        if (!Is(record.GetProperty("seq"), JsonValueKind.Number) || !record.GetProperty("seq").TryGetInt64(out seq) || seq < 1) { return "bad-record"; }
        if (!TryTime(record.GetProperty("at"), out at)) { return "bad-record"; }
        var truncated = record.GetProperty("truncated");
        if (truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { return "bad-record"; }
        if (seen.Contains("total") && (!truncated.GetBoolean() || !Int(record.GetProperty("total"), out var total) || total <= MaxSessions)) { return "bad-record"; }
        if (seen.Contains("sig") && (!Is(record.GetProperty("sig"), JsonValueKind.String) || record.GetProperty("sig").GetString() is not { Length: > 0 and <= 256 } sig || !sig.All(IsBase64Url))) { return "bad-record"; }
        var sessions = record.GetProperty("sessions");
        if (sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() > MaxSessions) { return "bad-record"; }
        foreach (var s in sessions.EnumerateArray())
        {
            if (!SessionOk(s)) { return "bad-record"; }
        }
        return null;
    }

    static bool SessionOk(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) { return false; }
        var seen = new HashSet<string>();
        foreach (var p in s.EnumerateObject())
        {
            if (!SessionMembers.Contains(p.Name) || !seen.Add(p.Name)) { return false; }
        }
        if (!seen.IsSupersetOf(["id", "node", "state", "since"])) { return false; }
        if (!Is(s.GetProperty("id"), JsonValueKind.String) || !IsHex16(s.GetProperty("id").GetString()!)) { return false; }
        if (!Is(s.GetProperty("node"), JsonValueKind.String) || s.GetProperty("node").GetString() is not { } node
            || !node.StartsWith("agent_bridge_", StringComparison.Ordinal) || !IsHex16(node["agent_bridge_".Length..])) { return false; }
        if (!Is(s.GetProperty("state"), JsonValueKind.String) || !States.Contains(s.GetProperty("state").GetString()!)) { return false; }
        if (!TryTime(s.GetProperty("since"), out _)) { return false; }
        // Printable ASCII only: a name is display text from an untrusted source.
        return !seen.Contains("name") || (Is(s.GetProperty("name"), JsonValueKind.String)
            && s.GetProperty("name").GetString() is { Length: > 0 and <= MaxNameLength } name && name.All(c => c is >= ' ' and <= '~'));
    }

    static bool Int(JsonElement e, out int n) { n = 0; return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out n); }
    static bool Is(JsonElement e, JsonValueKind kind) => e.ValueKind == kind;
    static bool IsHex16(string s) => s.Length == 16 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    static bool IsBase64Url(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    static bool TryTime(JsonElement e, out DateTimeOffset t)
    {
        t = default;
        return e.ValueKind == JsonValueKind.String && e.GetString() is { Length: <= 40 } s
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t);
    }
}

// The newest accepted record per machine, in memory. Gone when the host's socket is, and rebuilt
// from its next snapshot, so the relay's restart or a machine's leaving leaves no session behind.
public sealed class SessionRegistry(TimeProvider time)
{
    sealed record Held(string Epoch, long Seq, DateTimeOffset At, DateTimeOffset ReceivedAt, string Json);
    readonly ConcurrentDictionary<(string Account, string Machine), Held> held = new();

    // Null when kept, else why not: too-large, bad-record, or stale (not newer than what is held).
    public string? Put(string account, string machine, JsonElement record)
    {
        var json = record.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > RosterFields.MaxRecordBytes) { return "too-large"; }
        if (RosterFields.Check(record, machine, out var epoch, out var seq, out var at) is { } why) { return why; }
        var key = (account, machine);
        while (true)
        {
            var next = new Held(epoch, seq, at, time.GetUtcNow(), json);
            if (!held.TryGetValue(key, out var old))
            {
                if (held.TryAdd(key, next)) { return null; }
                continue;
            }
            // Within an epoch a record must rise; a new epoch (a restarted host) must be later.
            if (old.Epoch == epoch ? seq <= old.Seq : at <= old.At) { return "stale"; }
            if (held.TryUpdate(key, next, old)) { return null; }
        }
    }

    public void Drop(string account, string machine) => held.TryRemove((account, machine), out _);

    // The account's held machines. Fresh is false once a host has gone a minute without a new
    // snapshot: its sessions are then withheld and it reads as offline, whatever its socket says.
    public List<(string Machine, string Json, DateTimeOffset LastSeen, bool Fresh)> Read(string account)
    {
        var now = time.GetUtcNow();
        return [.. held.Where(h => h.Key.Account == account)
            .Select(h => (h.Key.Machine, h.Value.Json, h.Value.ReceivedAt, now - h.Value.ReceivedAt <= RosterFields.HeartbeatWindow))];
    }
}
