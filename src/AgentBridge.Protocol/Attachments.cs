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

// One attachment named by a session.reply. However it reached the host (in this channel
// now, through the blob store later), a reply only ever refers to it by id.
public sealed record AttachmentRef
{
    public required string Id { get; init; }
}

public static class AttachmentLimits
{
    // One file through the channel. Larger ones wait for the blob store.
    public const int Bytes = 256 * 1024;
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
    public static string? Check(AttachmentStart start)
    {
        if (!IsId(start.Attachment)) { return "attachment must be a UUID"; }
        if (string.IsNullOrWhiteSpace(start.Name)) { return "the attachment needs a name"; }
        if (Encoding.UTF8.GetByteCount(start.Name) > AttachmentLimits.Name) { return $"the attachment's name is longer than {AttachmentLimits.Name} bytes"; }
        if (start.Type is { } t && Encoding.UTF8.GetByteCount(t) > AttachmentLimits.Type) { return $"the attachment's type is longer than {AttachmentLimits.Type} bytes"; }
        if (start.Size <= 0) { return "the attachment is empty"; }
        if (start.Size > AttachmentLimits.Bytes) { return $"the attachment is larger than {AttachmentLimits.Bytes / 1024} KiB"; }
        if (Sha256(start.Sha256) is null) { return "sha256 must be 32 bytes, base64url without padding"; }
        return null;
    }

    // The decoded bytes of a chunk, or why it cannot be read.
    public static (byte[]? Bytes, string? Problem) Read(AttachmentChunk chunk)
    {
        if (!IsId(chunk.Attachment)) { return (null, "attachment must be a UUID"); }
        if (chunk.Offset < 0 || chunk.Offset >= AttachmentLimits.Bytes) { return (null, "the offset is outside the attachment"); }
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
