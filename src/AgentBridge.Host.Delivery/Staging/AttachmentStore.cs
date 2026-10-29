using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Staging;

public sealed class AttachmentOptions
{
    // The profile's own private folder for files about to be handed to an agent. Created, and
    // narrowed to this user, on first use.
    public required string StagingRoot { get; init; }

    // An upload, finished or not, that no reply has used for this long is dropped.
    public TimeSpan HoldFor { get; init; } = TimeSpan.FromMinutes(15);

    // A staged file is the agent's to read once its reply has been typed, and a working session
    // reads it only when it takes the prompt, so it is kept this long (agent-ha-bridge's sweep
    // uses the same 24 hours) rather than deleted at once.
    public TimeSpan KeepStagedFor { get; init; } = TimeSpan.FromHours(24);

    // How many attachments the host holds at once, across devices. Those sent in the channel
    // are in memory: 32 of 256 KiB is 8 MiB at most. Those from the blob store, and larger ones
    // sent in chunks over a direct channel, are on disk.
    public int MaxHeld { get; init; } = 32;

    // Bytes of large attachments held at once on disk in the private folder: fetched or being
    // fetched from the blob store, or arriving in chunks over a direct channel.
    public long MaxHeldBlobBytes { get; init; } = 1024L * 1024 * 1024;

    // Blob-store fetches running at once; the rest wait their turn.
    public int ConcurrentFetches { get; init; } = 2;

    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromMinutes(10);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// Where attachments too large for the channel come from: the relay's blob store, through
// whatever relay connection the host has now (docs/protocol/blobs.md).
public interface IBlobSource
{
    // The largest file it can fetch now; 0 while the host has no relay with a blob store.
    long MaxBytes { get; }

    // Writes the file the blob holds to output, reporting how many bytes it has written. Throws
    // InvalidDataException when the blob is not exactly what was sealed for it (changed, cut
    // short, padded wrongly, or another blob), and anything else when it cannot be reached.
    Task FetchAsync(BlobRef blob, long size, Stream output, Action<long> progress, CancellationToken cancellationToken);

    // Deletes the blob from the store once the host has what it needs from it. Best effort:
    // the store's lifecycle deletes it within a day anyway.
    Task ForgetAsync(BlobRef blob);
}

// One attachment as it arrives and until a reply uses it.
internal sealed class Held(string peer, AttachmentStart start, byte[] sha256)
{
    public readonly string Peer = peer;
    public readonly AttachmentStart Start = start;
    public readonly Guid Id = Guid.ParseExact(start.Attachment, "D");
    public readonly byte[] Sha256 = sha256;
    // In memory for one sent in the channel up to AttachmentLimits.Bytes; empty for one on disk
    // (from the blob store, or larger and sent over a direct channel), which is in File.
    public readonly byte[] Bytes = OnDisk(start) ? [] : new byte[start.Size];
    public string? File;
    public CancellationTokenSource? Fetching;
    // For one arriving in chunks onto disk: the file as it is written, and its hash so far, so
    // finishing a 100 MiB file does not read it all again under the store's lock.
    public FileStream? Writing;
    public IncrementalHash? Hashing;
    // Why fetching it from the blob store failed: said once, to the next attachment.start.
    public string? Failed;
    public long Received;
    public bool Complete;
    public bool Claimed;
    public bool Gone;
    public DateTimeOffset Touched;

    public bool Same(AttachmentStart other) => Same(Start, other);

    public static bool OnDisk(AttachmentStart start) => start.Blob is not null || start.Size > AttachmentLimits.Bytes;

    public static bool Same(AttachmentStart a, AttachmentStart b) =>
        a.Size == b.Size && a.Name == b.Name && string.Equals(a.Sha256, b.Sha256, StringComparison.Ordinal)
        && string.Equals(a.Blob?.Id, b.Blob?.Id, StringComparison.Ordinal) && string.Equals(a.Blob?.Key, b.Blob?.Key, StringComparison.Ordinal);
}

// Attachments reserved for one reply, until it is typed or not.
public sealed class Claim
{
    internal Claim(IReadOnlyList<Held> items) => Items = items;

    internal IReadOnlyList<Held> Items { get; }

    internal IReadOnlyList<string> Staged { get; set; } = [];

    public int Count => Items.Count;

