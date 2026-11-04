using System.IO.Compression;

namespace AgentBridge.Transport;

// Per-message deflate inside a channel (design §5.3, #603): compress, then encrypt, each message on
// its own with no dictionary shared across messages, so text an attacker chose never shares a
// compression window with another message's secrets. Session JSON halves, or better. Sent only to
// a peer whose introduction said it inflates ("deflate": true), so an older peer gets JSON as it
// always did.
//
// A transport message's plaintext is JSON, which always starts with '{'. A deflated one starts with
// Marker instead, then raw DEFLATE (RFC 1951, no zlib header: WebCrypto's peers use
// CompressionStream("deflate-raw")).
public static class MessageDeflate
{
    public const byte Marker = 0x01;
    // Below this a message goes as it is: deflate would save a few bytes at best.
    public const int MinBytes = 512;

    // The message to send: deflated, or as it is where that is not smaller.
    public static byte[] Pack(byte[] plain)
    {
        if (plain.Length < MinBytes) { return plain; }
        using var packed = new MemoryStream(plain.Length / 2 + 16);
        packed.WriteByte(Marker);
        using (var z = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true)) { z.Write(plain); }
        return packed.Length < plain.Length ? packed.ToArray() : plain;
    }

    // The JSON a received message carries. A deflated one may inflate to at most max bytes, what a
    // sender may send uncompressed, so a small message cannot unpack into a large one (a bomb).
    // Corrupt or oversized, it is a FormatException, which ends the channel (ChannelHub.EndsChannel).
    public static byte[] Unpack(byte[] message, int max)
    {
        if (message.Length == 0 || message[0] != Marker) { return message; }
        try
        {
            using var z = new DeflateStream(new MemoryStream(message, 1, message.Length - 1), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = z.Read(chunk)) > 0)
            {
                if (output.Length + n > max) { throw new FormatException($"a deflated message inflates to more than {max} bytes"); }
                output.Write(chunk, 0, n);
            }
            return output.ToArray();
        }
        catch (InvalidDataException e) { throw new FormatException("a deflated message is corrupt", e); }
    }
}
