using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentBridge.Transport;

// The client's end of a conversation with a host, whatever carries it: the local pipe to this
// machine's host, or a Noise channel through the relay to another machine's. The desktop app
// speaks to every machine through this, so a card on another machine behaves exactly as one
// on its own.
public interface IClientChannel : IProtocolChannel, IAsyncDisposable
{
    ChannelReader<ChannelEvent> Events { get; }
    Exception? CloseReason { get; }
    Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default);
}

// A channel this device opened through the relay, as a client holds it: disposing it closes
// the channel, telling the host it can let go at once rather than when this device leaves.
public sealed class RelayClientChannel(ChannelHub hub, SecureChannel channel) : IClientChannel
{
    public SecureChannel Channel => channel;
    public string PeerId => channel.PeerId;
    public string PeerName => channel.PeerName;
    public string PeerKind => channel.PeerKind;
    public bool IsOpen => channel.IsOpen;
    public Task Closed => channel.Closed;
    public Exception? CloseReason => channel.CloseReason;
    public ChannelReader<ChannelEvent> Events => channel.Events;

    // A channel that ends under a call (a frame that fails to decrypt, a handshake repeated on an
    // open channel, an authenticated frame that is malformed) is a lost connection to whoever
    // holds it: the pipe's callers recover from IOException, and would otherwise stop for good, or
    // leave an answer sending. Anything else, with the channel still open, is left as it is.
    public async Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try { return await channel.RequestAsync(op, data, timeout, ct); }
        catch (Exception e) when (Ended(e)) { throw new IOException($"the channel to {channel.PeerName} failed: {e.Message}", e); }
    }

    public async Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default)
    {
        try { return await channel.SendEventAsync(name, data, ct); }
        catch (Exception e) when (Ended(e)) { throw new IOException($"the channel to {channel.PeerName} failed: {e.Message}", e); }
    }

    bool Ended(Exception e) => e is CryptographicException || (ChannelHub.EndsChannel(e) && !channel.IsOpen);

    public async ValueTask DisposeAsync()
    {
        if (!channel.Closed.IsCompleted) { await hub.CloseAsync(channel); }
    }
}
