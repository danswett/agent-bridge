using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// One person-signed rotation per profile (design/fleet-grants.md): both active streams move byte for byte to retired
// files and each fresh stream starts with a genesis carrying the signed command. No cutoff is inferred, heads prove
// nothing, and grants read only streams whose genesis verifies, never the retired history.
public sealed partial class AgentAuditLog
{
    public const string RotateAction = "audit.rotate";
    internal sealed record RetiredStream([property: JsonRequired] long Length, [property: JsonRequired] string Sha256, [property: JsonRequired] string Tail);
    internal sealed record RotationSeal([property: JsonRequired] RetiredStream Agents, [property: JsonRequired] RetiredStream Launches);
    sealed record GenesisRotation([property: JsonRequired] string Retired, [property: JsonRequired] long Length,
        [property: JsonRequired] string Sha256, [property: JsonRequired] SignedCommand Command);
    static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    static readonly bool[] Streams = [false, true];
    // Verifies genesis signatures; without it a rotated stream never fully verifies and nothing rotates.
    public P256KeyPair? DeviceKey { get; init; }
    // Tests only: called after each durable rotation step, to stand in for a crash there.
    internal Action<int>? RotationStep { get; init; }
    static string JournalFile(string folder) => Path.Combine(folder, "rotation.pending");
    static string RetiredName(bool launch, string request) => $"retired/{(launch ? "launches" : "agents")}-{request}.jsonl";
    string AuditFolder() => PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded");
    // What a person signs: the exact bytes in each stream now, so any later append refuses the rotation.
    internal RotationSeal Proposal() { lock (gate) { var folder = AuditFolder(); using var held = Lock(folder); return Seal(folder); } }
    // Null once rotated, including a retry of the same command. Only a user principal whose device is pinned here as a
    // person's, never this machine's own key, rotates: a relay's device kind proves nothing.
    internal string? Rotate(SignedCommand command, Func<string, bool> pinnedPerson)
    {
        if (DeviceKey is not { } key) { return "this host has no device key to verify a rotation"; }
        var (c, problem) = SignedCommands.Verify(key, command, RotateAction, time.GetUtcNow());
        if (c is null) { return problem; }
        if (Signed(key, command) is null || !pinnedPerson(command.By)) { return "only an independently pinned person's device may rotate the audit"; }
        if (Sealed(c) is not { } seal) { return "the rotation does not say what it retires"; }
        try
        {
            lock (gate)
            {
                var folder = AuditFolder();
                using var held = Lock(folder);
                Resume(folder);
                var bound = Streams.Select(launch => GenesisOf(folder, launch, key)).ToArray();
                if (bound.Any(b => b is not null)) { return bound.All(b => b == c.Request) ? null : "this profile's audit was already rotated"; }
                // Retiring grant history would orphan the store's tombstones and budgets, so only pre-grant profiles rotate.
                if (File.Exists(Path.Combine(profile, Fleet.Grants.GrantStore.FileName)) || Streams.SelectMany(l => Files(folder, l))
                    .SelectMany(File.ReadLines).Any(l => l.Contains("\"action\":\"grant.", StringComparison.Ordinal)))
                { return "grant history exists; rotating would hide it"; }
                if (Seal(folder) != seal) { return "the audit changed after the rotation was signed; press again"; }
                Durable(JournalFile(folder), s => s.Write(JsonSerializer.SerializeToUtf8Bytes(command, Strict)));
                RotationStep?.Invoke(0);
                Complete(folder, c, command, seal);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { log($"agent audit rotation refused: {e.GetType().Name}"); return "the audit could not be rotated; see the host log"; }
    }
    // A crash mid-rotation leaves the journal: every later write finishes that rotation first, never a second one.
    void Resume(string folder)
    {
        if (!File.Exists(JournalFile(folder))) { return; }
        var command = JsonSerializer.Deserialize<SignedCommand>(File.ReadAllBytes(JournalFile(folder)), Strict);
        if (command is null || DeviceKey is not { } key || Signed(key, command) is not { } c || Sealed(c) is not { } seal)
        { throw new IOException("a pending audit rotation cannot be verified"); }
        Complete(folder, c, command, seal);
    }
    // Each step is durable before the next; a retry skips a finished stream and reuses a retired file that matches.
    void Complete(string folder, CommandBody c, SignedCommand command, RotationSeal seal)
    {
        foreach (var launch in Streams)
        {
            if (GenesisOf(folder, launch, DeviceKey!) == c.Request) { continue; }
            var (mine, name) = (launch ? seal.Launches : seal.Agents, RetiredName(launch, c.Request));
            var retired = Path.Combine(folder, name);
            if (mine.Length > 0 && !File.Exists(retired))
            {
                PrivateFolder.Ensure(Path.GetDirectoryName(retired)!, "the retired audit folder", "the audit is not rotated");
                Durable(retired, output => { foreach (var file in Files(folder, launch)) { using var input = File.OpenRead(file); input.CopyTo(output); } });
                RotationStep?.Invoke(launch ? 4 : 1);
            }
            if (mine.Length > 0 && !Matches(retired, mine)) { throw new IOException("the retired audit does not match its signed rotation"); }
            // The journal blocks appends, so what remains is the retired content or a torn genesis.
            foreach (var file in Files(folder, launch).Append(HeadFile(folder, launch)).ToArray()) { File.Delete(file); }
            RotationStep?.Invoke(launch ? 5 : 2);
            var genesis = JsonSerializer.Serialize(new { v = 1, previousSha256 = mine.Tail, rotation = new GenesisRotation(name, mine.Length, mine.Sha256, command) }, Json);
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

    // A missing retired file is accepted: the signed genesis still says what it was, and nothing reads that history.
    static string? GenesisOf(string folder, bool launch, P256KeyPair key, bool retired = false)
    {
        try
        {
            if (Files(folder, launch).FirstOrDefault() is not { } first) { return null; }
            using var doc = JsonDocument.Parse(File.ReadLines(first).First());
            if (!doc.RootElement.TryGetProperty("rotation", out var element)) { return null; }
            if (element.Deserialize<GenesisRotation>(Strict) is not { } rotation || Signed(key, rotation.Command) is not { } c || Sealed(c) is not { } seal) { return null; }
            var mine = launch ? seal.Launches : seal.Agents;
            var path = Path.Combine(folder, rotation.Retired);
            return rotation.Retired == RetiredName(launch, c.Request) && rotation.Length == mine.Length && rotation.Sha256 == mine.Sha256
                && doc.RootElement.GetProperty("previousSha256").GetString() == mine.Tail
                && (!retired || !File.Exists(path) || Matches(path, mine)) ? c.Request : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or ArgumentException) { return null; }
    }
    // A genesis outlives its command's lifetime: check the signature and fields as of when it was signed.
    static CommandBody? Signed(P256KeyPair key, SignedCommand command)
    {
        try
        {
            var issued = JsonSerializer.Deserialize<CommandBody>(BlobFormat.FromBase64Url(command.Body) ?? [], ProtocolJson.Options)?.IssuedAt ?? 0;
            var (c, _) = SignedCommands.Verify(key, command, RotateAction, DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(issued, 0, 253402300799)));
            return c is not null && c.Principal.Device != Wire.DeviceId(key.Public) && c.To == c.Target.Machine ? c : null;
        }
        catch (JsonException) { return null; }
    }
    static RotationSeal? Sealed(CommandBody c) { try { return c.Args?.Deserialize<RotationSeal>(Strict); } catch (JsonException) { return null; } }
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
