using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Fleet;

// Keep one instance for the host's lifetime, including across relay reconnects.
public sealed class FleetPublisher(ISessionSource source, string machine, Func<string, string?>? name = null, TimeProvider? time = null,
    Func<JsonElement, JsonElement>? sign = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly string epoch = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    readonly SemaphoreSlim publishing = new(1, 1);
    long sequence;
    long? lastSent;
    string? lastContent;
    HashSet<string> lastSessionIds = new(StringComparer.Ordinal);
    public int SkippedInvalidIds { get; private set; }

    // Called at least every two seconds; force the first snapshot on each new connection.
    // Refusals, timeouts and cancellation propagate to the host's diagnostics owner.
    public async Task<bool> PublishAsync(Func<JsonElement, CancellationToken, Task> put, bool share = true, bool force = false, CancellationToken ct = default,
        bool relaySupportsSessionId = false)
    {
        await publishing.WaitAsync(ct);
        try
        {
            var elapsed = lastSent is { } sent ? clock.GetElapsedTime(sent) : TimeSpan.MaxValue;
            if (share && source.Held is not null) { return false; }
            IReadOnlyList<Session> snapshot = share ? source.Snapshot() : [];
            if (share && source.Held is not null) { return false; }
            var usable = snapshot.Select(s => (Session: s, Node: HaContract.NodeId(s.Id))).Where(entry =>
                entry.Node["agent_bridge_".Length..] is { Length: 16 } id
                && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))).ToArray();
            SkippedInvalidIds = snapshot.Count - usable.Length;
            // Ended rows must disappear even when ordinary changes are still being coalesced.
            var removed = !lastSessionIds.IsSubsetOf(usable.Select(e => e.Node["agent_bridge_".Length..]));
            if (!force && !removed && elapsed < TimeSpan.FromSeconds(2)) { return false; }
            var sessions = usable.OrderByDescending(e => e.Session.StatusSince).ThenBy(e => e.Session.Id, StringComparer.Ordinal)
                .Take(64).Select(entry =>
                {
                    var (s, node) = entry;
                    var id = node["agent_bridge_".Length..];
                    var displayName = name?.Invoke(s.Id)?.Trim();
                    return new
                    {
                        id, node, name = displayName is not null && FleetNames.IsValidName(displayName) ? displayName : null,
                        sessionId = relaySupportsSessionId ? FleetSessionId.ForNode(s.Id, node) : null,
                        state = s.Status, since = s.StatusSince,
                        startedAt = source.ProcessOf(s.Id) is { Id: > 0, StartedAt: { } started }
                            ? started.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null,
                        owner = Outcome("owner", s.Owner), pr = Outcome("pr", s.Pr), finished = Outcome("finished", s.Finished),
                    };
                }).ToList();
            var truncated = usable.Length > 64;
            int? total = truncated ? usable.Length : null;
            var content = JsonSerializer.Serialize(new { truncated, total, sessions }, ProtocolJson.Options);
            if (!force && content == lastContent && elapsed < TimeSpan.FromSeconds(20)) { return false; }
            var seq = ++sequence;
            var at = clock.GetUtcNow();
            JsonElement record;
            while (true)
            {
                record = JsonSerializer.SerializeToElement(new
                {
                    v = 1, machine, epoch, seq, at, truncated, total, sessions,
                }, ProtocolJson.Options);
                record = sign?.Invoke(record) ?? record;
                if (Encoding.UTF8.GetByteCount(record.GetRawText()) <= 16 * 1024) { break; }
                if (sessions.Count == 0) { throw new InvalidDataException("registry publication exceeds the 16 KiB record limit"); }
                // Fit the completed signed record, dropping the least-recent state change first.
                sessions.RemoveAt(sessions.Count - 1);
                truncated = true;
                total = usable.Length;
            }
            var started = clock.GetTimestamp();
            await put(record, ct);
            lastSent = started;
            lastContent = content;
            lastSessionIds = sessions.Select(s => s.id).ToHashSet(StringComparer.Ordinal);
            return true;
        }
        finally { publishing.Release(); }
    }

    // Unknown is sent as absent, which readers already treat as unknown, so a relay older than
    // this allow-list (it refuses unknown members) sees nothing new until a value is known.
    // Malformed own evidence is dropped here rather than having the relay refuse the whole record.
    static JsonElement? Outcome(string member, object? evidence)
    {
        if (evidence is null) { return null; }
        var value = JsonSerializer.SerializeToElement(evidence, evidence.GetType(), ProtocolJson.Options);
        return value.TryGetProperty("state", out var state) && state.GetString() != "unknown" && SessionEvidence.Outcome(member, value) ? value : null;
    }
}
