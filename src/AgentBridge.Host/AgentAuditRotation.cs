using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// One automatic rotation per profile, inside its first person-confirmed grant issue (design/fleet-grants.md, owner
// Nov 21 21:41 PT): both active streams move byte for byte to retired files and each fresh stream starts with a genesis
// carrying the host-MACed record. No cutoff is inferred, heads prove nothing, and grants never read retired history.
public sealed partial class AgentAuditLog
{
    public const string FirstGrantReason = "automatic-first-grant";
    internal sealed record RetiredStream([property: JsonRequired] long Length, [property: JsonRequired] string Sha256, [property: JsonRequired] string Tail);
    internal sealed record RotationSeal([property: JsonRequired] RetiredStream Agents, [property: JsonRequired] RetiredStream Launches);
    // What the host MACs: the exact bytes it retires, why, and the grant whose confirmed issuance retired them.
    internal sealed record RotationRecord([property: JsonRequired] string Request, [property: JsonRequired] string Reason,
        [property: JsonRequired] string Grant, [property: JsonRequired] long At, [property: JsonRequired] RotationSeal Seal, [property: JsonRequired] string Mac);
    sealed record GenesisRotation([property: JsonRequired] string Retired, [property: JsonRequired] long Length,
        [property: JsonRequired] string Sha256, [property: JsonRequired] RotationRecord Record);
    static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    static readonly bool[] Streams = [false, true];
    // Verifies genesis MACs; without it a rotated stream never fully verifies.
    public P256KeyPair? DeviceKey { get; init; }
    // Tests only: called after each durable rotation step, to stand in for a crash there.
    internal Action<int>? RotationStep { get; init; }
    static string JournalFile(string folder) => Path.Combine(folder, "rotation.pending");
    static string RetiredName(bool launch, string request) => $"retired/{(launch ? "launches" : "agents")}-{request}.jsonl";
    string AuditFolder() => PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded");
    // Only GrantStore.Issue calls this, inside the person-confirmed issuance it is part of. Null when this call rotated or
    // finished a journaled rotation; a profile already rotated, or not provably free of grant history, refuses unchanged.
    internal string? RotateForFirstGrant(P256KeyPair key, Guid grant)
    {
        try
        {
            lock (gate)
            {
                var folder = AuditFolder();
                using var held = Lock(folder);
                if (File.Exists(JournalFile(folder))) { Resume(folder, key); return null; }
                if (Streams.Any(launch => GenesisOf(folder, launch, key) is not null)) { return "this profile's audit was already rotated"; }
                // Only provably grant-free history rotates (the reader's own parse, nothing retained away, no gated rows
                // without heads): retiring grant history would orphan its tombstones and budgets and reset the boundary.
                long retained = 0;
                var (rows, complete) = GateAuditProjection.Load(profile, alreadyLocked: true, boundary: (floor, _) => retained = floor);
                if (File.Exists(Path.Combine(profile, Fleet.Grants.GrantStore.FileName)) || !complete || retained > 0
                    || rows.Any(r => r.Action.StartsWith("grant.", StringComparison.OrdinalIgnoreCase)) || !HasHeads(folder) && rows.Any(r => r.Gate is not null))
                { return "grant history cannot be proved absent; rotating could hide it"; }
                var record = Sign(key, new(Guid.NewGuid().ToString(), FirstGrantReason, grant.ToString(), time.GetUtcNow().ToUnixTimeSeconds(), Seal(folder), ""));
                Durable(JournalFile(folder), s => s.Write(JsonSerializer.SerializeToUtf8Bytes(record, Strict)));
                RotationStep?.Invoke(0);
                Complete(folder, record, key);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        { log($"agent audit rotation refused: {e.GetType().Name}"); return "the audit could not be rotated; see the host log"; }
    }
    // A crash mid-rotation leaves the journal: every later write finishes that rotation first, never a second one.
    void Resume(string folder, P256KeyPair? key = null)
    {
        if (!File.Exists(JournalFile(folder))) { return; }
        var record = JsonSerializer.Deserialize<RotationRecord>(File.ReadAllBytes(JournalFile(folder)), Strict);
        if ((key ?? DeviceKey) is not { } k || Verified(k, record) is not { } r) { throw new IOException("a pending audit rotation cannot be verified"); }
        Complete(folder, r, k);
    }
    // Each step is durable before the next; a retry skips a finished stream and reuses a retired file that matches.
    void Complete(string folder, RotationRecord r, P256KeyPair key)
    {
        foreach (var launch in Streams)
        {
            if (GenesisOf(folder, launch, key) == r.Request) { continue; }
            var (mine, name) = (launch ? r.Seal.Launches : r.Seal.Agents, RetiredName(launch, r.Request));
            var retired = Path.Combine(folder, name);
            if (mine.Length > 0 && !File.Exists(retired))
            {
                PrivateFolder.Ensure(Path.GetDirectoryName(retired)!, "the retired audit folder", "the audit is not rotated");
                Durable(retired, output => { foreach (var file in Files(folder, launch)) { using var input = File.OpenRead(file); input.CopyTo(output); } });
                RotationStep?.Invoke(launch ? 4 : 1);
            }
            if (mine.Length > 0 && !Matches(retired, mine)) { throw new IOException("the retired audit does not match its rotation record"); }
            // The journal blocks appends, so what remains is the retired content or a torn genesis.
            foreach (var file in Files(folder, launch).Append(HeadFile(folder, launch)).ToArray()) { File.Delete(file); }
            RotationStep?.Invoke(launch ? 5 : 2);
            var genesis = JsonSerializer.Serialize(new { v = 1, previousSha256 = mine.Tail, rotation = new GenesisRotation(name, mine.Length, mine.Sha256, r) }, Json);
            var day = Path.Combine(folder, FileFor(time.GetUtcNow(), launch));
            using (var stream = PrivateFolder.CreateNew(day)) { stream.Write(Encoding.UTF8.GetBytes(genesis + "\n")); stream.Flush(flushToDisk: true); }
            if (OperatingSystem.IsWindows()) { NarrowFileWindows(day); }
            WriteHead(folder, launch, null, 0, "none");
            RotationStep?.Invoke(launch ? 6 : 3);
        }
        File.Delete(JournalFile(folder));
    }
    // The request of the rotation both streams start with, when each genesis verifies and any retired file matches.
    internal static string? Rotation(string profile, P256KeyPair key) => GenesisOf(Path.Combine(profile, FolderName), false, key, retired: true) is { } agents
        && GenesisOf(Path.Combine(profile, FolderName), true, key, retired: true) == agents ? agents : null;

    // A missing retired file is accepted: the MACed genesis still says what it was, and nothing reads that history.
    static string? GenesisOf(string folder, bool launch, P256KeyPair key, bool retired = false)
    {
        try
        {
            // A genesis without its newline is torn, never trusted.
            if (Files(folder, launch).FirstOrDefault() is not { } first || FirstLine(first) is not { } line) { return null; }
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("rotation", out var element)) { return null; }
            if (element.Deserialize<GenesisRotation>(Strict) is not { } rotation || Verified(key, rotation.Record) is not { } r) { return null; }
            var mine = launch ? r.Seal.Launches : r.Seal.Agents;
            var path = Path.Combine(folder, rotation.Retired);
            return rotation.Retired == RetiredName(launch, r.Request) && rotation.Length == mine.Length && rotation.Sha256 == mine.Sha256
                && doc.RootElement.GetProperty("previousSha256").GetString() == mine.Tail
                && (!retired || !File.Exists(path) || Matches(path, mine)) ? r.Request : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or CryptographicException) { return null; }
    }
    static string? FirstLine(string file)
    {
        using var input = File.OpenRead(file);
        var buffer = new byte[16384];
        var n = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var end = Array.IndexOf(buffer, (byte)'\n', 0, n);
        return end < 0 ? null : Encoding.UTF8.GetString(buffer, 0, end);
    }
    // Only this host's key MACs a rotation, and only for the first-grant reason.
    static RotationRecord Sign(P256KeyPair key, RotationRecord r) => r with { Mac = Convert.ToHexString(DeviceMacFile.Mac(
        JsonSerializer.SerializeToUtf8Bytes(r with { Mac = "" }, Strict), key, "agent-bridge audit rotation v1"u8)) };
    static RotationRecord? Verified(P256KeyPair key, RotationRecord? r) => r is { Reason: FirstGrantReason, Seal: not null }
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(key, r).Mac), Encoding.ASCII.GetBytes(r.Mac)) ? r : null;
    static RotationSeal Seal(string folder) => new(Measure(Files(folder, false).ToArray()), Measure(Files(folder, true).ToArray()));
    static bool Matches(string path, RetiredStream mine) => Measure([path], tail: false) is var m && m.Length == mine.Length && m.Sha256 == mine.Sha256;
    static RetiredStream Measure(string[] files, bool tail = true)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var (buffer, length) = (new byte[81920], 0L);
        foreach (var file in files)
        {
            using var input = File.OpenRead(file);
            for (int n; (n = input.Read(buffer)) > 0; length += n) { sha.AppendData(buffer, 0, n); }
        }
        return new(length, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), files.Length == 0 || !tail ? Genesis : TailHash(files[^1], promoteTail: true).Hash);
    }
    static void Durable(string file, Action<Stream> write)
    {
        File.Delete(file + ".new");
        using (var stream = PrivateFolder.CreateNew(file + ".new")) { write(stream); stream.Flush(flushToDisk: true); }
        File.Move(file + ".new", file, overwrite: true);
    }
}
