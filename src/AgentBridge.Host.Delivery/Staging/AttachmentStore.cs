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

    // How many attachments the host holds in memory at once, across devices: 32 of 256 KiB
    // is 8 MiB at most.
    public int MaxHeld { get; init; } = 32;

    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromMinutes(10);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// One attachment as it arrives and until a reply uses it.
internal sealed class Held(string peer, AttachmentStart start, byte[] sha256)
{
    public readonly string Peer = peer;
    public readonly AttachmentStart Start = start;
    public readonly Guid Id = Guid.ParseExact(start.Attachment, "D");
    public readonly byte[] Sha256 = sha256;
    public readonly byte[] Bytes = new byte[start.Size];
    public long Received;
    public bool Complete;
    public bool Claimed;
    public DateTimeOffset Touched;

    public bool Same(AttachmentStart other) => Same(Start, other);

    public static bool Same(AttachmentStart a, AttachmentStart b) =>
        a.Size == b.Size && a.Name == b.Name && string.Equals(a.Sha256, b.Sha256, StringComparison.Ordinal);
}

// Attachments reserved for one reply, until it is typed or not.
public sealed class Claim
{
    internal Claim(IReadOnlyList<Held> items) => Items = items;

    internal IReadOnlyList<Held> Items { get; }

    internal IReadOnlyList<string> Staged { get; set; } = [];

    public int Count => Items.Count;

    public long Bytes => Items.Sum(i => (long)i.Bytes.Length);
}

// Attachments sent with replies, from the first chunk until the agent has the file:
//   * Held in memory while they arrive and until a reply uses them, for the device that sent
//     them only, each checked against its size and SHA-256 when its last byte arrives.
//   * Staged, when a reply is about to be typed, into a folder only this user can read, with
//     a random name and the extension the file's own bytes call for, since that is what
//     Copilot goes by.
//   * Once typed, gone from memory; the staged copy is deleted after KeepStagedFor. A reply
//     that types nothing deletes its staged copies at once and leaves the attachments held,
//     so the person can send again.
// Names and contents never reach the log.
public sealed class AttachmentStore : IDisposable
{
    readonly AttachmentOptions options;
    readonly Action<string> log;
    readonly Lock gate = new();
    readonly Dictionary<Guid, Held> held = [];
    // Attachments a typed reply has used: starting one again (a client resending after a lost
    // response) says it is complete instead of taking every byte again for nothing.
    readonly Dictionary<Guid, (string Peer, AttachmentStart Start, DateTimeOffset At)> used = [];
    readonly ITimer? sweeper;

    public AttachmentStore(AttachmentOptions options, Action<string>? log = null, bool sweep = true)
    {
        this.options = options;
        this.log = log ?? (_ => { });
        if (sweep)
        {
            Sweep();
            sweeper = options.Time.CreateTimer(_ => Sweep(), null, options.SweepEvery, options.SweepEvery);
        }
    }

    public string StagingRoot => options.StagingRoot;

    // How many attachments are in memory now.
    public int Count { get { lock (gate) { return held.Count; } } }

    public void Dispose() => sweeper?.Dispose();

    public AttachmentState Start(string peer, AttachmentStart start)
    {
        if (Attachments.Check(start) is { } problem) { throw new ArgumentException(problem); }
        var id = Guid.ParseExact(start.Attachment, "D");
        lock (gate)
        {
            Expire();
            if (held.TryGetValue(id, out var h) && h.Peer == peer)
            {
                if (!h.Same(start)) { throw new ArgumentException("that attachment was started with a different name, size or sha256"); }
                h.Touched = Now;
                return new AttachmentState(h.Received, h.Complete);
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
            held[id] = new Held(peer, start, Attachments.Sha256(start.Sha256)!) { Touched = Now };
            return new AttachmentState(0, false);
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
            h.Touched = Now;
            // A chunk sent again after a lost response is one the host already has.
            if (chunk.Offset + bytes!.Length <= h.Received) { return new AttachmentState(h.Received, h.Complete); }
            if (chunk.Offset != h.Received) { throw new ArgumentException($"expected the chunk at offset {h.Received}"); }
            if (h.Received + bytes.Length > h.Bytes.Length) { throw new ArgumentException("the chunk runs past the attachment's size"); }
            bytes.CopyTo(h.Bytes, h.Received);
            h.Received += bytes.Length;
            if (h.Received < h.Bytes.Length) { return new AttachmentState(h.Received, false); }
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(h.Bytes), h.Sha256))
            {
                held.Remove(id);
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
            if (Find(peer, id) is not { Claimed: false }) { return false; }
            return held.Remove(id);
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
            var (name, problem) = AttachmentFiles.Name(h.Start.Name, h.Bytes);
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
                PrivateFolder.WriteNew(path, claim.Items[i].Bytes);
                paths.Add(path);
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

    // The reply was typed: the attachments are the agent's now, and leave memory. Their staged
    // copies stay until the sweep.
    public void Consume(Claim claim)
    {
        lock (gate)
        {
            foreach (var h in claim.Items)
            {
                if (held.Remove(h.Id)) { used[h.Id] = (h.Peer, h.Start, Now); }
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

    // Drops held attachments no reply used in time, and deletes staged copies past their time.
    // Runs at start, then every SweepEvery. Only files this store names (a 32-hex prefix and a
    // dash) are touched, and only in the folder itself: if it has been replaced by a link, the
    // files it points at were never staged here.
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
        foreach (var (id, _) in held.Where(p => !p.Value.Claimed && now - p.Value.Touched > options.HoldFor).ToList()) { held.Remove(id); }
        foreach (var (id, _) in used.Where(p => now - p.Value.At > options.HoldFor).ToList()) { used.Remove(id); }
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
