using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Migration;

namespace AgentBridge.Host.Lite;

internal sealed record ReplyClaimRecord(string Session, string RequestHash, string TextHash, string? AttachmentHash, Guid Generation, DateTimeOffset FirstClaimUtc);
internal sealed record ReplyClaimSnapshot(List<ReplyClaimRecord> Claims, long Sequence, string Hash);
internal sealed record ReplyClaimMarker(int Schema, string ProfileKey, string State, long Sequence, string Hash, string StateHash);

internal static class ReplyClaimJournal
{
    const int Version = 1, ChecksumSize = 32, MaxFrame = 4 * 1024 * 1024, MaxPayload = MaxFrame - 4 - ChecksumSize, SnapshotChunkClaims = 1024;
    internal const int MaxPerSession = 4096, MaxSessionBytes = 64;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static readonly AsyncLocal<Action?> BeforeCompactReplace = new(), AfterCompactReplace = new();

    sealed record Row(int Version, string ProfileKey, long Sequence, string PreviousHash, string Action,
        ReplyClaimRecord? Claim = null, List<ReplyClaimRecord>? Claims = null, long CoverageSequence = -1, string? CoverageHash = null,
        string? StateHash = null, string? CoverageStateHash = null);

    internal static string Genesis(string profileKey) => Hash(Encoding.UTF8.GetBytes($"reply-claims-v{Version}:{profileKey}"));
    internal static string StateHash(IEnumerable<ReplyClaimRecord> claims) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(claims.Select(c => c with
            { RequestHash = c.RequestHash.ToUpperInvariant(), TextHash = c.TextHash.ToUpperInvariant(), AttachmentHash = c.AttachmentHash?.ToUpperInvariant() })
            .OrderBy(c => c.Session, StringComparer.Ordinal)
            .ThenBy(c => c.RequestHash, StringComparer.OrdinalIgnoreCase).ToArray(), Json));

    internal static ReplyClaimSnapshot Read(string path, string profileKey, ReplyClaimMarker marker, bool repairTail)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException e) { throw new InvalidDataException("The reply-claim journal is missing; delivery remains held.", e); }
        var claims = new Dictionary<(string, string), ReplyClaimRecord>();
        var sessions = new Dictionary<string, int>(StringComparer.Ordinal);
        var proofs = new Dictionary<long, string>();
        var stateProofs = new Dictionary<long, string>();
        Row? snapshot = null;
        var offset = 0;
        int? truncateAt = null;
        var sequence = 0L;
        var tip = Genesis(profileKey);
        proofs[0] = tip;
        string? expectedStateHash = StateHash([]);
        stateProofs[0] = expectedStateHash;
        var first = true;
        while (offset < bytes.Length)
        {
            var start = offset;
            if (bytes.Length - offset < 4) { truncateAt = start; break; }
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            if (length is < 2 or > MaxPayload) { throw Corrupt(); }
            offset += 4;
            if (bytes.Length - offset < length + ChecksumSize) { truncateAt = start; break; }
            var payload = bytes.AsSpan(offset, length);
            var digest = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(digest, bytes.AsSpan(offset + length, ChecksumSize))) { throw Corrupt(); }
            Row row;
            try { row = JsonSerializer.Deserialize<Row>(payload, Json) ?? throw Corrupt(); }
            catch (JsonException e) { throw new InvalidDataException("The reply-claim journal contains an invalid row.", e); }
            if (row.Version != Version || row.ProfileKey != profileKey || row.Sequence <= 0
                || !IsHash(row.PreviousHash) || !IsHash(row.StateHash)) { throw Corrupt(); }
            var rowHash = Convert.ToHexString(digest);
            if (first && row.Action is "snapshot" or "snapshotStart")
            {
                if (row.Claim is not null || row.CoverageSequence < 0 || row.CoverageHash is null || !IsHash(row.CoverageHash)
                    || row.CoverageStateHash is null || !IsHash(row.CoverageStateHash) || row.StateHash is null || !IsHash(row.StateHash)
                    || row.Sequence != row.CoverageSequence + 1 || row.PreviousHash != row.CoverageHash) { throw Corrupt(); }
                if (row.Action == "snapshotStart")
                {
                    if (row.Claims is not null) { throw Corrupt(); }
                    snapshot = row;
                }
                else
                {
                    ApplySnapshot(claims, sessions, row.Claims);
                    ProveSnapshot(claims, row, proofs, stateProofs);
                }
            }
            else
            {
                if (row.CoverageSequence != -1 || row.CoverageHash is not null
                    || row.CoverageStateHash is not null || row.Sequence != sequence + 1 || row.PreviousHash != tip) { throw Corrupt(); }
                if (snapshot is not null)
                {
                    if (row.Claim is not null || row.StateHash != snapshot.StateHash) { throw Corrupt(); }
                    if (row.Action == "snapshotChunk") { ApplySnapshot(claims, sessions, row.Claims); }
                    else if (row.Action == "snapshotEnd" && row.Claims is null)
                    {
                        ProveSnapshot(claims, snapshot, proofs, stateProofs);
                        snapshot = null;
                    }
                    else { throw Corrupt(); }
                }
                else
                {
                    if (row.Claims is not null) { throw Corrupt(); }
                    Apply(claims, sessions, row);
                }
            }
            sequence = row.Sequence;
            tip = rowHash;
            proofs[sequence] = tip;
            if (sequence == marker.Sequence && snapshot is null)
            {
                var stateHash = StateHash(claims.Values);
                if (row.StateHash != stateHash) { throw Corrupt(); }
                stateProofs[sequence] = stateHash;
            }
            expectedStateHash = row.StateHash;
            first = false;
            offset += length + ChecksumSize;
        }
        if (snapshot is not null) { throw Corrupt(); }
        if (sequence < marker.Sequence || !proofs.TryGetValue(marker.Sequence, out var proof) || proof != marker.Hash
            || !stateProofs.TryGetValue(marker.Sequence, out var stateProof) || stateProof != marker.StateHash)
        { throw new InvalidDataException("The reply-claim journal cannot prove the marker's high-water point."); }
        if (StateHash(claims.Values) != expectedStateHash) { throw Corrupt(); }
        if (truncateAt is not null)
        {
            if (!repairTail || ContainsValidFrame(bytes, truncateAt.Value, sequence, profileKey)) { throw Corrupt(); }
            Truncate(path, bytes, truncateAt.Value);
        }
        return new(claims.Values.ToList(), sequence, tip);
    }

    internal static ReplyClaimSnapshot Append(string path, string profileKey, ReplyClaimSnapshot state, string action, ReplyClaimRecord claim)
    {
        var row = new Row(Version, profileKey, state.Sequence + 1, state.Hash, action, claim,
            StateHash: StateHash(state.Claims));
        var bytes = Frame(JsonSerializer.SerializeToUtf8Bytes(row, Json));
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        { stream.Write(bytes); stream.Flush(true); }
        return new(state.Claims, row.Sequence, Hash(bytes.AsSpan(4, bytes.Length - 4 - ChecksumSize)));
    }

    internal static ReplyClaimSnapshot Compact(string path, string profileKey, ReplyClaimSnapshot state, ReplyClaimMarker marker)
    {
        var sequence = state.Sequence;
        var tip = state.Hash;
        var stateHash = StateHash(state.Claims);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = path + ".bak";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                void WriteRow(string action, List<ReplyClaimRecord>? claims = null)
                {
                    var covering = action is "snapshot" or "snapshotStart";
                    var row = new Row(Version, profileKey, ++sequence, tip, action, Claims: claims,
                        CoverageSequence: covering ? state.Sequence : -1, CoverageHash: covering ? state.Hash : null,
                        StateHash: stateHash, CoverageStateHash: covering ? stateHash : null);
                    var payload = JsonSerializer.SerializeToUtf8Bytes(row, Json);
                    stream.Write(Frame(payload));
                    tip = Hash(payload);
                }
                if (state.Claims.Count <= SnapshotChunkClaims) { WriteRow("snapshot", state.Claims); }
                else
                {
                    WriteRow("snapshotStart");
                    foreach (var chunk in state.Claims.Chunk(SnapshotChunkClaims)) { WriteRow("snapshotChunk", chunk.ToList()); }
                    WriteRow("snapshotEnd");
                }
                stream.Flush(true);
            }
            var verified = Read(temp, profileKey, marker, repairTail: false);
            if (verified.Sequence != sequence || verified.Hash != tip)
            { throw Corrupt(); }
            BeforeCompactReplace.Value?.Invoke();
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            ClaimDirectorySync.Flush(Path.GetDirectoryName(path)!);
            AfterCompactReplace.Value?.Invoke();
        }
        finally { File.Delete(temp); }
        return new(state.Claims, sequence, tip);
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

    static void ProveSnapshot(Dictionary<(string, string), ReplyClaimRecord> claims, Row snapshot,
        Dictionary<long, string> proofs, Dictionary<long, string> stateProofs)
    {
        var stateHash = StateHash(claims.Values);
        if (stateHash != snapshot.StateHash || stateHash != snapshot.CoverageStateHash) { throw Corrupt(); }
        proofs[snapshot.CoverageSequence] = snapshot.CoverageHash ?? throw Corrupt();
        stateProofs[snapshot.CoverageSequence] = stateHash;
    }

    static void Apply(Dictionary<(string, string), ReplyClaimRecord> claims, Dictionary<string, int> sessions, Row row)
    {
        if (row.Action == "claimed")
        {
            var claim = row.Claim ?? throw Corrupt();
            AddClaim(claims, sessions, claim);
            return;
        }
        if (row.Action is not ("released" or "expired") || row.Claim is null) { throw Corrupt(); }
        Validate(row.Claim);
        var key = (row.Claim.Session, row.Claim.RequestHash.ToUpperInvariant());
        if (!claims.TryGetValue(key, out var current) || current.Generation != row.Claim.Generation) { throw Corrupt(); }
        claims.Remove(key);
        sessions[current.Session]--;
    }

    static void ApplySnapshot(Dictionary<(string, string), ReplyClaimRecord> claims, Dictionary<string, int> sessions, List<ReplyClaimRecord>? snapshot)
    {
        if (snapshot is null) { throw Corrupt(); }
        foreach (var claim in snapshot) { AddClaim(claims, sessions, claim); }
    }

    static void AddClaim(Dictionary<(string, string), ReplyClaimRecord> claims, Dictionary<string, int> sessions, ReplyClaimRecord claim)
    {
        Validate(claim);
        var count = sessions.GetValueOrDefault(claim.Session) + 1;
        if (count > MaxPerSession || !claims.TryAdd((claim.Session, claim.RequestHash.ToUpperInvariant()), claim)) { throw Corrupt(); }
        sessions[claim.Session] = count;
    }

    static void Validate(ReplyClaimRecord? claim)
    {
        if (claim is null || string.IsNullOrEmpty(claim.Session) || Encoding.UTF8.GetByteCount(claim.Session) > MaxSessionBytes
            || !IsHash(claim.RequestHash) || !IsHash(claim.TextHash)
            || claim.AttachmentHash is not null && !IsHash(claim.AttachmentHash) || claim.Generation == Guid.Empty
            || claim.FirstClaimUtc == default || claim.FirstClaimUtc.Offset != TimeSpan.Zero) { throw Corrupt(); }
    }

    static bool ContainsValidFrame(byte[] bytes, int start, long sequence, string profileKey)
    {
        for (var offset = start + 1; offset <= bytes.Length - 4; offset++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            if (length is < 2 or > MaxPayload || bytes.Length - offset - 4 < length + ChecksumSize) { continue; }
            var payload = bytes.AsSpan(offset + 4, length);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), bytes.AsSpan(offset + 4 + length, ChecksumSize))) { continue; }
            try
            {
                var row = JsonSerializer.Deserialize<Row>(payload, Json);
                if (row is { Version: Version } && row.ProfileKey == profileKey && row.Sequence > sequence) { return true; }
            }
            catch (JsonException) { }
        }
        return false;
    }

    static void Truncate(string path, byte[] bytes, int length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
        stream.Flush(true);
    }

    static byte[] Frame(byte[] payload)
    {
        if (payload.Length > MaxPayload) { throw new InvalidDataException("The reply-claim frame exceeds the journal limit."); }
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
