using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Updates;

// The later wiring keeps these distinct (owner-approved 15:24 PT Nov 21): a known halt always refuses;
// unavailable protection allows only a person's confirmed single-machine exception; verified allows.
public enum HaltPolicyOutcome { Unavailable, KnownHalted, Verified }

// Floors: the highest release run any verified policy has offered per "channel/prerelease" (or
// "channel/release"); never lowered, so unsigned transport cannot pull candidates back.
public sealed record HaltPolicyVerdict(HaltPolicyOutcome Outcome, HaltPolicyEnvelope? Policy, IReadOnlyDictionary<string, long> Floors, string? Reason);

// The verified halt policy's durable state (docs/design/halt-policy-without-github-api.md,
// "Freshness, replay and failure"): its own file beside release-discovery.json, under the same
// installation-wide lock, replaced atomically. The verified body itself is kept, so sequence, hash,
// validity, halts and releases have one source. The latest time seen makes expiry terminal per
// sequence across restarts and exposes a clock put back. Lost or unreadable state is a new install:
// no sequence anchor, at most one signed lifetime (six hours) of exposure, as the design accepts.
public sealed class HaltPolicyStore(string folder, TimeProvider? time = null)
{
    public const string FileName = "halt-policy-state.json";
    readonly TimeProvider time = time ?? TimeProvider.System;
    // This process's newest state: newer than the file after a failed save, and all there is
    // while the lock can't be had, so known halts still refuse.
    State memory = State.Empty;

    // ReleaseDiscovery's lock file. Not reentrant: a caller must not already hold it.
    internal Func<string, FileStream> OpenLock { get; set; } = path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    internal Action<string, byte[]> Replace { get; set; } = (path, bytes) => { File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, overwrite: true); };
    internal TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(45);

    // Halts: the policy's, plus those of any other body signed at its sequence, until a higher one.
    sealed record State(HaltPolicyEnvelope? Policy, DateTimeOffset Seen, bool Contradicted, IReadOnlyDictionary<string, long> Floors, IReadOnlySet<string> Halts)
    { public static readonly State Empty = new(null, DateTimeOffset.MinValue, false, new Dictionary<string, long>(), new HashSet<string>()); }

    // read: a policy ReadAsync verified just now, or null when none was read (cached state only).
    public async Task<HaltPolicyVerdict> CheckAsync(HaltPolicyEnvelope? read, string? version, CancellationToken cancellationToken = default)
    {
        using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        // After the wait: the lock can take long enough for a policy to expire meanwhile.
        var now = time.GetUtcNow();
        var disk = held is null ? null : Load();
        var state = disk is null ? memory : Merge(memory, disk);
        var problem = disk is null ? "halt policy state could not be locked or read" : now < state.Seen ? "the clock went back" : null;
        if (read is not null)
        {
            // Signed equivocation retains both bodies' halts even when the new body is time-invalid.
            if (state.Policy is { } previous && read.Sequence == previous.Sequence && read.BodyHash != previous.BodyHash)
            { state = Merge(state, new State(read, now, false, new Dictionary<string, long>(), read.Halted.ToHashSet(StringComparer.Ordinal))); }
            if (read.IssuedAt > now) { problem ??= "halt policy issued in the future"; }
            else if (read.Sequence < (state.Policy?.Sequence ?? 0)) { problem ??= "halt policy sequence went back"; }
            // An expired policy, however high, clears no halt.
            else if (read.ExpiresAt <= (state.Seen > now ? state.Seen : now)) { problem ??= "halt policy already expired"; }
            else { state = Merge(state, new State(read, now, false, read.Releases.ToDictionary(Key, Run), read.Halted.ToHashSet(StringComparer.Ordinal))); }
        }
        memory = state = state with { Seen = state.Seen > now ? state.Seen : now };
        if (disk is not null && !Save(state)) { problem ??= "halt policy state not saved"; }
        var policy = state.Policy;
        // Again after the save, which can be slow; a verdict holds at most until now.
        var end = time.GetUtcNow();
        if (policy is not null && state.Seen < policy.ExpiresAt && end >= policy.ExpiresAt)
        {
            memory = state = state with { Seen = end };
            if (disk is not null && !Save(state)) { problem ??= "halt policy state not saved"; }
            end = time.GetUtcNow();
        }
        problem ??= end < state.Seen ? "the clock went back"
            : policy is null ? "no verified halt policy" : state.Contradicted ? "halt policy contradicted at its sequence"
            : state.Seen >= policy.ExpiresAt || end >= policy.ExpiresAt ? "halt policy expired" : null;
        var outcome = version is not null && state.Halts.Contains(version) ? HaltPolicyOutcome.KnownHalted
            : problem is null ? HaltPolicyOutcome.Verified : HaltPolicyOutcome.Unavailable;
        return new(outcome, policy, state.Floors, problem);
    }

    static string Key(HaltPolicyRelease r) => $"{r.Channel}/{(r.Prerelease ? "prerelease" : "release")}";
    static long Run(HaltPolicyRelease r) => ReleaseVersions.TryRun(r.Tag[1..], out var run) ? run : 0;

    // The higher sequence wins; at one sequence another body is equivocation, kept until a higher
    // sequence replaces it. Floors and the latest time seen only rise.
    static State Merge(State a, State b)
    {
        var (high, low) = (a.Policy?.Sequence ?? 0) >= (b.Policy?.Sequence ?? 0) ? (a, b) : (b, a);
        var same = high.Policy is { } p && low.Policy is { } q && p.Sequence == q.Sequence;
        var floors = new Dictionary<string, long>(low.Floors);
        foreach (var (key, run) in high.Floors) { floors[key] = Math.Max(run, floors.GetValueOrDefault(key)); }
        return new(high.Policy, a.Seen > b.Seen ? a.Seen : b.Seen,
            high.Contradicted || same && (low.Contradicted || high.Policy!.BodyHash != low.Policy!.BodyHash), floors,
            same ? high.Halts.Union(low.Halts, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal) : high.Halts);
    }

    // Null when the file exists but can't be read now; unparseable content is lost state.
    State? Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, FileName)));
            var root = doc.RootElement;
            var body = root.GetProperty("body").GetBytesFromBase64();
            return new(body.Length == 0 ? null : HaltPolicyReader.Parse(body), root.GetProperty("seen").GetDateTimeOffset(),
                root.GetProperty("contradicted").GetBoolean(), root.GetProperty("floors").EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetInt64()),
                root.GetProperty("halts").EnumerateArray().Select(h => h.GetString() ?? throw new FormatException()).ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidOperationException
            or FormatException or KeyNotFoundException or HaltPolicyUnavailableException) { return State.Empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    bool Save(State state)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBase64String("body", state.Policy?.Body ?? []);
            json.WriteString("seen", state.Seen);
            json.WriteBoolean("contradicted", state.Contradicted);
            json.WriteStartObject("floors");
            foreach (var (key, run) in state.Floors) { json.WriteNumber(key, run); }
            json.WriteEndObject();
            json.WriteStartArray("halts");
            foreach (var halt in state.Halts) { json.WriteStringValue(halt); }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        try { Replace(Path.Combine(folder, FileName), buffer.ToArray()); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    async Task<FileStream?> LockAsync(CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try { Directory.CreateDirectory(folder); return OpenLock(Path.Combine(folder, "release-discovery.lock")); }
            catch (IOException e) when (ReleaseDiscovery.Contended(e) && waited.Elapsed < LockWait) { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }
}