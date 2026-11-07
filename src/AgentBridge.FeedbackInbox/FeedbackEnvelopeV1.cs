using System.Buffers.Binary;
using System.Text;

namespace AgentBridge.FeedbackInbox;

public sealed record FeedbackEnvelopeV1(
    string KeyId,
    ReadOnlyMemory<byte> BundleId,
    ReadOnlyMemory<byte> EphemeralPublicKey,
    ReadOnlyMemory<byte> Salt,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Ciphertext)
{
    const int MaxStoredBytes = 8 * 1024 * 1024;
    const int MinStoredBytes = 4096;
    const int MiB = 1024 * 1024;

    public static FeedbackEnvelopeV1 Parse(ReadOnlyMemory<byte> bytes)
    {
        var data = bytes.Span;
        if (data.Length < MinStoredBytes || data.Length > MaxStoredBytes
            || !data[..4].SequenceEqual("SGBF"u8) || data[4] != 1)
        {
            throw new FeedbackEnvelopeException("invalid-envelope");
        }

        var keyLength = data[5];
        if (keyLength is < 1 or > 64)
        {
            throw new FeedbackEnvelopeException("invalid-envelope");
        }
        var at = 6;
        var key = data.Slice(at, keyLength);
        foreach (var b in key)
        {
            if (!((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z')
                || (b >= '0' && b <= '9') || b is (byte)'.' or (byte)'_' or (byte)'-'))
            {
                throw new FeedbackEnvelopeException("invalid-envelope");
            }
        }
        at += keyLength;
        var id = bytes.Slice(at, 16); at += 16;
        var ephemeral = bytes.Slice(at, 32); at += 32;
        var salt = bytes.Slice(at, 32); at += 32;
        var nonce = bytes.Slice(at, 12); at += 12;
        var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at, 4)); at += 4;
        if (length < 16 || length > data.Length - at)
        {
            throw new FeedbackEnvelopeException("invalid-envelope");
        }
        var end = at + (int)length;
        if (StoredSize(end) != data.Length || data[end..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new FeedbackEnvelopeException("invalid-envelope");
        }
        return new FeedbackEnvelopeV1(
            Encoding.ASCII.GetString(key), id, ephemeral, salt, nonce, bytes.Slice(at, (int)length));
    }

    static int StoredSize(int length)
    {
        if (length <= MinStoredBytes) { return MinStoredBytes; }
        if (length > MiB) { return ((length + MiB - 1) / MiB) * MiB; }
        var bucket = MinStoredBytes;
        while (bucket < length) { bucket *= 2; }
        return bucket;
    }
}
