using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentBridge.Host.Core.Launch;

public enum LaunchRequestState { Queued, Launching, Launched, Refused, Failed }

public enum EnqueueStatus { Added, Existing, Conflict, InvalidId, Full }

// What the store keeps of a launch request: never its text (a prompt can hold anything), only a
// hash of it to tell a true repeat from the same id reused for something else.
public sealed record LaunchRequestRecord(string Id, LaunchRequestState State, string TextHash, DateTimeOffset QueuedAt, DateTimeOffset ChangedAt)
{
    public string? Session { get; init; }
    public string? Code { get; init; }
    public bool Finished => State >= LaunchRequestState.Launched;
}

public readonly record struct EnqueueResult(EnqueueStatus Status, LaunchRequestRecord? Record);

// Launch requests keyed by the caller's request id (#2743). Home Assistant's one payload slot let
// a second coordinator overwrite the first and lose its launch; here each id has its own record,
// launches at most once while its record is kept, and survives a restart in the profile folder.
public sealed class LaunchRequestStore
{
    public const string FileName = "launch-requests.json";
    public const int Version = 1;
    public const int MaxIdLength = 64;
    public const int MaxRecords = 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    readonly Lock _gate = new();
    readonly string _path;
    readonly TimeProvider _time;
    readonly int _maxPending;
    Dictionary<string, LaunchRequestRecord> _records;

    LaunchRequestStore(string path, TimeProvider time, int maxPending, Dictionary<string, LaunchRequestRecord> records) =>
        (_path, _time, _maxPending, _records) = (path, time, maxPending, records);

