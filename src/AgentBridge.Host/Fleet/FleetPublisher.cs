using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Fleet;

// Keep one instance for the host's lifetime, including across relay reconnects.
public sealed class FleetPublisher(ISessionSource source, string machine, Func<string, string?>? name = null, TimeProvider? time = null,
    Func<JsonElement, JsonElement>? sign = null, Func<FleetSessionSnapshot, CancellationToken, Task<SessionEvidence?>>? observe = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly string epoch = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    readonly SemaphoreSlim publishing = new(1, 1);
    long sequence;
    long? lastSent;
    string? lastContent;
    public int SkippedInvalidIds { get; private set; }

    // Called at least every two seconds; force the first snapshot on each new connection.
    // Refusals, timeouts and cancellation propagate to the host's diagnostics owner.
    public async Task<bool> PublishAsync(Func<JsonElement, CancellationToken, Task> put, bool share = true, bool force = false, CancellationToken ct = default)
    {
        await publishing.WaitAsync(ct);
        try
        {
            var elapsed = lastSent is { } sent ? clock.GetElapsedTime(sent) : TimeSpan.MaxValue;
            if (!force && elapsed < TimeSpan.FromSeconds(2)) { return false; }
            if (share && source.Held is not null) { return false; }
            IReadOnlyList<FleetSessionSnapshot> snapshot = share ? await source.FleetSnapshotAsync(ct) : [];
            if (share && source.Held is not null) { return false; }
            var usable = snapshot.Select(s => (Snapshot: s, Session: s.Session, Node: HaContract.NodeId(s.Session.Id))).Where(entry =>
                entry.Node["agent_bridge_".Length..] is { Length: 16 } id
                && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))).ToArray();
            SkippedInvalidIds = snapshot.Count - usable.Length;
            var sessions = new List<PublishedSession>();
            foreach (var entry in usable.OrderByDescending(e => e.Session.StatusSince).ThenBy(e => e.Session.Id, StringComparer.Ordinal).Take(64))
            {
                var (_, s, node) = entry;
                var displayName = name?.Invoke(s.Id)?.Trim();
                var evidence = observe is null ? FleetEvidenceCapture.Evidence(FleetEvidenceCapture.Facts(entry.Snapshot, clock.GetUtcNow()))
                    : await observe(entry.Snapshot, ct);
                if (observe is not null && source.ProcessOf(s.Id) != entry.Snapshot.Process) { throw new InvalidOperationException("refused: fleet session binding changed during observation"); }
                sessions.Add(new(node["agent_bridge_".Length..], node, displayName is not null && FleetNames.IsValidName(displayName) ? displayName : null,
                    s.Status, s.StatusSince, entry.Snapshot.Process is { Id: > 0, StartedAt: { } processStarted }
                        ? processStarted.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null, evidence));
            }
            if (share && source.Held is not null) { return false; }
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
            return true;
        }
        finally { publishing.Release(); }
    }

    sealed record PublishedSession(string Id, string Node, string? Name, SessionStatus State, DateTimeOffset Since, string? StartedAt,
        [property: JsonIgnore] SessionEvidence? Evidence)
    {
        [JsonExtensionData] public Dictionary<string, JsonElement>? EvidenceFields => Evidence?.Copy();
    }
}
