using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.Host.Fleet;

// The caller supplies the locally pinned reader's state, never a roster inferred from registry frames.
internal sealed class FleetRecordVerifier(Func<RosterVerifier?> pinnedRoster, TimeProvider time)
{
    readonly object gate = new();
    readonly Dictionary<(string Account, string Generation, string Add), (string Epoch, long Seq, DateTimeOffset AtHighWater, byte[] RecordHash)> accepted = [];

    public bool Verify(string machine, JsonElement record, string? account = null)
    {
        lock (gate)
        {
            var roster = pinnedRoster();
            if (roster is not null && account is not null && roster.Account != account)
            { throw new InvalidOperationException("refused: the pinned registry roster belongs to another account"); }
            if (roster is null || !roster.Serving.TryGetValue(machine, out var add)
                || roster.Graph[add].Body.Device is not { Kind: "host", HostSign: { } key }) { return false; }
            try
            {
                if (!Shape(record, machine) || !FleetRecordSigning.Verify(record, key)) { return false; }
                var at = ReadTime(record.GetProperty("at"));
                if ((time.GetUtcNow() - at).Duration() > TimeSpan.FromSeconds(60)) { return false; }
                var epoch = record.GetProperty("epoch").GetString()!;
                var seq = record.GetProperty("seq").GetInt64();
                var identity = (roster.Account, roster.Generation, Convert.ToHexString(roster.Graph[add].Hash));
                var recordHash = SHA256.HashData(FleetRecordSigning.CanonicalBytes(record));
                var atHighWater = at;
                if (accepted.TryGetValue(identity, out var previous))
                {
                    if (epoch == previous.Epoch && seq == previous.Seq && recordHash.AsSpan().SequenceEqual(previous.RecordHash)) { return true; }
                    if (epoch == previous.Epoch ? seq <= previous.Seq : at <= previous.AtHighWater) { return false; }
                    if (previous.AtHighWater > at) { atHighWater = previous.AtHighWater; }
                }
                accepted[identity] = (epoch, seq, atHighWater, recordHash);
                return true;
            }
            catch (InvalidOperationException) { return false; }
            catch (FormatException) { return false; }
        }
    }

    static bool Shape(JsonElement record, string machine)
    {
        if (!Fields(record, ["v", "machine", "epoch", "seq", "at", "truncated", "total", "sessions", "sig"],
                ["v", "machine", "epoch", "seq", "at", "truncated", "sessions", "sig"])
            || Encoding.UTF8.GetByteCount(record.GetRawText()) > 16 * 1024
            || record.GetProperty("v").GetInt32() != 1 || record.GetProperty("machine").GetString() != machine
            || !Hex(record.GetProperty("epoch").GetString()) || record.GetProperty("seq").GetInt64() < 1
            || !Timestamp(record.GetProperty("at")) || record.GetProperty("truncated").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || record.GetProperty("sig").GetString() is not { Length: > 0 and <= 256 }) { return false; }
        if (record.TryGetProperty("total", out var total)
            && (!record.GetProperty("truncated").GetBoolean() || total.GetInt32() <= 64)) { return false; }
        var sessions = record.GetProperty("sessions");
        if (sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() > 64) { return false; }
        foreach (var session in sessions.EnumerateArray())
        {
            if (!Fields(session, ["id", "node", "name", "state", "since"], ["id", "node", "state", "since"])
                || !Hex(session.GetProperty("id").GetString())
                || session.GetProperty("node").GetString() is not { } node
                || !node.StartsWith("agent_bridge_", StringComparison.Ordinal) || !Hex(node["agent_bridge_".Length..])
                || session.GetProperty("state").GetString() is not ("idle" or "waiting" or "working" or "ending")
                || !Timestamp(session.GetProperty("since"))
                || (session.TryGetProperty("name", out var name) && (name.GetString() is not { Length: > 0 and <= 64 } text
                    || !text.All(c => c is >= ' ' and <= '~')))) { return false; }
        }
        return true;
    }

    static bool Fields(JsonElement value, string[] allowed, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) { return false; }
        var fields = value.EnumerateObject().Select(p => p.Name).ToArray();
        return fields.Length == fields.Distinct(StringComparer.Ordinal).Count()
            && fields.All(f => allowed.Contains(f, StringComparer.Ordinal)) && required.All(f => fields.Contains(f, StringComparer.Ordinal));
    }

    static bool Hex(string? value) => value is { Length: 16 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool Timestamp(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 40 } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
    internal static DateTimeOffset ReadTime(JsonElement value) =>
        DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
