using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace AgentBridge.Transport;

// The blob store's one encrypted format (design §4.6; docs/protocol/blobs.md). The sender
// picks a random 256-bit key per blob and seals the bytes as a run of 64 KiB AES-256-GCM
// records. The nonce is the record's index, and the associated data binds the blob's id, the
// index and whether the record is the last, so a relay or a store that truncates, reorders or
// splices records is caught. The sealed records are then padded with zeros to a size bucket,
// so the size the service sees says little about the file. The key, the true size and the
// file's SHA-256 travel only inside the Noise channel; the relay and storage see this.
// pwa/js/blobs.js implements the same format byte for byte.
public static class BlobFormat
{
    public const int Record = 64 * 1024;
    public const int Tag = 16;
    public const int KeyBytes = 32;
    public const int IdBytes = 16;
    const int NonceBytes = 12;
    const long Mib = 1024 * 1024;
    // The smallest bucket. Below it every blob looks the same.
    public const long MinStored = 4096;
    static readonly byte[] Label = "agent-bridge blob v1"u8.ToArray();

    public static long Records(long size) => size <= 0 ? 0 : (size + Record - 1) / Record;

    // The records alone, before padding.
    public static long SealedSize(long size) => size + Records(size) * Tag;

    // What is uploaded and stored for a file of this many bytes.
    public static long StoredSize(long size) => Bucket(SealedSize(size));

    // Powers of two up to 1 MiB, then whole MiB.
    public static long Bucket(long n)
    {
        if (n <= MinStored) { return MinStored; }
        if (n <= Mib) { return (long)BitOperations.RoundUpToPowerOf2((ulong)n); }
        return (n + Mib - 1) / Mib * Mib;
    }

    public static bool IsBucket(long n) => n >= MinStored && Bucket(n) == n;

    // The largest file whose stored form fits in maxStored bytes.
    public static long MaxPlain(long maxStored)
    {
        if (maxStored < MinStored) { return 0; }
        long lo = 0, hi = maxStored;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (StoredSize(mid) <= maxStored) { lo = mid; } else { hi = mid - 1; }
        }
        return lo;
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    // The blob's id, which the associated data binds: the 16 random bytes that start a
    // relay's ticket (its first dot-separated part, base64url). Null if it is not a ticket.
    public static byte[]? IdOf(string? ticket)
    {
        var dot = ticket?.IndexOf('.') ?? -1;
        if (dot != 22) { return null; }
        var b = FromBase64Url(ticket![..dot]);
        return b is { Length: IdBytes } ? b : null;
    }

