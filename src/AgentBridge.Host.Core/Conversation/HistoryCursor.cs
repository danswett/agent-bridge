using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Conversation;

// session.history's before (docs/protocol/v1-history.md, The cursor): where the oldest turn of a
// page starts, its id, how many turns had been served by then, the transcript's fingerprint and the
// session, sealed with a MAC under a key the host keeps, so a client can neither read a path from
// it nor make one that starts anywhere but at a turn it could page to, or skips the 500-turn limit.
// The key outlives the host's restarts (HistoryKey), so cursors do too.
internal sealed record HistoryCursor(long Offset, int Depth, byte[] TurnId, byte[] Fingerprint, byte[] Session)
{
    const string Prefix = "h1.";
    const byte Version = 1;
    const int FingerprintBytes = 8;
    const int SessionBytes = 8;
    const int MacBytes = 16;
    const int PayloadBytes = 1 + 8 + 2 + TurnIds.Bytes + FingerprintBytes + SessionBytes;

    public static byte[] SessionOf(string id) => SHA256.HashData(Encoding.UTF8.GetBytes(id))[..SessionBytes];

    // The transcript's first record (or what there is of its first 4 KiB), which never changes once
    // written: another transcript in its place gives another.
    public static byte[] FingerprintOf(ReadOnlySpan<byte> head) => SHA256.HashData(head)[..FingerprintBytes];

    public string Seal(byte[] key)
    {
        var payload = new byte[PayloadBytes];
        var p = payload.AsSpan();
        p[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(p[1..], Offset);
        BinaryPrimitives.WriteUInt16BigEndian(p[9..], (ushort)Depth);
        TurnId.CopyTo(p[11..]);
        Fingerprint.CopyTo(p[(11 + TurnIds.Bytes)..]);
        Session.CopyTo(p[(11 + TurnIds.Bytes + FingerprintBytes)..]);
        var mac = HMACSHA256.HashData(key, payload)[..MacBytes];
        return Prefix + Attachments.Base64Url([.. payload, .. mac]);
    }

    // The cursor a key sealed, or null for anything else: not one of this form, cut, changed, or
    // sealed under another key. Whether it still fits the transcript is the reader's to check.
    public static HistoryCursor? Open(string text, byte[] key)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) { return null; }
        var bytes = FromBase64Url(text[Prefix.Length..]);
        if (bytes is not { Length: PayloadBytes + MacBytes }) { return null; }
        var payload = bytes.AsSpan(0, PayloadBytes);
        var mac = HMACSHA256.HashData(key, payload)[..MacBytes];
        if (!CryptographicOperations.FixedTimeEquals(mac, bytes.AsSpan(PayloadBytes))) { return null; }
        if (payload[0] != Version) { return null; }
        var offset = BinaryPrimitives.ReadInt64BigEndian(payload[1..]);
        if (offset < 0) { return null; }
        return new HistoryCursor(
            offset,
            BinaryPrimitives.ReadUInt16BigEndian(payload[9..]),
            payload.Slice(11, TurnIds.Bytes).ToArray(),
            payload.Slice(11 + TurnIds.Bytes, FingerprintBytes).ToArray(),
            payload.Slice(11 + TurnIds.Bytes + FingerprintBytes, SessionBytes).ToArray());
    }

    static byte[]? FromBase64Url(string s)
    {
        if (s.Length is 0 or > HistoryLimits.MaxCursorBytes) { return null; }
        foreach (var c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) { return null; }
        }
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "!" };
        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { return null; }
    }
}

// The key cursors are sealed under: 32 random bytes in the host's profile folder, made the first
// time and read after, so a cursor a client holds stays good across the host's restarts. Without a
// folder (a test, a one-off command), or one it cannot use, a key for this run only: cursors from
// before a restart then read as stale, which a client recovers from by asking again.
internal static class HistoryKey
{
    const int Bytes = 32;

    public static byte[] Load(string? path, Action<string>? log = null)
    {
        if (path is null) { return RandomNumberGenerator.GetBytes(Bytes); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    var key = File.ReadAllBytes(path);
                    if (key.Length == Bytes) { return key; }
                    log?.Invoke("the history cursor key is not 32 bytes; cursors from before this run will be stale");
                    return RandomNumberGenerator.GetBytes(Bytes);
                }
                var made = RandomNumberGenerator.GetBytes(Bytes);
                // CreateNew: two hosts racing each make one, and the loser reads the winner's.
                using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    f.Write(made);
                    f.Flush(flushToDisk: true);
                }
                return made;
            }
            catch (IOException) when (attempt == 0 && File.Exists(path)) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"could not keep the history cursor key ({e.GetType().Name}); cursors from before a restart will be stale");
                return RandomNumberGenerator.GetBytes(Bytes);
            }
        }
        return RandomNumberGenerator.GetBytes(Bytes);
    }
}
