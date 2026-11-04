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
    // progress hears the host's {re, progress} frames for this request before its response, for an
    // op that asked for them (session.reply and session.answer with progress: true, when hello
    // offers sessions.progress). Raised on the receive path: keep it quick. A progress frame never
    // completes the call.
    Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null);
    // The same, with the seq of the last event this end had received when the response came (-1
    // for none): events with a higher seq came after the response. A subscribe's snapshot is the
    // events after its response; one before belongs to an earlier subscription (#427).
    Task<(JsonElement Response, long EventsBefore)> RequestOrderedAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null);
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
    public async Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null) =>
        (await RequestOrderedAsync(op, data, timeout, ct, progress)).Response;

    public async Task<(JsonElement Response, long EventsBefore)> RequestOrderedAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null)
    {
        try { return await channel.RequestOrderedAsync(op, data, timeout, ct, progress); }
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