    public long Bytes => Items.Sum(i => i.Start.Size);
}

// Attachments sent with replies, from the first chunk until the agent has the file:
//   * Held while they arrive and until a reply uses them, for the device that sent them only,
//     each checked against its size and SHA-256 once whole. One sent in the channel is held in
//     memory, unless it is over 256 KiB (only a direct channel, this machine's own pipe, takes
//     those in chunks), when it is written to a private folder (incoming/ in the staging
//     folder) as it arrives. One in the relay's blob store is fetched by the host itself, opened
//     as it arrives, and held on disk in the same folder, never in
//     a shared temporary folder; once it checks out, the blob is deleted from the store.
//   * Staged, when a reply is about to be typed, into a folder only this user can read, with
//     a random name and the extension the file's own bytes call for, since that is what
//     Copilot goes by.
//   * Once typed, gone from memory and from incoming/; the staged copy is deleted after
//     KeepStagedFor. A reply that types nothing deletes its staged copies at once and leaves
//     the attachments held, so the person can send again.
// Names and contents never reach the log.
public sealed partial class AttachmentStore : IDisposable
{
    readonly AttachmentOptions options;
    readonly Action<string> log;
    readonly IBlobSource? blobs;
    readonly Lock gate = new();
    readonly Dictionary<Guid, Held> held = [];
    // Attachments a typed reply has used: starting one again (a client resending after a lost
    // response) says it is complete instead of taking every byte again for nothing.
    readonly Dictionary<Guid, (string Peer, AttachmentStart Start, DateTimeOffset At)> used = [];
    readonly SemaphoreSlim fetchSlots;
    readonly ITimer? sweeper;

    public AttachmentStore(AttachmentOptions options, Action<string>? log = null, bool sweep = true, IBlobSource? blobs = null)
    {
        this.options = options;
        this.log = log ?? (_ => { });
        this.blobs = blobs;
        fetchSlots = new SemaphoreSlim(Math.Max(1, options.ConcurrentFetches));
        if (sweep)
        {
            Sweep();
            sweeper = options.Time.CreateTimer(_ => Sweep(), null, options.SweepEvery, options.SweepEvery);
        }
    }

    public string StagingRoot => options.StagingRoot;

    // Where files from the blob store are written as they are opened.
    public string IncomingRoot => Path.Combine(options.StagingRoot, "incoming");

    // The largest file this host can fetch from the blob store now: 0 without one.
    public long BlobBytes => Math.Min(blobs?.MaxBytes ?? 0, AttachmentLimits.BlobBytes);

    // How many attachments are held now.
    public int Count { get { lock (gate) { return held.Count; } } }

    public void Dispose()
    {
        sweeper?.Dispose();
        lock (gate)
        {
            foreach (var h in held.Values)
            {
                h.Fetching?.Cancel();
                CloseWriting(h);
            }
        }
    }

    // direct: the channel is this machine's own pipe, which takes files up to
    // AttachmentLimits.DirectBytes in chunks; any other takes AttachmentLimits.Bytes.
    public AttachmentState Start(string peer, AttachmentStart start, bool direct = false)
    {
        if (Attachments.Check(start, direct ? AttachmentLimits.DirectBytes : AttachmentLimits.Bytes) is { } problem) { throw new ArgumentException(problem); }
        var id = Guid.ParseExact(start.Attachment, "D");
        lock (gate)
        {
            Expire();
            if (held.TryGetValue(id, out var h) && h.Peer == peer)
            {
                if (!h.Same(start)) { throw new ArgumentException("that attachment was started with a different name, size or sha256"); }
                if (h.Failed is { } failed)
                {
                    Drop(h);
                    throw new InvalidOperationException(failed);
                }
                h.Touched = Now;
                return new AttachmentState(Interlocked.Read(ref h.Received), h.Complete);
            }
            if (used.TryGetValue(id, out var u) && u.Peer == peer && Held.Same(u.Start, start))
            {
                return new AttachmentState(u.Start.Size, true);
            }
            // Another device's id is not this one's to see or reuse.
            if (held.ContainsKey(id) || used.ContainsKey(id)) { throw new ArgumentException("that attachment id is taken; use a new one"); }
            if (held.Count >= options.MaxHeld)
            {
                throw new InvalidOperationException($"the host is already holding {options.MaxHeld} attachments; send or remove some first");
            }
            var item = new Held(peer, start, Attachments.Sha256(start.Sha256)!) { Touched = Now };
            if (start.Blob is not null) { BeginFetch(item); }
            else if (Held.OnDisk(start)) { BeginWrite(item); }
            held[id] = item;
            return new AttachmentState(0, false);
        }
    }