    static byte[] Nonce(long index)
    {
        var n = new byte[NonceBytes];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), (ulong)index);
        return n;
    }

    static byte[] Aad(ReadOnlySpan<byte> id, long index, bool final)
    {
        var a = new byte[Label.Length + IdBytes + 8 + 1];
        Label.CopyTo(a, 0);
        id.CopyTo(a.AsSpan(Label.Length));
        BinaryPrimitives.WriteUInt64BigEndian(a.AsSpan(Label.Length + IdBytes), (ulong)index);
        a[^1] = final ? (byte)1 : (byte)0;
        return a;
    }

    static void Check(ReadOnlySpan<byte> key, ReadOnlySpan<byte> id)
    {
        if (key.Length != KeyBytes) { throw new ArgumentException("a blob key is 32 bytes"); }
        if (id.Length != IdBytes) { throw new ArgumentException("a blob id is 16 bytes"); }
    }

    // Seals a whole file into its stored form, padding included.
    public static byte[] Seal(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, ReadOnlySpan<byte> id)
    {
        Check(key, id);
        if (plain.Length == 0) { throw new ArgumentException("a blob is at least one byte"); }
        var records = Records(plain.Length);
        var output = new byte[StoredSize(plain.Length)];
        using var gcm = new AesGcm(key, Tag);
        var at = 0;
        for (long i = 0; i < records; i++)
        {
            var chunk = plain.Slice((int)(i * Record), (int)Math.Min(Record, plain.Length - i * Record));
            var dest = output.AsSpan(at, chunk.Length);
            var tag = output.AsSpan(at + chunk.Length, Tag);
            gcm.Encrypt(Nonce(i), chunk, dest, tag, Aad(id, i, i == records - 1));
            at += chunk.Length + Tag;
        }
        return output;
    }

    // Seals a stream of exactly size bytes into output as it reads, for files too large to
    // hold twice in memory.
    public static async Task SealAsync(Stream input, long size, byte[] key, byte[] id, Stream output, CancellationToken ct = default)
    {
        Check(key, id);
        if (size <= 0) { throw new ArgumentException("a blob is at least one byte"); }
        var records = Records(size);
        using var gcm = new AesGcm(key, Tag);
        var plain = new byte[Record];
        var sealedRecord = new byte[Record + Tag];
        for (long i = 0; i < records; i++)
        {
            var n = (int)Math.Min(Record, size - i * Record);
            await input.ReadExactlyAsync(plain.AsMemory(0, n), ct);
            gcm.Encrypt(Nonce(i), plain.AsSpan(0, n), sealedRecord.AsSpan(0, n), sealedRecord.AsSpan(n, Tag), Aad(id, i, i == records - 1));
            await output.WriteAsync(sealedRecord.AsMemory(0, n + Tag), ct);
        }
        await WriteZerosAsync(output, StoredSize(size) - SealedSize(size), ct);
    }

    // Opens a stored blob as it streams in, writing the file to output record by record, and
    // returns the file's SHA-256. Throws BlobIntegrityException for anything that is not
    // exactly what the sender sealed for this id and size: a record that fails its tag, one
    // missing or out of place, a stored length other than the bucket, or non-zero padding.
    public static async Task<byte[]> OpenAsync(Stream stored, long size, byte[] key, byte[] id, Stream output, Action<long>? progress = null, CancellationToken ct = default)
    {
        Check(key, id);
        if (size <= 0) { throw new ArgumentException("a blob is at least one byte"); }
        var records = Records(size);
        using var gcm = new AesGcm(key, Tag);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sealedRecord = new byte[Record + Tag];
        var plain = new byte[Record];
        long done = 0;
        for (long i = 0; i < records; i++)
        {
            var n = (int)Math.Min(Record, size - i * Record);
            if (!await FillAsync(stored, sealedRecord.AsMemory(0, n + Tag), ct)) { throw new BlobIntegrityException("the blob is shorter than its size says"); }
            try { gcm.Decrypt(Nonce(i), sealedRecord.AsSpan(0, n), sealedRecord.AsSpan(n, Tag), plain.AsSpan(0, n), Aad(id, i, i == records - 1)); }
            catch (AuthenticationTagMismatchException) { throw new BlobIntegrityException("a part of the blob was changed, moved or is not from this blob"); }
            hash.AppendData(plain, 0, n);
            await output.WriteAsync(plain.AsMemory(0, n), ct);
            done += n;
            progress?.Invoke(done);
        }
        // The padding: exactly to the bucket, and zeros. Not secret, but a store that adds or
        // changes bytes is not storing what was sent.
        var padding = StoredSize(size) - SealedSize(size);
        var buffer = new byte[16 * 1024];
        long read = 0;
        while (true)
        {
            var r = await stored.ReadAsync(buffer, ct);
            if (r == 0) { break; }
            read += r;
            if (read > padding || buffer.AsSpan(0, r).ContainsAnyExcept((byte)0)) { throw new BlobIntegrityException("the blob has bytes after its end"); }
        }
        if (read != padding) { throw new BlobIntegrityException("the blob is shorter than its size says"); }
        return hash.GetHashAndReset();
    }

    static async Task<bool> FillAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var at = 0;
        while (at < buffer.Length)
        {
            var r = await s.ReadAsync(buffer[at..], ct);
            if (r == 0) { return false; }
            at += r;
        }
        return true;
    }

    static async Task WriteZerosAsync(Stream output, long count, CancellationToken ct)
    {
        var zeros = new byte[(int)Math.Min(count, 64 * 1024)];
        while (count > 0)
        {
            var n = (int)Math.Min(count, zeros.Length);
            await output.WriteAsync(zeros.AsMemory(0, n), ct);
            count -= n;
        }
    }

    public static string Base64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) { return null; }
        var t = s.Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4)); }
        catch (FormatException) { return null; }
    }
}

public sealed class BlobIntegrityException(string message) : Exception(message);

