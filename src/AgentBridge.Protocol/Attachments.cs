// Bridge Protocol v1, attachments: files and images sent with a session.reply. The contract
// is docs/protocol/v1-sessions.md, "Attachments"; the PWA mirrors these checks in
// pwa/js/attachments.js.
using System.Text;

namespace AgentBridge.Protocol;

// attachment.start: the client's id for one file, what it is, and how to know it arrived
// whole. Starting again with the same id and the same details resumes it.
public sealed record AttachmentStart
{
    public required string Attachment { get; init; }
    public required string Name { get; init; }
    // The MIME type the client gave the file, if any. Informational: the host decides how to
    // stage a file from its bytes and its name, never from this.
    public string? Type { get; init; }
    public required long Size { get; init; }
    // The SHA-256 of the whole file, base64url without padding (43 characters).
    public required string Sha256 { get; init; }
    // Set for a file in the relay's blob store rather than sent in chunks: the host fetches,
    // opens and checks it itself (docs/protocol/blobs.md).
    public BlobRef? Blob { get; init; }
}

// A file sealed into the relay's blob store for this host: the relay's ticket for it and the
// key it is sealed with. The key exists only here, inside the end-to-end channel.
public sealed record BlobRef
{
    public required string Id { get; init; }
    // 32 bytes, base64url without padding (43 characters).
    public required string Key { get; init; }
}

// attachment.chunk: the next bytes, standard base64, at the offset the host has reached.
public sealed record AttachmentChunk
{
    public required string Attachment { get; init; }
    public required long Offset { get; init; }
    public required string Data { get; init; }
}

public sealed record AttachmentDiscard
{
    public required string Attachment { get; init; }
}

// The result of attachment.start and attachment.chunk: how many bytes the host has, and
// whether they are the whole file and match its sha256.
public sealed record AttachmentState(long Received, bool Complete);

public sealed record AttachmentDiscarded(bool Discarded);

// One attachment named by a session.reply. However it reached the host (in this channel, or
// through the blob store), a reply only ever refers to it by id.
public sealed record AttachmentRef
{
    public required string Id { get; init; }
}

public static class AttachmentLimits
{
    // One file through the channel. Larger ones go through the blob store.
    public const int Bytes = 256 * 1024;
    // One file through the blob store (design §9: images and files up to 100 MB).
    public const long BlobBytes = 100L * 1024 * 1024;
    // One file in chunks through a direct channel (the app on the machine itself, over the
    // local pipe), where the 256 KiB cap that keeps the relay's bulk lane cheap means nothing.
    public const long DirectBytes = BlobBytes;
    // A relay's ticket, as the host passes it back to the relay.
    public const int BlobId = 512;
    // Decoded bytes in one attachment.chunk: its request is under 16 KiB as sent, so a chunk
    // never holds an interactive frame back by more than one small record.
    public const int Chunk = 11 * 1024;
    public const int ChunkRequest = 16 * 1024;
    public const int PerReply = 10;
    public const int Name = 255;
    public const int Type = 127;
}

public static class Attachments
{
    // Null when the start can be taken; otherwise why not. The host fails the request with it.
    // inChannel is the most this channel takes in chunks: Bytes, or DirectBytes on a direct one.
    public static string? Check(AttachmentStart start, long inChannel = AttachmentLimits.Bytes)
    {
        if (!IsId(start.Attachment)) { return "attachment must be a UUID"; }
        if (string.IsNullOrWhiteSpace(start.Name)) { return "the attachment needs a name"; }
        if (Encoding.UTF8.GetByteCount(start.Name) > AttachmentLimits.Name) { return $"the attachment's name is longer than {AttachmentLimits.Name} bytes"; }
        if (start.Type is { } t && Encoding.UTF8.GetByteCount(t) > AttachmentLimits.Type) { return $"the attachment's type is longer than {AttachmentLimits.Type} bytes"; }
        if (start.Size <= 0) { return "the attachment is empty"; }
        if (start.Blob is null && start.Size > inChannel) { return $"the attachment is larger than {(inChannel >= 1024 * 1024 ? $"{inChannel / (1024 * 1024)} MiB" : $"{inChannel / 1024} KiB")}"; }
        if (start.Blob is { } blob)
        {
            if (start.Size > AttachmentLimits.BlobBytes) { return $"the attachment is larger than {AttachmentLimits.BlobBytes / (1024 * 1024)} MiB"; }
            if (blob.Id is not { Length: > 0 and <= AttachmentLimits.BlobId } || blob.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) { return "blob.id must be the relay's ticket for the blob"; }
            if (Sha256(blob.Key) is null) { return "blob.key must be 32 bytes, base64url without padding"; }
        }
        if (Sha256(start.Sha256) is null) { return "sha256 must be 32 bytes, base64url without padding"; }
        return null;
    }

    // The decoded bytes of a chunk, or why it cannot be read.
    public static (byte[]? Bytes, string? Problem) Read(AttachmentChunk chunk)
    {
        if (!IsId(chunk.Attachment)) { return (null, "attachment must be a UUID"); }
        // The attachment's own size is the host's to check; this only bounds any channel's.
        if (chunk.Offset < 0 || chunk.Offset >= AttachmentLimits.DirectBytes) { return (null, "the offset is outside the attachment"); }
        if (chunk.Data is null) { return (null, "the chunk is empty"); }
        // Checked before decoding, so an oversized chunk costs nothing.
        if (chunk.Data.Length > (AttachmentLimits.Chunk + 2) / 3 * 4) { return (null, $"a chunk carries at most {AttachmentLimits.Chunk / 1024} KiB"); }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(chunk.Data); }
        catch (FormatException) { return (null, "the chunk's data is not base64"); }
        if (bytes.Length == 0) { return (null, "the chunk is empty"); }
        if (bytes.Length > AttachmentLimits.Chunk) { return (null, $"a chunk carries at most {AttachmentLimits.Chunk / 1024} KiB"); }
        return (bytes, null);
    }

    // The 32 bytes of a base64url SHA-256, or null if it is not one.
    public static byte[]? Sha256(string? value)
    {
        if (value is not { Length: 43 } || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) { return null; }
        var standard = value.Replace('-', '+').Replace('_', '/') + "=";
        try { return Convert.FromBase64String(standard) is { Length: 32 } b ? b : null; }
        catch (FormatException) { return null; }
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsId(string? id) => Guid.TryParseExact(id, "D", out _);
}