    // Null with a reason when the file cannot be trusted. It is kept as found, never replaced:
    // an empty store over it would forget which requests already launched and launch them again.
    public static (LaunchRequestStore? Store, string? Problem) Open(string profileFolder, TimeProvider time, int maxPending = 32)
    {
        var path = Path.Combine(profileFolder, FileName);
        try
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception absent) when (absent is FileNotFoundException or DirectoryNotFoundException)
            {
                return (new LaunchRequestStore(path, time, maxPending, new(StringComparer.Ordinal)), null);
            }
            if (Parse(bytes) is not { } records) { return (null, $"{path} is damaged; it is kept as found and no launch request is accepted"); }
            var now = time.GetUtcNow();
            // A launch under way when the host stopped may have started a session: it is never
            // tried again unasked.
            foreach (var r in records.Values.Where(r => r.State == LaunchRequestState.Launching).ToList())
            {
                records[r.Id] = r with { State = LaunchRequestState.Failed, Code = "restarted", ChangedAt = now > r.ChangedAt ? now : r.ChangedAt };
            }
            var store = new LaunchRequestStore(path, time, maxPending, records);
            store.Prune(now);
            store.Save();
            return (store, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"{path} could not be read ({e.Message}); it is kept as found and no launch request is accepted");
        }
    }

    public static bool IsValidId(string? id) => IsToken(id, MaxIdLength);

    public EnqueueResult Enqueue(string id, string launchText)
    {
        if (!IsValidId(id)) { return new(EnqueueStatus.InvalidId, null); }
        // The UTF-16 units as they are: UTF-8 would read two different lone surrogates as one U+FFFD.
        var hash = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(launchText.AsSpan())));
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (_records.TryGetValue(id, out var existing))
            {
                return new(existing.TextHash == hash ? EnqueueStatus.Existing : EnqueueStatus.Conflict, existing);
            }
            if (_records.Count >= MaxRecords || _records.Values.Count(r => !r.Finished) >= _maxPending) { return new(EnqueueStatus.Full, null); }
            var record = new LaunchRequestRecord(id, LaunchRequestState.Queued, hash, now, now);
            Commit(() => _records[id] = record);
            return new(EnqueueStatus.Added, record);
        }
    }

    public LaunchRequestRecord? Get(string id)
    {
        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            return _records.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<LaunchRequestRecord> All()
    {
        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            return Ordered();
        }
    }

    public bool Begin(string id) => Move(id, LaunchRequestState.Launching, null, null);
    public bool Launched(string id, string session) => Move(id, LaunchRequestState.Launched, Checked(session, nameof(session)), null);
    public bool Refuse(string id, string code) => Move(id, LaunchRequestState.Refused, null, Checked(code, nameof(code)));
    public bool Fail(string id, string code) => Move(id, LaunchRequestState.Failed, null, Checked(code, nameof(code)));

    // Forward only: queued to launching or straight to a refusal or failure; launching to an
    // outcome. False, changing nothing, for anything else. Times never go back with a clock set
    // back, since changedAt before queuedAt reads as damage at the next start.
    bool Move(string id, LaunchRequestState to, string? session, string? code)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (!_records.TryGetValue(id, out var r)) { return false; }
            var allowed = r.State == LaunchRequestState.Queued ? to is LaunchRequestState.Launching or LaunchRequestState.Refused or LaunchRequestState.Failed
                : r.State == LaunchRequestState.Launching && to >= LaunchRequestState.Launched;
            if (!allowed) { return false; }
            Commit(() => _records[r.Id] = r with { State = to, Session = session, Code = code, ChangedAt = now > r.ChangedAt ? now : r.ChangedAt });
            return true;
        }
    }

    // In memory only: the file drops them at its next write, and a reopen prunes them again.
    // Queued and launching records are never pruned by age: they have not happened yet.
    void Prune(DateTimeOffset now)
    {
        foreach (var r in _records.Values.Where(r => r.Finished && now - r.ChangedAt >= Retention).ToList()) { _records.Remove(r.Id); }
    }

    // The change stands only once it is on disk, so memory never claims what a restart forgets.
    void Commit(Action change)
    {
        var before = new Dictionary<string, LaunchRequestRecord>(_records, _records.Comparer);
        change();
        try { Save(); }
        catch { _records = before; throw; }
    }

    List<LaunchRequestRecord> Ordered() => [.. _records.Values.OrderBy(r => r.QueuedAt).ThenBy(r => r.Id, StringComparer.Ordinal)];

    void Save()
    {
        var partial = _path + ".tmp";
        using (var stream = File.Create(partial))
        {
            using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteStartArray("requests");
            foreach (var r in Ordered())
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteString("state", Name(r.State));
                w.WriteString("textHash", r.TextHash);
                if (r.Session is { } s) { w.WriteString("session", s); }
                if (r.Code is { } c) { w.WriteString("code", c); }
                w.WriteString("queuedAt", r.QueuedAt.ToString("o", CultureInfo.InvariantCulture));
                w.WriteString("changedAt", r.ChangedAt.ToString("o", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.Flush();
            // On the disk before the rename: a power cut never leaves a zero-filled record.
            stream.Flush(flushToDisk: true);
        }
        File.Move(partial, _path, overwrite: true);
    }

    static string Name(LaunchRequestState state) => state.ToString().ToLowerInvariant();

    // Every record whole or the file is damaged: a record dropped as unreadable could be an id
    // that already launched.
    static Dictionary<string, LaunchRequestRecord>? Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object || Json.Int(e, "version") != Version || Json.Arr(e, "requests") is not { } list) { return null; }
        var records = new Dictionary<string, LaunchRequestRecord>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            var id = Json.Str(item, "id");
            var hash = Json.Str(item, "textHash");
            var session = Json.Str(item, "session");
            var code = Json.Str(item, "code");
            var stateText = Json.Str(item, "state");
            LaunchRequestState? state = Enum.GetValues<LaunchRequestState>().Where(s => Name(s) == stateText).Select(s => (LaunchRequestState?)s).FirstOrDefault();
            if (!IsValidId(id) || state is not { } known || hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigitLower)
                || Json.Time(item, "queuedAt") is not { } queued || Json.Time(item, "changedAt") is not { } changed || changed < queued
                || (session is not null && !IsToken(session, 128)) || (code is not null && !IsToken(code, 128))
                || (known == LaunchRequestState.Launched) != (session is not null)
                || (known is LaunchRequestState.Refused or LaunchRequestState.Failed) != (code is not null)
                || !records.TryAdd(id!, new(id!, known, hash, queued, changed) { Session = session, Code = code }))
            {
                return null;
            }
        }
        return records;
    }

    // Ids, session ids and codes are short plain tokens, so nothing else a caller sends can ride
    // into the file or a log line.
    static bool IsToken(string? s, int max) =>
        s is { Length: > 0 } && s.Length <= max && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    static string Checked(string value, string name) =>
        IsToken(value, 128) ? value : throw new ArgumentException($"{name} must be 1 to 128 letters, digits, '-', '_' or '.'", name);
}
