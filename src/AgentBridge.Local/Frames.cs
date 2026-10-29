using System.Buffers.Binary;

namespace AgentBridge.Local;

// One message on the local pipe: a 4-byte little-endian length, then that many bytes of UTF-8
// JSON. At most one Noise transport message's worth, so everything the host sends fits the
// same budget whichever way a client reaches it, and a response too large for the relay is
// too large here too.
public static class Frames
{
    public const int MaxMessageBytes = AgentBridge.Transport.SecureChannel.MaxMessageBytes;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        if (message.Length > MaxMessageBytes) { throw new AgentBridge.Transport.MessageTooLargeException(message.Length); }
        var frame = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)message.Length);
        message.CopyTo(frame.AsMemory(4));
        // One write per frame, so two frames can never interleave on a stream shared by
        // writers that each hold the send lock only for their own frame.
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    // Null at a clean end of stream (between frames). A stream that ends mid-frame, or a
    // length over the limit, is a broken peer: the connection is over.
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        var got = await ReadFullyAsync(stream, header, ct);
        if (got == 0) { return null; }
        if (got < 4) { throw new EndOfStreamException("the pipe closed inside a frame header"); }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxMessageBytes) { throw new InvalidDataException($"a {length}-byte frame is over the {MaxMessageBytes}-byte limit"); }
        var body = new byte[length];
        if (await ReadFullyAsync(stream, body, ct) < length) { throw new EndOfStreamException("the pipe closed inside a frame"); }
        return body;
    }

    static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var at = 0;
        while (at < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(at), ct);
            if (n == 0) { break; }
            at += n;
        }
        return at;
    }
}
