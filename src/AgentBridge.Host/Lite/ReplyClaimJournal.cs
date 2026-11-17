using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Migration;

namespace AgentBridge.Host.Lite;

internal sealed record ReplyClaimRecord(string Session, string RequestHash, string TextHash, string? AttachmentHash, Guid Generation, DateTimeOffset FirstClaimUtc);
internal sealed record ReplyClaimSnapshot(List<ReplyClaimRecord> Claims, long Sequence, string Hash);
internal sealed record ReplyClaimMarker(int Schema, string ProfileKey, string State, long Sequence, string Hash);

internal static class ReplyClaimJournal
{
    const int Version = 1, ChecksumSize = 32, MaxFrame = 4 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static readonly AsyncLocal<Action?> BeforeCompactReplace = new(), AfterCompactReplace = new();

    sealed record Row(int Version, string ProfileKey, long Sequence, string PreviousHash, string Action,
        ReplyClaimRecord? Claim = null, List<ReplyClaimRecord>? Claims = null, long CoverageSequence = -1, string? CoverageHash = null);

    internal static string Genesis(string profileKey) => Hash(Encoding.UTF8.GetBytes($"reply-claims-v{Version}:{profileKey}"));

    internal static ReplyClaimSnapshot Read(string path, string profileKey, ReplyClaimMarker marker, bool repairTail)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException e) { throw new InvalidDataException("The reply-claim journal is missing; delivery remains held.", e); }
        var claims = new Dictionary<(string, string), ReplyClaimRecord>();
        var proofs = new Dictionary<long, string>();
        var offset = 0;
        var sequence = 0L;
        var tip = Genesis(profileKey);
        proofs[0] = tip;
        var first = true;
        while (offset < bytes.Length)
        {
            var start = offset;
            if (bytes.Length - offset < 4) { Truncate(path, bytes, start, repairTail); break; }
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            if (length is < 2 or > MaxFrame) { throw Corrupt(); }
            offset += 4;
            if (bytes.Length - offset < length + ChecksumSize) { Truncate(path, bytes, start, repairTail); break; }
            var payload = bytes.AsSpan(offset, length);
            var digest = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(digest, bytes.AsSpan(offset + length, ChecksumSize))) { throw Corrupt(); }
            Row row;
            try { row = JsonSerializer.Deserialize<Row>(payload, Json) ?? throw Corrupt(); }
            catch (JsonException e) { throw new InvalidDataException("The reply-claim journal contains an invalid row.", e); }
            if (row.Version != Version || row.ProfileKey != profileKey || row.Sequence <= 0 || !IsHash(row.PreviousHash)) { throw Corrupt(); }
            var rowHash = Convert.ToHexString(digest);
            if (first && row.Action == "snapshot")
            {
                if (row.Claim is not null || row.CoverageSequence < 0 || row.CoverageHash is null || !IsHash(row.CoverageHash)
                    || row.Sequence != row.CoverageSequence + 1 || row.PreviousHash != row.CoverageHash) { throw Corrupt(); }
                proofs[row.CoverageSequence] = row.CoverageHash;
                ApplySnapshot(claims, row.Claims);
            }
            else
            {
                if (row.Action == "snapshot" || row.Claims is not null || row.CoverageSequence != -1 || row.CoverageHash is not null
                    || row.Sequence != sequence + 1 || row.PreviousHash != tip) { throw Corrupt(); }
                Apply(claims, row);
            }
            sequence = row.Sequence;
            tip = rowHash;
            proofs[sequence] = tip;
            first = false;
            offset += length + ChecksumSize;
        }
        if (sequence < marker.Sequence || !proofs.TryGetValue(marker.Sequence, out var proof) || proof != marker.Hash)
        { throw new InvalidDataException("The reply-claim journal cannot prove the marker's high-water point."); }
        return new(claims.Values.ToList(), sequence, tip);
    }

    internal static ReplyClaimSnapshot Append(string path, string profileKey, ReplyClaimSnapshot state, string action, ReplyClaimRecord claim)
    {
        var row = new Row(Version, profileKey, state.Sequence + 1, state.Hash, action, claim);
        var bytes = Frame(JsonSerializer.SerializeToUtf8Bytes(row, Json));
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        { stream.Write(bytes); stream.Flush(true); }
        return new(state.Claims, row.Sequence, Hash(bytes.AsSpan(4, bytes.Length - 4 - ChecksumSize)));
    }

    internal static ReplyClaimSnapshot Compact(string path, string profileKey, ReplyClaimSnapshot state, ReplyClaimMarker marker)
    {
        var row = new Row(Version, profileKey, state.Sequence + 1, state.Hash, "snapshot",
            Claims: state.Claims, CoverageSequence: state.Sequence, CoverageHash: state.Hash);
        var frame = Frame(JsonSerializer.SerializeToUtf8Bytes(row, Json));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = path + ".bak";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(frame); stream.Flush(true); }
            var verified = Read(temp, profileKey, marker, repairTail: false);
            if (verified.Sequence != row.Sequence || verified.Hash != Hash(frame.AsSpan(4, frame.Length - 4 - ChecksumSize)))
            { throw Corrupt(); }
            BeforeCompactReplace.Value?.Invoke();
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            ClaimDirectorySync.Flush(Path.GetDirectoryName(path)!);
            AfterCompactReplace.Value?.Invoke();
        }
        finally { File.Delete(temp); }
        return new(state.Claims, row.Sequence, Hash(frame.AsSpan(4, frame.Length - 4 - ChecksumSize)));
    }

    internal static void Recover(string path, string backup, string profileKey, ReplyClaimMarker marker)
    {
        var state = Read(backup, profileKey, marker, repairTail: false);
        if (File.Exists(path)) { throw new IOException("Preserve the current reply-claim journal before operator recovery."); }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(backup, temp);
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { stream.Flush(true); }
            Read(temp, profileKey, marker, repairTail: false);
            File.Move(temp, path);
            ClaimDirectorySync.Flush(Path.GetDirectoryName(path)!);
        }
        finally { File.Delete(temp); }
    }

    static void Apply(Dictionary<(string, string), ReplyClaimRecord> claims, Row row)
    {
        if (row.Action == "claimed")
        {
            var claim = row.Claim ?? throw Corrupt();
            Validate(claim);
            if (!claims.TryAdd((claim.Session, claim.RequestHash.ToUpperInvariant()), claim)) { throw Corrupt(); }
            if (claims.Values.Count(c => c.Session == claim.Session) > 4096) { throw Corrupt(); }
            return;
        }
        if (row.Action is not ("released" or "expired") || row.Claim is null) { throw Corrupt(); }
        Validate(row.Claim);
        var key = (row.Claim.Session, row.Claim.RequestHash.ToUpperInvariant());
        if (!claims.TryGetValue(key, out var current) || current.Generation != row.Claim.Generation) { throw Corrupt(); }
        claims.Remove(key);
    }

    static void ApplySnapshot(Dictionary<(string, string), ReplyClaimRecord> claims, List<ReplyClaimRecord>? snapshot)
    {
        if (snapshot is null) { throw Corrupt(); }
        foreach (var claim in snapshot)
        {
            Validate(claim);
            if (!claims.TryAdd((claim.Session, claim.RequestHash.ToUpperInvariant()), claim)
                || claims.Values.Count(c => c.Session == claim.Session) > 4096) { throw Corrupt(); }
        }
    }

    static void Validate(ReplyClaimRecord? claim)
    {
        if (claim is null || string.IsNullOrEmpty(claim.Session) || !IsHash(claim.RequestHash) || !IsHash(claim.TextHash)
            || claim.AttachmentHash is not null && !IsHash(claim.AttachmentHash) || claim.Generation == Guid.Empty
            || claim.FirstClaimUtc == default || claim.FirstClaimUtc.Offset != TimeSpan.Zero) { throw Corrupt(); }
    }

    static void Truncate(string path, byte[] bytes, int length, bool repair)
    {
        if (!repair) { throw Corrupt(); }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
        stream.Flush(true);
    }

    static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length + ChecksumSize];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        SHA256.HashData(payload).CopyTo(frame.AsSpan(4 + payload.Length));
        return frame;
    }

    static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    static InvalidDataException Corrupt() => new("The reply-claim journal is corrupt; delivery remains held.");
}
