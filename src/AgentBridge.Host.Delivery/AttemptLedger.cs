using System.Text.Json;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// Claims are flushed before the first key and remain for 30 days. Request receipts
// live beside them so a resend after a restart cannot repeat a delivery.
public sealed class AttemptLedger
{
    internal static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    internal const string AlreadyTyped = "an answer was already typed into this prompt once, and is never typed again";
    static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(15);
    static readonly JsonSerializerOptions StrictJson = new() { RespectRequiredConstructorParameters = true };
    const int Version = 1;

    sealed record ClaimEntry(DateTimeOffset At, Guid? Request);
    sealed record Receipt(DateTimeOffset At, AnswerResult Result);
    sealed record FileData(int Version, Dictionary<string, ClaimEntry> Attempts, Dictionary<Guid, Receipt> Outcomes);

    readonly string? path;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly Lock gate = new();
    Dictionary<string, ClaimEntry>? attempts;
    Dictionary<Guid, Receipt>? outcomes;
    // Latched only for history that is corrupt; a failed write is retryable and is re-verified instead.
    string? broken;
    bool unverified;

    public event Action? Changed;

    public AttemptLedger(string? path, TimeProvider? time = null, Action<string>? log = null)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
    }

    public static string Key(string agent, string sessionId, string questionId) => $"{agent}\u001f{sessionId}\u001f{questionId}";

    public string? Claim(string key) => Claim(key, null);

    public string? Claim(string key, Guid? request)
    {
        lock (gate)
        {
            if (Load() is { } problem) { return problem; }
            if (attempts!.ContainsKey(key)) { return AlreadyTyped; }
            attempts[key] = new(time.GetUtcNow(), request);
            if (Save() is { } failed)
            {
                attempts.Remove(key);
                return failed;
            }
            Changed?.Invoke();
            return null;
        }
    }

    public void Release(string key)
    {
        lock (gate)
        {
            if (attempts is null || !attempts.Remove(key, out var entry)) { return; }
            if (Save() is not null) { attempts[key] = entry; }
            else { Changed?.Invoke(); }
        }
    }

    public AnswerResult? Outcome(Guid request)
    {
        lock (gate)
        {
            if (Load() is { } problem) { return new(AnswerOutcome.Refused, problem); }
            var cutoff = time.GetUtcNow() - RememberFor;
            if (outcomes!.TryGetValue(request, out var receipt))
            {
                // An expired receipt must not fall through to its older claim.
                return receipt.At >= cutoff ? receipt.Result : null;
            }
            return attempts!.Values.Any(a => a.Request == request && a.At >= cutoff)
                ? new(AnswerOutcome.Unconfirmed, "the answer may have reached the session, but its outcome was not recorded", AnswerStage.Typed)
                : null;
        }
    }

    public bool HasClaim(Guid request)
    {
        lock (gate) { return attempts?.Values.Any(a => a.Request == request) == true; }
    }

    public bool IsClaimed(Session session)
    {
        if (session.Question is not { } question) { return false; }
        var agent = session.Agent switch
        {
            AgentKind.Copilot => "copilot",
            AgentKind.Claude => "claude",
            AgentKind.Codex => "codex",
            _ => null,
        };
        if (agent is null) { return false; }
        var id = session.Agent == AgentKind.Codex ? CodexApprovalId.Parse(question.Id)?.DecisionId : question.Id;
        if (id is null) { return false; }
        lock (gate) { return Load() is not null || attempts!.ContainsKey(Key(agent, session.Id, id)); }
    }

    public string? RecordOutcome(Guid request, AnswerResult result)
    {
        lock (gate)
        {
            if (Load() is { } problem) { return problem; }
            var receipts = outcomes!;
            var before = receipts.GetValueOrDefault(request);
            receipts[request] = new(time.GetUtcNow(), result);
            if (Save() is { } failed)
            {
                if (before is null) { receipts.Remove(request); }
                else { receipts[request] = before; }
                return failed;
            }
            return null;
        }
    }

    string? Load()
    {
        if (broken is not null) { return broken; }
        if (attempts is not null && !unverified) { return null; }
        if (path is null)
        {
            attempts = new(StringComparer.Ordinal);
            outcomes = [];
            return null;
        }
        try
        {
            var (durableAttempts, durableOutcomes) = ReadDurable();
            if (attempts is null) { (attempts, outcomes) = (durableAttempts, durableOutcomes); }
            else
            {
                // A write failed since the last good read: keep every claim and receipt held
                // in memory, whatever the file does or does not yet say.
                foreach (var (k, v) in durableAttempts) { attempts.TryAdd(k, v); }
                foreach (var (k, v) in durableOutcomes) { outcomes!.TryAdd(k, v); }
            }
            unverified = false;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Possibly transient (a lock, a share violation): refuse now, read again next time.
            log($"the record of answers already typed ({path}) could not be read, so nothing is typed: {e.GetType().Name}: {e.Message}");
            return DeliveryReasons.CouldNotDeliver;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            var kind = e is JsonException ? nameof(JsonException) : e.GetType().Name;
            log($"the record of answers already typed ({path}) could not be read, so nothing is typed: {kind}: {e.Message}");
            broken = DeliveryReasons.CouldNotDeliver;
            return broken;
        }
    }

    (Dictionary<string, ClaimEntry>, Dictionary<Guid, Receipt>) ReadDurable()
    {
        try
        {
            using var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new JsonException("ledger is not an object"); }
            CheckUniqueProperties(root);
            if (root.TryGetProperty(nameof(FileData.Version), out _))
            {
                var data = root.Deserialize<FileData>(StrictJson) ?? throw new JsonException("empty ledger");
                if (data.Version != Version || data.Attempts is null || data.Outcomes is null
                    || data.Attempts.Any(a => a.Value is null || a.Value.At == default)
                    || data.Outcomes.Any(o => o.Value is null || o.Value.At == default || o.Value.Result is null
                        || !Enum.IsDefined(o.Value.Result.Outcome)
                        || o.Value.Result.Stage is { } stage && !Enum.IsDefined(stage)))
                {
                    throw new JsonException("unknown or invalid ledger");
                }
                return (new(data.Attempts, StringComparer.Ordinal), data.Outcomes);
            }
            else
            {
                // The old format was a map from prompt key to claim time, with no request id.
                var old = root.Deserialize<Dictionary<string, DateTimeOffset>>() ?? throw new JsonException("empty ledger");
                if (old.Any(a => a.Value == default)) { throw new JsonException("invalid claim"); }
                return (old.ToDictionary(a => a.Key, a => new ClaimEntry(a.Value, null), StringComparer.Ordinal), []);
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (new(StringComparer.Ordinal), []);
        }
    }

    static void CheckUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new JsonException("duplicate ledger property"); }
                CheckUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) { CheckUniqueProperties(child); }
        }
    }

    string? Save()
    {
        if (path is null) { return null; }
        var now = time.GetUtcNow();
        var claims = attempts!;
        var receipts = outcomes!;
        foreach (var old in claims.Where(a => a.Value.At < now - Keep).Select(a => a.Key).ToList()) { claims.Remove(old); }
        foreach (var old in receipts.Where(o => o.Value.At < now - RememberFor).Select(o => o.Key).ToList()) { receipts.Remove(old); }
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new FileData(Version, claims, receipts));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"the attempt could not be recorded in {path}, so nothing is typed: {e.GetType().Name}: {e.Message}");
            unverified = true;
            return DeliveryReasons.CouldNotDeliver;
        }
    }
}
