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
    const int Schema = 1, MaxPerSession = 4096;
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(24), LockWait = TimeSpan.FromSeconds(2);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
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
        var marker = Read<MarkerDocument>(paths.Marker, required: false);
        var ledger = Read<LedgerDocument>(paths.Ledger, required: false);
        if (migration is null)
        {
            if (marker is not null || ledger is not null
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
            ValidateLedger(ledger, paths);
            return;
        }
        if (migration.State != "initializing") { throw new InvalidDataException("The reply-claim migration state is invalid."); }
        if (marker is null)
        {
            if (ledger is not null) { throw new InvalidDataException("The reply-claim marker is missing; delivery remains held."); }
            marker = new(Schema, paths.ProfileKey, "initializing");
            Write(paths.Marker, marker);
        }
        ValidateProfile(marker.Schema, marker.ProfileKey, paths, "marker");
        if (marker.State == "active")
        { ValidateLedger(ledger, paths); }
        else if (marker.State == "initializing")
        {
            if (ledger is not null)
            {
                ValidateLedger(ledger, paths);
                if (ledger.Claims.Count != 0) { throw new InvalidDataException("An initializing reply-claim ledger is not empty."); }
            }
            else
            {
                ledger = new(Schema, paths.ProfileKey, []);
                Write(paths.Ledger, ledger);
            }
            marker = new(Schema, paths.ProfileKey, "active");
            Write(paths.Marker, marker);
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

    public ReplyClaimResult Acquire(string sessionId, string requestId, string textHash, string? attachmentHash = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ValidateHash(textHash, nameof(textHash));
        if (attachmentHash is not null) { ValidateHash(attachmentHash, nameof(attachmentHash)); }
        using var held = Lock(paths);
        ValidateReady(paths);
        var ledger = Read<LedgerDocument>(paths.Ledger, required: true);
        ValidateLedger(ledger, paths);
        var now = time.GetUtcNow();
        var removed = ledger!.Claims.RemoveAll(c => now - c.FirstClaimUtc >= Lifetime) > 0;
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
        var existing = ledger.Claims.FirstOrDefault(c => c.Session == sessionId && string.Equals(c.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (removed) { Write(paths.Ledger, ledger); }
            return string.Equals(existing.TextHash, textHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.AttachmentHash, attachmentHash, StringComparison.OrdinalIgnoreCase)
                ? new(ReplyClaimStatus.Existing, existing.Generation)
                : new(ReplyClaimStatus.RequestReused, Guid.Empty);
        }
        if (ledger.Claims.Count(c => c.Session == sessionId) >= MaxPerSession)
        {
            if (removed) { Write(paths.Ledger, ledger); }
            return new(ReplyClaimStatus.Full, Guid.Empty);
        }
        var claim = new Claim(sessionId, requestHash, textHash.ToUpperInvariant(), attachmentHash?.ToUpperInvariant(), Guid.NewGuid(), now);
        ledger.Claims.Add(claim);
        Write(paths.Ledger, ledger);
        return new(ReplyClaimStatus.Acquired, claim.Generation);
    }

    public bool Release(string sessionId, string requestId, Guid generation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        using var held = Lock(paths);
        ValidateReady(paths);
        var ledger = Read<LedgerDocument>(paths.Ledger, required: true);
        ValidateLedger(ledger, paths);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
        var index = ledger!.Claims.FindIndex(c => c.Session == sessionId && string.Equals(c.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase) && c.Generation == generation);
        if (index < 0) { return false; }
        ledger.Claims.RemoveAt(index);
        Write(paths.Ledger, ledger);
        return true;
    }

    public void Dispose() => disposed = true;

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

    static void ValidateLedger(LedgerDocument? ledger, Paths paths)
    {
        if (ledger is null || ledger.Claims is null) { throw new InvalidDataException("The reply-claim ledger is missing or invalid; delivery remains held."); }
        ValidateProfile(ledger.Schema, ledger.ProfileKey, paths, "ledger");
        var keys = new HashSet<(string, string)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var claim in ledger.Claims)
        {
            if (claim is null || string.IsNullOrEmpty(claim.Session) || !IsHash(claim.RequestHash) || !IsHash(claim.TextHash)
                || claim.AttachmentHash is not null && !IsHash(claim.AttachmentHash) || claim.Generation == Guid.Empty
                || claim.FirstClaimUtc == default || claim.FirstClaimUtc.Offset != TimeSpan.Zero
                || !keys.Add((claim.Session, claim.RequestHash.ToUpperInvariant())))
            { throw new InvalidDataException("The reply-claim ledger is invalid; delivery remains held."); }
            counts[claim.Session] = counts.GetValueOrDefault(claim.Session) + 1;
            if (counts[claim.Session] > MaxPerSession) { throw new InvalidDataException("The reply-claim ledger exceeds its per-session bound."); }
        }
    }

    static void ValidateReady(Paths paths)
    {
        var migration = Read<MigrationDocument>(paths.Migration, required: true)!;
        var marker = Read<MarkerDocument>(paths.Marker, required: true)!;
        ValidateProfile(migration.Schema, migration.ProfileKey, paths, "migration");
        ValidateProfile(marker.Schema, marker.ProfileKey, paths, "marker");
        if (migration.State != "complete" || marker.State != "active")
        { throw new InvalidDataException("The reply-claim migration or marker is not active."); }
    }

    static void ValidateProfile(int schema, string profileKey, Paths paths, string name)
    { if (schema != Schema || profileKey != paths.ProfileKey) { throw new InvalidDataException($"The reply-claim {name} belongs to another profile or schema."); } }

    static T? Read<T>(string path, bool required) where T : class
    {
        if (!File.Exists(path)) { return required ? throw new InvalidDataException($"Required reply-claim state is missing: {Path.GetFileName(path)}.") : null; }
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException($"Reply-claim state is invalid: {Path.GetFileName(path)}."); }
        catch (JsonException e) { throw new InvalidDataException($"Reply-claim state is invalid: {Path.GetFileName(path)}.", e); }
    }

    static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
        else { Directory.CreateDirectory(directory, ownerOnly); File.SetUnixFileMode(directory, ownerOnly); }
        ClaimDirectorySync.Flush(Path.GetDirectoryName(directory)!);
        DurableFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        ClaimDirectorySync.Flush(directory);
    }

    static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    static void ValidateHash(string value, string name)
    { if (!IsHash(value)) { throw new ArgumentException("Expected a SHA-256 hash in hexadecimal form.", name); } }

    sealed class LockLease(SemaphoreSlim gate, FileStream stream) : IDisposable
    {
        public void Dispose() { stream.Dispose(); gate.Release(); }
    }

    sealed record Paths(string Profile, string ProfileKey, string Directory, string Migration, string Marker, string Ledger, string Lock);
    sealed record MigrationDocument(int Schema, string ProfileKey, string State);
    sealed record MarkerDocument(int Schema, string ProfileKey, string State);
    sealed record LedgerDocument(int Schema, string ProfileKey, List<Claim> Claims);
    sealed record Claim(string Session, string RequestHash, string TextHash, string? AttachmentHash, Guid Generation, DateTimeOffset FirstClaimUtc);
}