    // Under the lock: checks a large attachment fits beside those already on disk, and returns
    // the private folder it goes in.
    string Reserve(Held h)
    {
        var holding = held.Values.Where(x => Held.OnDisk(x.Start)).Sum(x => x.Start.Size);
        if (holding + h.Start.Size > options.MaxHeldBlobBytes)
        {
            throw new InvalidOperationException($"the host is already holding {holding / (1024 * 1024)} MiB of large attachments; send or remove some first");
        }
        try { return PrivateFolder.Ensure(IncomingRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"cannot take large attachments: {e.Message}");
            throw new InvalidOperationException($"the host has no private folder to put the attachment in ({e.Message})");
        }
    }

    // Under the lock: a large attachment sent in chunks over a direct channel is written to the
    // private folder as it arrives, never held whole in memory.
    void BeginWrite(Held h)
    {
        var folder = Reserve(h);
        h.File = Path.Combine(folder, $"{Guid.NewGuid():N}.part");
        try { h.Writing = PrivateFolder.CreateNew(h.File); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"cannot take large attachments: {e.Message}");
            throw new InvalidOperationException($"the host could not create the attachment's file ({e.Message})");
        }
        h.Hashing = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    // Under the lock: checks a blob-store attachment can be taken, and starts fetching it.
    void BeginFetch(Held h)
    {
        var max = BlobBytes;
        if (max <= 0) { throw new InvalidOperationException("this host cannot fetch from the blob store now: it is not connected to a relay that has one"); }
        if (h.Start.Size > max) { throw new ArgumentException($"the attachment is larger than the {max / (1024 * 1024)} MiB this host can fetch"); }
        var folder = Reserve(h);
        h.File = Path.Combine(folder, $"{Guid.NewGuid():N}.part");
        h.Fetching = new CancellationTokenSource();
        _ = Task.Run(() => FetchAsync(h, h.Fetching.Token));
    }

