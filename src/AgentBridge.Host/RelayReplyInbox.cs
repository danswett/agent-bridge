using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal sealed class RelayReplyInbox(string path, Func<RelayReplyInbox.Entry, CancellationToken, Task<ReplyResult>> dispatch,
    Action<string>? log = null, Func<string, RelayReplyInbox.Entry, bool>? ready = null, Func<string, string>? key = null)
{
    internal sealed record Entry(ReplyRequest? Reply, string Peer, string Account, LiteChannel? Channel = null, JsonElement? Data = null);
    // Relay refusals have no HA cleanup obligation; uncertain attempts stay pinned.
    readonly DurableInbox inbox = new(path, pinRefused: false);
    readonly HashSet<string> draining = new(StringComparer.Ordinal);
    readonly object admission = new();
    readonly Action<string> report = log ?? Console.Error.WriteLine;
    internal event Action? Pending;

    public async Task<ReplyResult> ReceiveAsync(string node, string rid, Entry entry, CancellationToken ct = default,
        Func<ReplyResult?>? admit = null)
    {
        if (string.IsNullOrWhiteSpace(node) || !Guid.TryParse(rid, out var request))
        { return new(AnswerOutcome.Refused, "a session and reply request UUID are required"); }
        rid = request.ToString("N");
        node = Key(node);
        try
        {
            lock (admission)
            {
                var prior = inbox.Find(node, rid);
                if (prior is not null && prior.Sender != entry.Peer) { return new(AnswerOutcome.Refused, "request belongs to another peer"); }
                if (prior is null && admit?.Invoke() is { } refusal) { return refusal; }
                inbox.Enqueue(node, rid, entry.Peer, JsonSerializer.Serialize(entry), DateTimeOffset.UtcNow);
            }
            try { return await DrainAsync(node, rid, ct); }
            finally { Pending?.Invoke(); }
        }
        catch (Exception e) when (StorageFailure(e))
        { report($"relay reply storage failed: {e.GetType().Name}"); return new(AnswerOutcome.Refused, "reply storage is unavailable"); }
    }

    public async Task RecoverAsync(IEnumerable<string> nodes, CancellationToken ct = default)
    {
        foreach (var node in nodes.SelectMany(n => new[] { n, Key(n) }).Distinct())
        {
            try { await DrainAsync(node, null, ct); }
            catch (Exception e) when (StorageFailure(e)) { report($"relay reply recovery failed: {e.GetType().Name}"); }
        }
    }

    async Task<ReplyResult> DrainAsync(string node, string? requested, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (draining) { if (!draining.Add(Key(node))) { return new(AnswerOutcome.Busy, "reply queued"); } }
        try
        {
            while (inbox.Peek(node) is { } next)
            {
                ct.ThrowIfCancellationRequested();
                ReplyResult result;
                if (next.StartedAt is not null) { result = new(AnswerOutcome.Unconfirmed); }
                else if (DateTimeOffset.UtcNow - next.QueuedAt > TimeSpan.FromMinutes(2))
                { result = new(AnswerOutcome.Refused, "queued reply expired"); }
                else
                {
                    Entry? entry;
                    try { entry = JsonSerializer.Deserialize<Entry>(next.Payload) ?? throw new InvalidDataException("Invalid relay inbox entry."); }
                    catch (Exception e) when (e is JsonException or InvalidDataException)
                    {
                        report($"relay reply entry refused: {e.GetType().Name}");
                        inbox.Deliver(node, next.Rid, DateTimeOffset.UtcNow, "refused");
                        if (next.Rid == requested) { return new(AnswerOutcome.Refused, "invalid queued reply"); }
                        continue;
                    }
                    if (ready?.Invoke(node, entry) == false) { return new(AnswerOutcome.Busy, "reply queued"); }
                    inbox.Start(node, next.Rid, DateTimeOffset.UtcNow);
                    try { result = await dispatch(entry, ct); }
                    catch (Exception e)
                    {
                        if (e is not OperationCanceledException || !ct.IsCancellationRequested)
                        { report($"relay reply dispatch failed: {e.GetType().Name}"); }
                        result = new(AnswerOutcome.Unconfirmed, "reply delivery could not be confirmed");
                    }
                }
                var outcome = result.Outcome switch
                {
                    AnswerOutcome.Delivered => "delivered",
                    AnswerOutcome.Unconfirmed => "unconfirmed",
                    _ => "refused",
                };
                inbox.Deliver(node, next.Rid, DateTimeOffset.UtcNow, outcome);
                ct.ThrowIfCancellationRequested();
                if (next.Rid == requested) { return result; }
            }
            return new(inbox.Find(node, requested ?? "")?.Outcome switch
            {
                "delivered" => AnswerOutcome.Delivered,
                "unconfirmed" => AnswerOutcome.Unconfirmed,
                _ => AnswerOutcome.Refused,
            });
        }
        finally { lock (draining) { draining.Remove(Key(node)); } }
    }

    string Key(string node) => key?.Invoke(node) ?? node;
    static bool StorageFailure(Exception e) => e is IOException or UnauthorizedAccessException or JsonException;
}
