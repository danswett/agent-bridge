using System.Net.WebSockets;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// The relay's blob store as the app reaches it (docs/protocol/blobs.md): how a reply's file over
// 256 KiB goes to another machine, as the PWA's do (pwa/js/attachments.js). This machine's own
// host needs none: its pipe takes files that large in chunks (sessions.attach.direct).
public interface IBlobStore
{
    // The largest stored blob the relay takes now; 0 without a blob store or a connection.
    long Max { get; }
    // Seals file for the device `to` with a fresh key and uploads it straight to storage.
    // uploaded(bytes of the file sent so far). Throws HostErrorException when the relay or
    // storage refused it (sending again will not change that), IOException when the way there
    // failed (it may).
    Task<BlobRef> PutAsync(string to, string name, byte[] file, Action<long> uploaded);
    // Best effort: a blob nobody fetched is deleted by storage within two days anyway.
    Task DeleteAsync(string blob);
}

public static class LargeAttachments
{
    // How long a blob's ticket is reused for a file sent again, less a margin on the relay's 24 hours.
    public static readonly TimeSpan TicketReuse = TimeSpan.FromHours(20);

    // The largest file a machine takes through the blob store now: 0 unless it offers
    // sessions.attach.blob and the relay has a blob store (largeLimit in the PWA).
    public static long Max(IReadOnlyList<string> capabilities, IBlobStore? blobs) =>
        blobs is { Max: > 0 } b && capabilities.Contains(Capabilities.SessionsAttachBlob)
            ? Math.Min(AttachmentLimits.BlobBytes, BlobFormat.MaxPlain(b.Max)) : 0;

    static readonly Dictionary<string, Func<string, string>> Refusals = new()
    {
        ["too-large"] = n => $"{n} is too large for the relay's blob store",
        ["quota"] = _ => "the relay's daily allowance for large attachments is used up; try again tomorrow, or send files of up to 256 KiB",
        ["rate-limited"] = _ => "too many large attachments in the last minute; try again in a minute",
        ["disabled"] = _ => "the relay no longer takes large attachments; send files of up to 256 KiB",
        ["unavailable"] = _ => "the relay's blob store is not answering; try again in a while",
    };

    public static string Refusal(string reason, string name) =>
        Refusals.TryGetValue(reason, out var say) ? say(name) : $"the relay refused {name} ({reason})";
}

// The blob store through the app's relay connection, whichever connection is current.
public sealed class RelayBlobStore(Func<RelayClient?> relay) : IBlobStore
{
    readonly HttpClient http = BlobHttp.CreateClient();

    public long Max => relay()?.BlobMaxBytes ?? 0;

    public async Task<BlobRef> PutAsync(string to, string name, byte[] file, Action<long> uploaded)
    {
        var r = relay() ?? throw new IOException("not connected to the relay");
        try
        {
            var slot = await r.BlobPutAsync(to, BlobFormat.StoredSize(file.Length));
            var url = BlobHttp.Resolve(r.Relay, slot.Url) ?? throw new HostErrorException("the relay gave an upload address that is not Azure storage");
            var id = BlobFormat.IdOf(slot.Blob) ?? throw new HostErrorException("the relay gave a blob that cannot be sealed for");
            var key = BlobFormat.NewKey();
            // Off the UI thread, which a reply is sent from: up to 100 MiB to encrypt.
            var stored = await Task.Run(() => BlobFormat.Seal(file, key, id));
            var counted = new CountingStream(new MemoryStream(stored), n => uploaded(Math.Min(file.Length, n * file.Length / stored.Length)));
            await BlobHttp.UploadAsync(http, url, counted, stored.Length);
            return new BlobRef { Id = slot.Blob, Key = BlobFormat.Base64Url(key) };
        }
        catch (BlobRefusedException e) { throw new HostErrorException(LargeAttachments.Refusal(e.Reason, name)); }
        // A status is storage refusing it, which trying again will not change; none is a dropped
        // connection, which it may.
        catch (HttpRequestException e) when (e.StatusCode is not null) { throw new HostErrorException($"couldn't upload {name}: {e.Message}"); }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            throw new IOException($"the upload did not get through ({e.Message})", e);
        }
    }

    public async Task DeleteAsync(string blob)
    {
        if (relay() is not { } r) { return; }
        try { await r.BlobDeleteAsync(blob); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }
}

// Reports how much of a stream has been read: what an upload has sent.
sealed class CountingStream(Stream inner, Action<long> read) : Stream
{
    long total;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => total; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    int Count(int n)
    {
        total += n;
        if (n > 0) { read(total); }
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); }
        base.Dispose(disposing);
    }
}