// The relay refused a blob request; Reason is its code (too-large, quota, forbidden, ...).
public sealed class BlobRefusedException(string reason) : Exception($"the relay refused the blob request: {reason}")
{
    public string Reason => reason;
}

// Url: as the relay gave it, absolute, or a path on the relay itself (a dev relay).
public sealed record BlobSlot(string Blob, string Url, DateTimeOffset Expires);

public sealed record BlobLocation(string Url, long Size);

// The bytes' trip to and from storage, on the short-lived single-blob URLs the relay issues.
// Every URL comes from the relay, which is not trusted, so one is only followed to Azure
// Blob Storage or back to the relay itself (a dev relay serves blobs on its own origin),
// over HTTPS unless it is that relay, and never through a redirect.
public static class BlobHttp
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        UseCookies = false,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    // The URL to use, resolved against the relay (a dev relay gives a path), or null if it
    // points anywhere a blob does not live.
    public static Uri? Resolve(Uri relay, string url)
    {
        if (!Uri.TryCreate(relay, url, out var u)) { return null; }
        if (u.Scheme == relay.Scheme && u.Authority.Equals(relay.Authority, StringComparison.OrdinalIgnoreCase)) { return u; }
        if (u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(u.UserInfo)) { return u; }
        return null;
    }

    // Put Blob, which a create-only URL allows exactly once.
    public static async Task UploadAsync(HttpClient http, Uri url, Stream content, long length, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StreamContent(content) };
        req.Content.Headers.ContentLength = length;
        req.Content.Headers.ContentType = new("application/octet-stream");
        req.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        using var r = await http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode) { throw new HttpRequestException($"storage answered {(int)r.StatusCode} to the upload", null, r.StatusCode); }
    }

    public static async Task<Stream> DownloadAsync(HttpClient http, Uri url, long expected, CancellationToken ct = default)
    {
        var r = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!r.IsSuccessStatusCode)
        {
            r.Dispose();
            throw new HttpRequestException($"storage answered {(int)r.StatusCode} to the download", null, r.StatusCode);
        }
        if (r.Content.Headers.ContentLength is { } n && n != expected)
        {
            r.Dispose();
            throw new BlobIntegrityException("the stored blob is not the size the relay said");
        }
        return await r.Content.ReadAsStreamAsync(ct);
    }
}

// Both ends of one blob through the relay: seal and upload for a peer, or fetch, open and
// delete. The caller carries the ticket, key, size and hash to the peer inside a Noise channel.
public static class BlobTransfer
{
    public sealed record Sent(string Blob, byte[] Key, byte[] Sha256, long Size);

    public static async Task<Sent> SendAsync(RelayClient relay, HttpClient http, string to, byte[] file, CancellationToken ct = default)
    {
        var slot = await relay.BlobPutAsync(to, BlobFormat.StoredSize(file.Length), ct);
        var url = BlobHttp.Resolve(relay.Relay, slot.Url) ?? throw new BlobRefusedException("bad-url");
        var key = BlobFormat.NewKey();
        var stored = BlobFormat.Seal(file, key, BlobFormat.IdOf(slot.Blob) ?? throw new BlobRefusedException("bad-ticket"));
        await BlobHttp.UploadAsync(http, url, new MemoryStream(stored), stored.Length, ct);
        return new Sent(slot.Blob, key, SHA256.HashData(file), file.Length);
    }

    // Writes the file to output and returns its SHA-256; the caller compares it with the one
    // the sender gave, and deletes the blob once satisfied.
    public static async Task<byte[]> ReceiveAsync(RelayClient relay, HttpClient http, string blob, byte[] key, long size, Stream output, Action<long>? progress = null, CancellationToken ct = default)
    {
        var id = BlobFormat.IdOf(blob) ?? throw new BlobRefusedException("bad-ticket");
        var where = await relay.BlobGetAsync(blob, ct);
        if (where.Size != BlobFormat.StoredSize(size)) { throw new BlobIntegrityException("the stored blob is not the size of this file"); }
        var url = BlobHttp.Resolve(relay.Relay, where.Url) ?? throw new BlobRefusedException("bad-url");
        await using var stored = await BlobHttp.DownloadAsync(http, url, where.Size, ct);
        return await BlobFormat.OpenAsync(stored, size, key, id, output, progress, ct);
    }
}
