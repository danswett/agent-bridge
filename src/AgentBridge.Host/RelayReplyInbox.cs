using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal sealed class RelayReplyInbox(string path, Func<RelayReplyInbox.Entry, CancellationToken, Task<ReplyResult>> dispatch)
{
    internal sealed record Entry(ReplyRequest? Reply, string Peer, string Account, LiteChannel? Channel = null, JsonElement? Data = null);
    readonly DurableInbox inbox = new(path);
    readonly SemaphoreSlim serial = new(1, 1);

    public async Task<ReplyResult> ReceiveAsync(string node, string rid, Entry entry, CancellationToken ct = default)
    {
        if (!Guid.TryParse(rid, out var request)) { throw new ArgumentException("A reply request UUID is required."); }
        rid = request.ToString("N");
        inbox.Enqueue(node, rid, entry.Peer, JsonSerializer.Serialize(entry), DateTimeOffset.UtcNow);
        return await DrainAsync(node, rid, ct);
    }

    public async Task RecoverAsync(IEnumerable<string> nodes, CancellationToken ct = default)
    {
        foreach (var node in nodes) { await DrainAsync(node, null, ct); }
    }

    async Task<ReplyResult> DrainAsync(string node, string? requested, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            while (inbox.Peek(node) is { } next)
            {
                ct.ThrowIfCancellationRequested();
                ReplyResult result;
                if (next.StartedAt is not null) { result = new(AnswerOutcome.Unconfirmed); }
                else
                {
                    var entry = JsonSerializer.Deserialize<Entry>(next.Payload) ?? throw new InvalidDataException("Invalid relay inbox entry.");
                    inbox.Start(node, next.Rid, DateTimeOffset.UtcNow);
                    result = await dispatch(entry, ct);
                }
                var outcome = result.Outcome switch
                {
                    AnswerOutcome.Delivered => "delivered",
                    AnswerOutcome.Unconfirmed => "unconfirmed",
                    _ => "refused",
                };
                inbox.Deliver(node, next.Rid, DateTimeOffset.UtcNow, outcome);
                if (next.Rid == requested) { return result; }
            }
            return new(inbox.Find(node, requested ?? "")?.Outcome switch
            {
                "delivered" => AnswerOutcome.Delivered,
                "unconfirmed" => AnswerOutcome.Unconfirmed,
                _ => AnswerOutcome.Refused,
            });
        }
        finally { serial.Release(); }
    }
}
