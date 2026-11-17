using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Migration;

namespace AgentBridge.Host.Lite;

public enum ReplyClaimStatus { Acquired, Existing, RequestReused, Full }

public sealed record ReplyClaimResult(ReplyClaimStatus Status, Guid Generation);

public sealed class ReplyClaimStore : IDisposable
{
    const int Schema = 1;
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(24), LockWait = TimeSpan.FromSeconds(2);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static readonly AsyncLocal<Action?> BeforeRecoveryFileFlush = new();
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    readonly Paths paths;
    readonly TimeProvider time;
    bool disposed;

    ReplyClaimStore(Paths paths, TimeProvider? time)
    {
        this.paths = paths;
        this.time = time ?? TimeProvider.System;
    }

    public static void EnsureInitialized(string profileFolder)
    {
        var paths = For(profileFolder);
        using var held = Lock(paths);
        var migration = Read<MigrationDocument>(paths.Migration, required: false);
        var marker = Read<ReplyClaimMarker>(paths.Marker, required: false);
        if (migration is null)
        {
            if (marker is not null || File.Exists(paths.Ledger)
                || Directory.Exists(paths.Directory) && Directory.EnumerateFileSystemEntries(paths.Directory).Any())
            { throw new InvalidDataException("Reply-claim state exists without a migration record; delivery remains held."); }
            migration = new(Schema, paths.ProfileKey, "initializing");
            Write(paths.Migration, migration);
        }
        ValidateProfile(migration.Schema, migration.ProfileKey, paths, "migration");
        if (migration.State == "complete")
        {
            if (marker is null || marker.State != "active") { throw new InvalidDataException("A completed reply-claim migration has a missing or inactive marker."); }
            ValidateProfile(marker.Schema, marker.ProfileKey, paths, "marker");
            Load(paths, ref marker, repairTail: true);
            return;
        }
        if (migration.State != "initializing") { throw new InvalidDataException("The reply-claim migration state is invalid."); }
        if (marker is null)
        {
            if (File.Exists(paths.Ledger)) { throw new InvalidDataException("The reply-claim marker is missing; delivery remains held."); }
            marker = new(Schema, paths.ProfileKey, "initializing", 0, ReplyClaimJournal.Genesis(paths.ProfileKey),
                ReplyClaimJournal.StateHash([]));
            Write(paths.Marker, marker);
        }
        ValidateProfile(marker.Schema, marker.ProfileKey, paths, "marker");
        if (marker.State == "active") { Load(paths, ref marker, repairTail: true); }
        else if (marker.State == "initializing")
        {
            var active = marker with { State = "active" };
            if (File.Exists(paths.Ledger))
            {
                if (ReplyClaimJournal.Read(paths.Ledger, paths.ProfileKey, active, repairTail: true).Claims.Count != 0)
                { throw new InvalidDataException("An initializing reply-claim ledger is not empty."); }
            }
            else { WriteBytes(paths.Ledger, []); }
            Write(paths.Marker, active);
        }
        else { throw new InvalidDataException("The reply-claim marker state is invalid."); }
        Write(paths.Migration, migration with { State = "complete" });
    }

    public static ReplyClaimStore Open(string profileFolder, TimeProvider? time = null)
    {
        var paths = For(profileFolder);
        EnsureInitialized(profileFolder);
        return new(paths, time);
    }

    public static void RecoverFromBackup(string profileFolder, string backupPath)
    {
        var paths = For(profileFolder);
        using var held = Lock(paths);
        var marker = ReadyMarker(paths);
        ReplyClaimJournal.Recover(paths.Ledger, Path.GetFullPath(backupPath), paths.ProfileKey, marker);
        Load(paths, ref marker, repairTail: false);
    }

