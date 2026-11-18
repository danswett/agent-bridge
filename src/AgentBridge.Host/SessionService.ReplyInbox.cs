using AgentBridge.Host.Lite;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public sealed partial class SessionService
{
    internal RelayReplyInbox? RelayInbox { get; private set; }

    internal void ConfigureRelayInbox(string path, Func<string, string, bool> personAllowed, Func<LiteChannel, bool> hostAllowed)
    {
        RelayInbox = new(path, async (entry, ct) =>
        {
            if (entry.Reply is { } reply)
            {
                return personAllowed(entry.Peer, entry.Account)
                    ? await ReplyCoreAsync(reply, entry.Peer) : new(AnswerOutcome.Refused, "the peer is not authorized");
            }
            if (LiteReceiver is null || entry.Channel is not { } channel || entry.Data is not { } data || !hostAllowed(channel))
            {
                return new(AnswerOutcome.Refused, "the peer is not authorized");
            }
            // Reverify the original signature, lifetime, mode, peers and target gates on recovery.
            var result = await LiteReceiver.ReceiveAsync(channel, data, ct, fromInbox: true);
            return new(result.Status == "delivered" ? AnswerOutcome.Delivered : AnswerOutcome.Refused, result.Code);
        });
        if (LiteReceiver is not null) { LiteReceiver.Inbox = RelayInbox; }
    }
}
