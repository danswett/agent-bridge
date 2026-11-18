using System.Threading.Channels;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed partial class SessionService
{
    internal RelayReplyInbox? RelayInbox { get; private set; }

    internal void ConfigureRelayInbox(string path, Func<string, string, bool> personAllowed, Func<LiteChannel, bool> hostAllowed,
        Func<LiteChannel, bool>? hostPresent = null)
    {
        RelayInbox = new(path, async (entry, ct) =>
        {
            if (ReadOnly?.Invoke() is { } refusal) { return new(AnswerOutcome.Refused, refusal); }
            if (entry.Reply is { } reply)
            {
                return personAllowed(entry.Peer, entry.Account)
                    ? await ReplyCoreAsync(reply with { RequireIdle = true }, entry.Peer, ct) : new(AnswerOutcome.Refused, "the peer is not authorized");
            }
            if (LiteReceiver is null || entry.Channel is not { } channel || entry.Data is not { } data || !hostAllowed(channel))
            {
                return new(AnswerOutcome.Refused, "the peer is not authorized");
            }
            // Reverify the original signature, lifetime, mode, peers and target gates on recovery.
            var result = await LiteReceiver.ReceiveAsync(channel, data, ct, fromInbox: true);
            return new(result.Status == "delivered" ? AnswerOutcome.Delivered : AnswerOutcome.Refused, result.Code);
        }, log, (node, entry) =>
            (RelayReplyTargets.Resolve(source, node) is not { } s || s.Status == SessionStatus.Idle && s.Question is null) &&
            (entry.Channel is not { } c || hostPresent?.Invoke(c) != false),
            node => node.StartsWith("agent_bridge_", StringComparison.Ordinal) ? node : HaContract.NodeId(node));
        if (LiteReceiver is not null) { LiteReceiver.Inbox = RelayInbox; }
    }

    internal async Task RunRelayRecoveryAsync(CancellationToken ct, RelayClient? relay = null)
    {
        if (RelayInbox is not { } inbox) { return; }
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void Signal() => wake.Writer.TryWrite(true);
        void Changed(object? sender, SessionChange change) => Signal();
        void Presence(IReadOnlyList<PresenceEntry> entries) => Signal();
        source.Changed += Changed;
        inbox.Pending += Signal;
        if (relay is not null) { relay.PresenceChanged += Presence; }
        try
        {
            Signal();
            await foreach (var _ in wake.Reader.ReadAllAsync(ct))
            { await inbox.RecoverAsync(source.Snapshot().Select(s => s.Id), ct); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            source.Changed -= Changed; inbox.Pending -= Signal;
            if (relay is not null) { relay.PresenceChanged -= Presence; }
        }
    }
}

internal static class RelayReplyTargets
{
    internal static Session? Resolve(ISessionSource source, string target)
    {
        if (source.Get(target) is { } direct) { return direct; }
        var matches = source.Snapshot().Where(s => HaContract.NodeId(s.Id) == target).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
