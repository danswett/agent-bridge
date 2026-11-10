using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Fleet;

// Keep one instance for the host's lifetime, including across relay reconnects.
public sealed class FleetPublisher(ISessionSource source, string machine, Func<string, string?>? name = null, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly string epoch = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    readonly SemaphoreSlim publishing = new(1, 1);
    long sequence;
    long? lastSent;
    string? lastContent;

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
            IReadOnlyList<Session> snapshot = share ? source.Snapshot() : [];
            if (share && source.Held is not null) { return false; }
            var sessions = snapshot.OrderByDescending(s => s.StatusSince).ThenBy(s => s.Id, StringComparer.Ordinal)
                .Take(64).Select(s =>
                {
                    var node = HaContract.NodeId(s.Id);
                    var id = node["agent_bridge_".Length..];
                    if (id.Length != 16 || id.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                    {
                        throw new InvalidDataException("registry publication needs a 16-hex session id");
                    }
                    var displayName = name?.Invoke(s.Id)?.Trim();
                    return new
                    {
                        id, node, name = displayName is not null && FleetNames.IsValidName(displayName) ? displayName : null,
                        state = s.Status, since = s.StatusSince,
                    };
                }).ToArray();
            var truncated = snapshot.Count > 64;
            int? total = truncated ? snapshot.Count : null;
            var content = JsonSerializer.Serialize(new { truncated, total, sessions }, ProtocolJson.Options);
            if (!force && content == lastContent && elapsed < TimeSpan.FromSeconds(20)) { return false; }
            var record = JsonSerializer.SerializeToElement(new
            {
                v = 1, machine, epoch, seq = ++sequence, at = clock.GetUtcNow(), truncated, total, sessions,
            }, ProtocolJson.Options);
            if (Encoding.UTF8.GetByteCount(record.GetRawText()) > 16 * 1024)
            {
                throw new InvalidDataException("registry publication exceeds the 16 KiB record limit");
            }
            var started = clock.GetTimestamp();
            await put(record, ct);
            lastSent = started;
            lastContent = content;
            return true;
        }
        finally { publishing.Release(); }
    }
}
