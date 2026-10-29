using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// The blob store as the host reaches it: through whichever relay connection it has now
// (Attach is called on every connect and disconnect). It asks the relay for a read URL, which
// it follows only to Azure Storage or the relay itself, opens the blob as it streams in
// (BlobFormat), and deletes it once the store has checked the file's hash.
public sealed class RelayBlobs : IBlobSource, IDisposable
{
    readonly HttpClient http = BlobHttp.CreateClient();
    RelayClient? relay;

    public void Attach(RelayClient? connection) => Volatile.Write(ref relay, connection);

    public long MaxBytes => Volatile.Read(ref relay) is { BlobMaxBytes: > 0 } r ? BlobFormat.MaxPlain(r.BlobMaxBytes) : 0;

    public async Task FetchAsync(BlobRef blob, long size, Stream output, Action<long> progress, CancellationToken cancellationToken)
    {
        var r = Volatile.Read(ref relay) ?? throw new IOException("the host is not connected to the relay");
        var key = BlobFormat.FromBase64Url(blob.Key) ?? throw new InvalidDataException("the blob's key is not base64url");
        // A whole 100 MB fetch is seconds on a desktop's link; the bound stops a stalled
        // transfer from holding a fetch slot for ever.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(20));
        try { await BlobTransfer.ReceiveAsync(r, http, blob.Id, key, size, output, progress, limit.Token); }
        catch (BlobIntegrityException e) { throw new InvalidDataException(e.Message, e); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("fetching the blob took over 20 minutes"); }
    }

    public async Task ForgetAsync(BlobRef blob)
    {
        if (Volatile.Read(ref relay) is { } r) { await r.BlobDeleteAsync(blob.Id); }
    }

    public void Dispose() => http.Dispose();
}