    async Task FetchAsync(Held h, CancellationToken ct)
    {
        var blob = h.Start.Blob!;
        var forget = false;
        try
        {
            await fetchSlots.WaitAsync(ct);
            try
            {
                await using (var file = PrivateFolder.CreateNew(h.File!))
                {
                    await blobs!.FetchAsync(blob, h.Start.Size, file, n => Interlocked.Exchange(ref h.Received, Math.Min(n, h.Start.Size)), ct);
                    if (file.Length != h.Start.Size) { throw new InvalidDataException("the blob held a different number of bytes than the attachment's size"); }
                }
                byte[] hash;
                await using (var read = File.OpenRead(h.File!)) { hash = await SHA256.HashDataAsync(read, ct); }
                if (!CryptographicOperations.FixedTimeEquals(hash, h.Sha256))
                {
                    forget = true;
                    Fail(h, "the attachment's bytes do not match its sha256, so it was dropped; send it again");
                    return;
                }
                lock (gate)
                {
                    if (h.Gone) { DeleteFile(h); return; }
                    h.Complete = true;
                    Interlocked.Exchange(ref h.Received, h.Start.Size);
                    h.Touched = Now;
                }
                forget = true;
                log($"fetched an attachment of {h.Start.Size / 1024.0:F0} KiB from the blob store");
            }
            finally { fetchSlots.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { DeleteFile(h); }
        catch (InvalidDataException e)
        {
            forget = true;
            Fail(h, $"the attachment in the blob store is not what was sent ({e.Message}), so it was dropped; send it again");
        }
        catch (Exception e)
        {
            log($"could not fetch an attachment from the blob store: {e.GetType().Name}: {e.Message}");
            Fail(h, $"the host could not fetch the attachment from the blob store ({e.Message}); send it again");
        }
        finally
        {
            // A blob the host has, or one that can never be right, is no use to anyone.
            if (forget)
            {
                try { await blobs!.ForgetAsync(blob); }
                catch (Exception e) when (e is not OperationCanceledException) { log($"could not delete a fetched blob from the store: {e.GetType().Name}"); }
            }
        }
    }

    void Fail(Held h, string why)
    {
        DeleteFile(h);
        lock (gate)
        {
            h.Failed = why;
            h.Touched = Now;
        }
    }

    public AttachmentState Chunk(string peer, AttachmentChunk chunk)
    {
        var (bytes, problem) = Attachments.Read(chunk);
        if (problem is not null) { throw new ArgumentException(problem); }
        var id = Guid.ParseExact(chunk.Attachment, "D");
        lock (gate)
        {
            Expire();
            var h = Find(peer, id) ?? throw new InvalidOperationException(Missing(chunk.Attachment));
            if (h.Start.Blob is not null) { throw new ArgumentException("that attachment comes from the blob store, not in chunks"); }
            h.Touched = Now;
            // A chunk sent again after a lost response is one the host already has.
            if (chunk.Offset + bytes!.Length <= h.Received) { return new AttachmentState(h.Received, h.Complete); }
            if (chunk.Offset != h.Received) { throw new ArgumentException($"expected the chunk at offset {h.Received}"); }
            if (h.Received + bytes.Length > h.Start.Size) { throw new ArgumentException("the chunk runs past the attachment's size"); }
            if (h.Writing is { } file)
            {
                try { file.Write(bytes); }
                catch (IOException e)
                {
                    Drop(h);
                    log($"could not write a large attachment: {e.GetType().Name}");
                    throw new InvalidOperationException($"the host could not write the attachment ({e.Message}), so it was dropped; send it again");
                }
                h.Hashing!.AppendData(bytes);
            }
            else { bytes.CopyTo(h.Bytes, h.Received); }
            h.Received += bytes.Length;
            if (h.Received < h.Start.Size) { return new AttachmentState(h.Received, false); }
            if (h.Writing is { } whole)
            {
                // Flushed here, where a full disk can still refuse it, not in a quiet Dispose.
                try { whole.Flush(); }
                catch (IOException e)
                {
                    Drop(h);
                    log($"could not write a large attachment: {e.GetType().Name}");
                    throw new InvalidOperationException($"the host could not write the attachment ({e.Message}), so it was dropped; send it again");
                }
            }
            var hash = h.Hashing?.GetHashAndReset() ?? SHA256.HashData(h.Bytes);
            CloseWriting(h);
            if (!CryptographicOperations.FixedTimeEquals(hash, h.Sha256))
            {
                Drop(h);
                throw new ArgumentException("the attachment's bytes do not match its sha256, so it was dropped; send it again");
            }
            h.Complete = true;
            return new AttachmentState(h.Received, true);
        }
    }

    public bool Discard(string peer, string attachment)
    {
        if (!Guid.TryParseExact(attachment, "D", out var id)) { return false; }
        lock (gate)
        {
            // One a reply is typing now is the reply's.
            if (Find(peer, id) is not { Claimed: false } h) { return false; }
            Drop(h);
            return true;
        }
    }

    // A reply's attachments, reserved for it until it is typed or not; or why it cannot have them.
    public (Claim? Claim, string? Problem) Claim(string peer, IReadOnlyList<AttachmentRef> refs)
    {
        lock (gate)
        {
            Expire();
            var taken = new List<Held>();
            foreach (var r in refs)
            {
                var id = Guid.ParseExact(r.Id, "D");
                var h = Find(peer, id);
                if (h is null)
                {
                    return (null, used.TryGetValue(id, out var u) && u.Peer == peer
                        ? $"attachment {Short(r.Id)} went with an earlier reply; attach it again"
                        : Missing(r.Id));
                }
                if (h.Failed is { } failed) { return (null, $"attachment {Short(r.Id)} could not be fetched: {failed}"); }
                if (!h.Complete) { return (null, $"attachment {Short(r.Id)} has not finished arriving; send it again"); }
                if (h.Claimed) { return (null, $"attachment {Short(r.Id)} is being sent with another reply"); }
                taken.Add(h);
            }
            foreach (var h in taken) { h.Claimed = true; }
            return (new Claim(taken), null);
        }
    }

    // Writes a claim's files into the staging folder, in the reply's order, and returns their
    // paths. Throws, leaving nothing written behind, when a file cannot be handed over or the
    // folder cannot be made private.
    public IReadOnlyList<string> Stage(Claim claim)
    {
        var names = new List<string>();
        foreach (var h in claim.Items)
        {
            var (name, problem) = AttachmentFiles.Name(h.Start.Name, h.File is null ? h.Bytes : Head(h.File));
            if (problem is not null) { throw new InvalidDataException(problem); }
            names.Add(name);
        }
        var root = PrivateFolder.Ensure(options.StagingRoot);
        var paths = new List<string>();
        try
        {
            for (var i = 0; i < names.Count; i++)
            {
                // Random, so a later send never overwrites a copy an agent has yet to read.
                var path = Path.Combine(root, $"{Guid.NewGuid():N}-{names[i]}");
                var item = claim.Items[i];
                // Added before writing, so a copy that fails part way is still deleted below.
                paths.Add(path);
                if (item.File is null) { PrivateFolder.WriteNew(path, item.Bytes); }
                else { PrivateFolder.CopyNew(item.File, path); }
            }
        }
        catch
        {
            Delete(paths);
            throw;
        }
        claim.Staged = paths;
        return paths;
    }

    // Enough of a file's start to tell its format.
    static byte[] Head(string path)
    {
        using var s = File.OpenRead(path);
        var b = new byte[64];
        var n = s.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
        return b[..n];
    }

    // The reply was typed: the attachments are the agent's now, and leave memory and incoming/.
    // Their staged copies stay until the sweep.
    public void Consume(Claim claim)
    {
        lock (gate)
        {
            foreach (var h in claim.Items)
            {
                if (held.Remove(h.Id)) { used[h.Id] = (h.Peer, h.Start, Now); }
                h.Gone = true;
                DeleteFile(h);
            }
        }
    }

    // Nothing was typed: the staged copies go now, and the attachments stay for another try.
    public void Release(Claim claim)
    {
        Delete(claim.Staged);
        claim.Staged = [];
        lock (gate)
        {
            foreach (var h in claim.Items)
            {
                h.Claimed = false;
                h.Touched = Now;
            }
        }
    }

    // Drops held attachments no reply used in time, and deletes staged copies past their time,
    // and files in incoming/ that no held attachment is using (all of them at start). Runs at
    // start, then every SweepEvery. Only files this store names are touched, and only in the
    // folders themselves: one replaced by a link is left alone, since the files it points at
    // were never staged here.
    public void Sweep()
    {
        lock (gate) { Expire(); }
        try
        {
            var root = new DirectoryInfo(options.StagingRoot);
            if (!root.Exists) { return; }
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                log($"left the attachment folder {options.StagingRoot} alone: it is a link");
                return;
            }
            var cutoff = Now - options.KeepStagedFor;
            var removed = 0;
            foreach (var file in root.EnumerateFiles())
            {
                if (!AttachmentFiles.IsStaged(file.Name) || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
                if (new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) >= cutoff) { continue; }
                try { file.Delete(); removed++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (removed > 0) { log($"removed {removed} staged attachment(s) older than {options.KeepStagedFor.TotalHours:0} h"); }

            var incoming = new DirectoryInfo(IncomingRoot);
            if (!incoming.Exists || incoming.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return; }
            var orphans = 0;
            foreach (var file in incoming.EnumerateFiles())
            {
                if (!IncomingName().IsMatch(file.Name) || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
                // Checked and deleted under the lock, against what is held now: a file an
                // attachment started after the sweep began is one it is using. Start names an
                // attachment's file under the lock before the file exists, so none is missed.
                lock (gate)
                {
                    if (held.Values.Any(h => h.File is { } path && string.Equals(Path.GetFileName(path), file.Name, StringComparison.Ordinal))) { continue; }
                    try { file.Delete(); orphans++; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            if (orphans > 0) { log($"removed {orphans} fetched attachment(s) no reply is waiting for"); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"could not clean up staged attachments: {e.Message}");
        }
    }

    DateTimeOffset Now => options.Time.GetUtcNow();

    Held? Find(string peer, Guid id) => held.TryGetValue(id, out var h) && h.Peer == peer ? h : null;

    void Expire()
    {
        var now = Now;
        foreach (var h in held.Values.Where(h => !h.Claimed && now - h.Touched > options.HoldFor).ToList()) { Drop(h); }
        foreach (var (id, _) in used.Where(p => now - p.Value.At > options.HoldFor).ToList()) { used.Remove(id); }
    }

    // Under the lock: lets an attachment go, stopping its fetch and deleting what it fetched.
    void Drop(Held h)
    {
        held.Remove(h.Id);
        h.Gone = true;
        h.Fetching?.Cancel();
        DeleteFile(h);
    }

    static void DeleteFile(Held h)
    {
        CloseWriting(h);
        if (h.File is null) { return; }
        try { File.Delete(h.File); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void CloseWriting(Held h)
    {
        try { h.Writing?.Dispose(); }
        catch (IOException) { }
        h.Writing = null;
        h.Hashing?.Dispose();
        h.Hashing = null;
    }

    static string Missing(string id) => $"attachment {Short(id)} is not on the host (it expired, was removed, or never arrived); attach it again";

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    static void Delete(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try { File.Delete(p); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex(@"^[0-9a-f]{32}\.part$")]
    private static partial Regex IncomingName();
}

// What a staged file is called. Copilot decides how to read an attachment from its extension,
// so a recognised image or PDF gets the extension its bytes call for, and a name claiming one
// of those formats over bytes that are not is refused rather than handed over mislabelled. The
// rest is agent-ha-bridge's Get-BridgeAttachmentFileName: the name arrives from a browser, so
// anything but letters, digits, dot, dash and underscore becomes a dash (no separators, and no
// spaces, which would split the @ mention), the stem is cut to 48 characters, and only a short
// alphanumeric extension is kept.
public static partial class AttachmentFiles
{
    public sealed record Format(string Kind, string[] Extensions);

    static readonly Format Png = new("PNG image", [".png"]);
    static readonly Format Jpeg = new("JPEG image", [".jpg", ".jpeg"]);
    static readonly Format Gif = new("GIF image", [".gif"]);
    static readonly Format Webp = new("WebP image", [".webp"]);
    static readonly Format Heic = new("HEIC image", [".heic", ".heif"]);
    static readonly Format Heif = new("HEIF image", [".heif", ".heic"]);
    static readonly Format Pdf = new("PDF", [".pdf"]);
    static readonly Format[] Known = [Png, Jpeg, Gif, Webp, Heic, Heif, Pdf];
    static readonly string[] HeicBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis"];

    public static Format? Detect(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) { return Png; }
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) { return Jpeg; }
        if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8)) { return Gif; }
        if (b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) { return Webp; }
        if (b.StartsWith("%PDF-"u8)) { return Pdf; }
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = System.Text.Encoding.ASCII.GetString(b[8..12]);
            if (HeicBrands.Contains(brand)) { return Heic; }
            if (brand is "mif1" or "msf1") { return Heif; }
        }
        return null;
    }

    // The staged file's name (without the random prefix), or why the file cannot be handed over.
    public static (string Name, string? Problem) Name(string given, ReadOnlySpan<byte> bytes)
    {
        var leaf = LastSegment().Replace(given, "");
        var extension = Path.GetExtension(leaf);
        if (!ShortExtension().IsMatch(extension)) { extension = ""; }
        extension = extension.ToLowerInvariant();
        var stem = Unsafe().Replace(Path.GetFileNameWithoutExtension(leaf), "-").Trim('-', '.');
        if (stem.Length > 48) { stem = stem[..48]; }
        if (stem.Length == 0) { stem = "attachment"; }

        var format = Detect(bytes);
        if (format is null)
        {
            if (Known.FirstOrDefault(k => k.Extensions.Contains(extension)) is { } claimed)
            {
                return ("", $"a file named {extension} that is not a {claimed.Kind} is not sent; attach it under another name");
            }
        }
        else if (!format.Extensions.Contains(extension))
        {
            extension = format.Extensions[0];
        }
        return (stem + extension, null);
    }

    // Whether a file name is one Stage gives: 32 hex digits, a dash, then the safe name.
    public static bool IsStaged(string fileName) => StagedName().IsMatch(fileName);

    [GeneratedRegex(@"^[0-9a-f]{32}-[0-9A-Za-z._-]+$")]
    private static partial Regex StagedName();

    [GeneratedRegex(@".*[\\/]", RegexOptions.Singleline)]
    private static partial Regex LastSegment();

    [GeneratedRegex(@"^\.[0-9A-Za-z]{1,12}$")]
    private static partial Regex ShortExtension();

    [GeneratedRegex(@"[^0-9A-Za-z._-]")]
    private static partial Regex Unsafe();
}