    public ReplyClaimResult Acquire(string sessionId, string requestId, string textHash, string? attachmentHash = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        if (Encoding.UTF8.GetByteCount(sessionId) > ReplyClaimJournal.MaxSessionBytes)
        { throw new ArgumentException("Session IDs may not exceed 64 UTF-8 bytes.", nameof(sessionId)); }
        ValidateHash(textHash, nameof(textHash));
        if (attachmentHash is not null) { ValidateHash(attachmentHash, nameof(attachmentHash)); }
        using var held = Lock(paths);
        var marker = ReadyMarker(paths);
        var ledger = Load(paths, ref marker, repairTail: true);
        var now = time.GetUtcNow();
        foreach (var expiredClaim in ledger.Claims.Where(c => now - c.FirstClaimUtc >= Lifetime).ToArray())
        {
            ledger.Claims.Remove(expiredClaim);
            ledger = ReplyClaimJournal.Append(paths.Ledger, paths.ProfileKey, ledger, "expired", expiredClaim);
            marker = Advance(paths, marker, ledger);
        }
        if (new FileInfo(paths.Ledger).Length > 1024 * 1024)
        {
            ledger = Compact(paths, marker, ledger);
            marker = marker with { Sequence = ledger.Sequence, Hash = ledger.Hash };
        }
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
        var existing = ledger.Claims.FirstOrDefault(c => c.Session == sessionId && Eq(c.RequestHash, requestHash));
        if (existing is not null)
        {
            return Eq(existing.TextHash, textHash) && Eq(existing.AttachmentHash, attachmentHash)
                ? new(ReplyClaimStatus.Existing, existing.Generation) : new(ReplyClaimStatus.RequestReused, Guid.Empty);
        }
        if (ledger.Claims.Count(c => c.Session == sessionId) >= ReplyClaimJournal.MaxPerSession) { return new(ReplyClaimStatus.Full, Guid.Empty); }
        var claim = new ReplyClaimRecord(sessionId, requestHash, textHash.ToUpperInvariant(), attachmentHash?.ToUpperInvariant(), Guid.NewGuid(), now);
        ledger.Claims.Add(claim);
        ledger = ReplyClaimJournal.Append(paths.Ledger, paths.ProfileKey, ledger, "claimed", claim);
        marker = Advance(paths, marker, ledger);
        return new(ReplyClaimStatus.Acquired, claim.Generation);
    }

    public bool Release(string sessionId, string requestId, Guid generation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        using var held = Lock(paths);
        var marker = ReadyMarker(paths);
        var ledger = Load(paths, ref marker, repairTail: true);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
        var claim = ledger.Claims.FirstOrDefault(c => c.Session == sessionId && Eq(c.RequestHash, requestHash) && c.Generation == generation);
        if (claim is null) { return false; }
        if (new FileInfo(paths.Ledger).Length > 1024 * 1024) { ledger = Compact(paths, marker, ledger); }
        ledger.Claims.Remove(claim);
        ledger = ReplyClaimJournal.Append(paths.Ledger, paths.ProfileKey, ledger, "released", claim);
        marker = Advance(paths, marker, ledger);
        return true;
    }

