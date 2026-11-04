using System.Security.Cryptography;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Conversation;

// Turn and part ids (docs/protocol/v1-history.md): made from the transcript record a turn or part
// starts at, so the same record gives the same id after the host restarts and as the transcript
// grows, and the live parser and the history reader agree on a turn's id without sharing state.
// A record never changes once written, so its bytes are its identity.
internal static class TurnIds
{
    // 12 bytes of SHA-256 is 16 characters, unique enough among one session's records.
    public const int Bytes = 12;

    public static string Of(ReadOnlySpan<byte> line) => Attachments.Base64Url(Raw(line));

    public static byte[] Raw(ReadOnlySpan<byte> line)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(line, hash);
        return hash[..Bytes].ToArray();
    }

    // A record too long to read whole (an unreadable turn's): its start and its length, which no
    // other record of the transcript shares.
    public static byte[] RawOfLong(ReadOnlySpan<byte> head, long length)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(head);
        Span<byte> len = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(len, length);
        sha.AppendData(len);
        return sha.GetHashAndReset()[..Bytes];
    }

    public static string Part(string record, int index) => $"{record}.{index}";
}
