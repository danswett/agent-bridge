namespace AgentBridge.Transport;

// One client's conversation with the host, whatever carries it: a Noise channel through the
// relay (SecureChannel), or the local pipe from an app on the same machine (docs/protocol/
// local-pipe.md). The host's services see only this, so a phone and the desktop app get
// exactly the same behaviour from the same code.
public interface IProtocolChannel
{
    // Who is on the other end: a device id through the relay, "local-<pid>" on the pipe.
    // Attachments and answer receipts are kept per peer.
    string PeerId { get; }
    string PeerName { get; }
    string PeerKind { get; }
    bool IsOpen { get; }
    // Completes (never faults) when the channel ends.
    Task Closed { get; }
    // Sends {ev, seq, data}; seq goes up by one per event on this channel.
    Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default);
}