    public void Compact()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var held = Lock(paths);
        var marker = ReadyMarker(paths);
        var ledger = Load(paths, ref marker, repairTail: true);
        Compact(paths, marker, ledger);
    }

    public void Dispose() => disposed = true;

    static ReplyClaimSnapshot Load(Paths paths, ref ReplyClaimMarker marker, bool repairTail)
    {
        var ledger = ReplyClaimJournal.Read(paths.Ledger, paths.ProfileKey, marker, repairTail);
        if (ledger.Sequence > marker.Sequence)
        {
            using (var stream = new FileStream(paths.Ledger, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            { BeforeRecoveryFileFlush.Value?.Invoke(); stream.Flush(true); }
            ClaimDirectorySync.Flush(paths.Directory);
            marker = Advance(paths, marker, ledger);
        }
        return ledger;
    }

    static ReplyClaimSnapshot Compact(Paths paths, ReplyClaimMarker marker, ReplyClaimSnapshot ledger)
    {
        var compacted = ReplyClaimJournal.Compact(paths.Ledger, paths.ProfileKey, ledger, marker);
        var verified = ReplyClaimJournal.Read(paths.Ledger, paths.ProfileKey, marker, repairTail: false);
        if (verified.Sequence != compacted.Sequence || verified.Hash != compacted.Hash)
        { throw new InvalidDataException("The compacted reply-claim journal did not verify."); }
        Advance(paths, marker, verified);
        return verified;
    }

    static ReplyClaimMarker Advance(Paths paths, ReplyClaimMarker marker, ReplyClaimSnapshot ledger)
    {
        var next = marker with { Sequence = ledger.Sequence, Hash = ledger.Hash, StateHash = ReplyClaimJournal.StateHash(ledger.Claims) };
        Write(paths.Marker, next);
        return next;
    }

    static ReplyClaimMarker ReadyMarker(Paths paths)
    {
        var migration = Read<MigrationDocument>(paths.Migration, required: true)!;
        var marker = Read<ReplyClaimMarker>(paths.Marker, required: true)!;
        ValidateProfile(migration.Schema, migration.ProfileKey, paths, "migration");
        ValidateProfile(marker.Schema, marker.ProfileKey, paths, "marker");
        if (migration.State != "complete" || marker.State != "active" || marker.Sequence < 0
            || marker.Hash is not { Length: 64 } || !marker.Hash.All(Uri.IsHexDigit)
            || marker.StateHash is not { Length: 64 } || !marker.StateHash.All(Uri.IsHexDigit))
        { throw new InvalidDataException("The reply-claim migration or marker is invalid or inactive."); }
        return marker;
    }

    static Paths For(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var profile = Path.GetFullPath(folder);
        var keyPath = OperatingSystem.IsWindows() ? profile.ToUpperInvariant() : profile;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyPath)));
        var directory = Path.Combine(profile, "reply-claims");
        return new(profile, key, directory, Path.Combine(profile, "reply-claims.migration.json"),
            Path.Combine(profile, "reply-claims.marker"), Path.Combine(directory, "claims.json"),
            Path.Combine(profile, "reply-claims.lock"));
    }

    static IDisposable Lock(Paths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        var gate = Gates.GetOrAdd(paths.Profile, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(LockWait)) { throw new IOException("Timed out waiting for the reply-claim lock."); }
        try
        {
            var until = Environment.TickCount64 + (long)LockWait.TotalMilliseconds;
            while (true)
            {
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                    var stream = new FileStream(paths.Lock, options);
                    return new LockLease(gate, stream);
                }
                catch (IOException) when (Environment.TickCount64 < until) { Thread.Sleep(20); }
            }
        }
        catch { gate.Release(); throw; }
    }

    static T? Read<T>(string path, bool required) where T : class
    {
        if (!File.Exists(path)) { return required ? throw new InvalidDataException($"Required reply-claim state is missing: {Path.GetFileName(path)}.") : null; }
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException($"Reply-claim state is invalid: {Path.GetFileName(path)}."); }
        catch (JsonException e) { throw new InvalidDataException($"Reply-claim state is invalid: {Path.GetFileName(path)}.", e); }
    }

    static void Write<T>(string path, T value) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    static void WriteBytes(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
        else { Directory.CreateDirectory(directory, ownerOnly); File.SetUnixFileMode(directory, ownerOnly); }
        ClaimDirectorySync.Flush(Path.GetDirectoryName(directory)!);
        DurableFile.Write(path, bytes);
        ClaimDirectorySync.Flush(directory);
    }

    static void ValidateProfile(int schema, string profileKey, Paths paths, string name)
    { if (schema != Schema || profileKey != paths.ProfileKey) { throw new InvalidDataException($"The reply-claim {name} belongs to another profile or schema."); } }

    static void ValidateHash(string value, string name)
    { if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit)) { throw new ArgumentException("Expected a SHA-256 hash in hexadecimal form.", name); } }
    static bool Eq(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    sealed class LockLease(SemaphoreSlim gate, FileStream stream) : IDisposable
    {
        public void Dispose() { stream.Dispose(); gate.Release(); }
    }

    sealed record Paths(string Profile, string ProfileKey, string Directory, string Migration, string Marker, string Ledger, string Lock);
    sealed record MigrationDocument(int Schema, string ProfileKey, string State);
}
