using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// Claims are flushed before the first key and remain for 30 days. Request receipts
// live beside them so a resend after a restart cannot repeat a delivery.
// Since #1159 slice 0 every entry, claim and receipt alike, also says who it was admitted for
// (sender, session and question), so a lookup by UUID gives its outcome to that owner alone; a
// claim keeps its answer's first result and a hash of the answer, so the same answer from the same
// sender gets that result after a restart too; and while that result is unconfirmed, what reading
// the session's record again needs (Reread) and a key to the answer's words. The words themselves
// are never in this file, which the Codex sandbox group can read, but in AnswerWords.
public sealed class AttemptLedger
{
    internal static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    // An answer's words are held no longer than this after they were typed, however long its claim
    // is kept (Trust's condition 3).
    internal static readonly TimeSpan WordsFor = TimeSpan.FromHours(24);

    internal const string AlreadyTyped = "an answer was already typed into this prompt once, and is never typed again";
    internal const string NotRecorded = "the answer may have reached the session, but its outcome was not recorded";
    static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(15);
    static readonly JsonSerializerOptions StrictJson = new() { RespectRequiredConstructorParameters = true };
    const int Version = 1;

    // Who a request was admitted for: the sender (the channel's peer, or the connector), and the
    // session and question it answers, as the request named them.
    internal sealed record Owner(string? Sender, string Session, string Question);
    // What reading a session's record of an answer again needs, apart from the answer's words: the
    // agent, its transcript, the question's call or tool-use id, and where the transcript was read
    // to before the first key.
    internal sealed record Reread(string Agent, string Transcript, string Call, long Offset);
    // What a claim is made with. Words are kept only with a Reread, there being nothing else to use them for.
    internal sealed record Admission(Owner By, byte[] Digest, Reread? Reread = null, AnswerWords.Words? Words = null);
    // A prompt's first answer as kept here, for a host that has restarted: Reread and Words only while
    // the words may still be read again.
    internal sealed record First(Guid Request, Owner By, byte[] Digest, DateTimeOffset At, AnswerResult? Result, Reread? Reread, AnswerWords.Words? Words);

    // Entries written before #1159 have none of By, Hmac, Result, Reread or WordsKey; their owner is
    // not known, so a lookup of their UUID is nobody's (AnswerService treats it as a request of its own).
    // Hmac is the answer's hash keyed with this install's key (Seal); a plain hash written by an
    // earlier build is under another name, never read, and so never matches.
    sealed record ClaimEntry(DateTimeOffset At, Guid? Request, Owner? By = null, string? Hmac = null, AnswerResult? Result = null, Reread? Reread = null, string? WordsKey = null);
    sealed record Receipt(DateTimeOffset At, AnswerResult Result, Owner? By = null);
    sealed record FileData(int Version, Dictionary<string, ClaimEntry> Attempts, Dictionary<Guid, Receipt> Outcomes);

    readonly string? path;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly Lock gate = new();
    readonly AnswerWords words;
    // Whether the words' 24 hours are kept by a timer of their own, so that they go then with no
    // other write; off, they go at the next write, start or lookup after it.
    readonly bool expire;
    ITimer? expiry;
    DateTimeOffset? expiryAt;
    Dictionary<string, ClaimEntry>? attempts;
    Dictionary<Guid, Receipt>? outcomes;
    // Latched only for history that is corrupt; a failed write is retryable and is re-verified instead.
    string? broken;
    bool unverified;

    public event Action? Changed;

    public AttemptLedger(string? path, TimeProvider? time = null, Action<string>? log = null) : this(path, time, log, expire: true) { }

    // expire: off for a ledger an AnswerService makes for itself from a path (tests), whose clocks
    // jump to the next timer armed: one 24 hours on would run out every receipt with it.
    internal AttemptLedger(string? path, TimeProvider? time, Action<string>? log, bool expire)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        this.expire = expire;
        words = new AnswerWords(path, this.log);
    }

    // The words kept for unconfirmed answers (tests).
    internal AnswerWords WordsStore => words;

    public static string Key(string agent, string sessionId, string questionId) => $"{agent}\u001f{sessionId}\u001f{questionId}";

    public string? Claim(string key) => Claim(key, null, null);

    public string? Claim(string key, Guid? request) => Claim(key, request, null);

    // With an admission, the claim says who it is for and keeps a hash of the answer; with its
    // Reread, the answer's words are kept too, before the claim is written, or it is refused
    // (CannotHoldAnswer) and nothing is claimed.
    internal string? Claim(string key, Guid? request, Admission? admission)
    {
        lock (gate)
        {
            if (Load() is { } problem) { return problem; }
            if (attempts!.ContainsKey(key)) { return AlreadyTyped; }
            var now = time.GetUtcNow();
            // No key to hash it with: nothing may rely on its hash, so it is refused before any key
            // as one the machine can't keep safely.
            string? hash = null;
            if (admission is not null && (hash = Seal(admission.Digest)) is null) { return DeliveryReasons.CannotHoldAnswer; }
            string? held = null;
            if (admission is { Reread: not null, Words: { } said })
            {
                // Words past an exit free their room before the bounds are checked.
                Exits(now);
                if (words.Hold(said, out held) is { } refused) { return refused; }
            }
            var before = new Dictionary<string, ClaimEntry>(StringComparer.Ordinal);
            if (held is not null && request is { } asked)
            {
                // The same answer typed afresh into its question asked again (#1123) is the one read
                // again from now on: the earlier claim's words go.
                foreach (var (k, c) in attempts.Where(a => a.Value.Request == asked && a.Value.WordsKey is not null).ToList())
                {
                    before[k] = c;
                    attempts[k] = c with { Reread = null, WordsKey = null };
                }
            }
            attempts[key] = new(now, request, admission?.By, hash,
                null, held is null ? null : admission!.Reread, held);
            if (Save() is { } failed)
            {
                attempts.Remove(key);
                foreach (var (k, c) in before) { attempts[k] = c; }
                Settle();
                return failed;
            }
            Settle();
            Changed?.Invoke();
            return null;
        }
    }

    public void Release(string key)
    {
        lock (gate)
        {
            if (attempts is null || !attempts.Remove(key, out var entry)) { return; }
            // Kept for want of a write, the claim still keeps no words: they go however the write went.
            if (Save() is not null) { attempts[key] = entry with { Reread = null, WordsKey = null }; }
            else { Changed?.Invoke(); }
            Settle();
        }
    }

    // Whether the request's UUID is known here, by a receipt still remembered or by a claim, and
    // who it was admitted for: null for an entry from before #1159, whose owner is not known, and
    // for one whose hash was made with a key this install no longer has (lost or replaced).
    internal bool Known(Guid request, out Owner? by)
    {
        lock (gate)
        {
            by = null;
            if (Load() is not null) { return false; }
            var claim = attempts!.Values.Where(a => a.Request == request).MaxBy(a => a.At);
            var nobodys = claim is { Hmac: { } hash } && Opened(hash) is null;
            if (outcomes!.TryGetValue(request, out var receipt) && receipt.At >= time.GetUtcNow() - RememberFor)
            {
                by = nobodys ? null : receipt.By ?? claim?.By;
                return true;
            }
            by = nobodys ? null : claim?.By;
            return claim is not null;
        }
    }

    // The prompt's first answer kept here, for a host that does not have it in memory: the latest
    // claim admitted for that session and question, whose hash this install's key still opens.
    internal First? FirstFor(string session, string question)
    {
        lock (gate)
        {
            if (Load() is not null) { return null; }
            var now = time.GetUtcNow();
            var found = attempts!.Values
                .Where(c => c.Request is not null && c.Hmac is not null && c.By is { } by && by.Session == session && by.Question == question)
                .MaxBy(c => c.At);
            if (found is null || Opened(found.Hmac!) is not { } digest) { return null; }
            var said = Words(found, now);
            return new First(found.Request!.Value, found.By!, digest, found.At, found.Result, said is null ? null : found.Reread, said);
        }
    }

    // An answer's digest (AnswerDigest) keyed with this install's key, as the ledger keeps it and
    // AnswerService compares it: HMAC-SHA-256, so the Codex sandbox group, which can read the
    // ledger, can't confirm a guess at a short answer by hashing it. Null with no key.
    internal byte[]? Keyed(byte[] digest)
    {
        lock (gate)
        {
            Load();
            return words.Key(checkNow: false) is { } key ? HMACSHA256.HashData(key, digest) : null;
        }
    }

    // The keyed hash as written: the key's id, then the hash, so one made with a key since lost or
    // replaced is known for what it is and never matched. Made only with a key checked private now.
    string? Seal(byte[] digest) =>
        words.Key(checkNow: true) is { } key ? $"{KeyId(key)}.{Convert.ToBase64String(HMACSHA256.HashData(key, digest))}" : null;

    byte[]? Opened(string hash)
    {
        var dot = hash.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || words.Key(checkNow: false) is not { } key || hash[..dot] != KeyId(key)) { return null; }
        try { return Convert.FromBase64String(hash[(dot + 1)..]); }
        catch (FormatException) { return null; }
    }

    static string KeyId(byte[] key) => Convert.ToHexString(HMACSHA256.HashData(key, "agent-bridge answer key id"u8))[..16];

    // The latest result kept for the request's claim, as first settled or read again since.
    internal AnswerResult? ResultOf(Guid request)
    {
        lock (gate)
        {
            if (Load() is not null) { return null; }
            return attempts!.Values.Where(a => a.Request == request).MaxBy(a => a.At)?.Result;
        }
    }

    // Whether the request's answer may still be read again from its words: unconfirmed, within its
    // 24 hours, and its words held (not cancelled, nor lost to a delete that failed).
    internal bool HoldsWords(Guid request)
    {
        lock (gate)
        {
            if (Load() is not null) { return false; }
            var now = time.GetUtcNow();
            return attempts!.Values.Any(c => c.Request == request && Words(c, now) is not null);
        }
    }

    // The claims holding words. Which of them are still being typed is the answer service's to say.
    internal IReadOnlyList<(string Key, Guid Request, Owner By, Reread Reread)> Holding()
    {
        lock (gate)
        {
            if (Load() is not null) { return []; }
            return [.. attempts!.Where(a => a.Value is { WordsKey: not null, Request: not null, By: not null, Reread: not null })
                .Select(a => (a.Key, a.Value.Request!.Value, a.Value.By!, a.Value.Reread!))];
        }
    }

    // The words a claim may still be read again from, if any.
    internal AnswerWords.Words? WordsOf(string key)
    {
        lock (gate)
        {
            if (Load() is not null || !attempts!.TryGetValue(key, out var claim)) { return null; }
            return Words(claim, time.GetUtcNow());
        }
    }

    // The claim's session or question has gone: its words go.
    internal void Cancel(string key)
    {
        lock (gate)
        {
            if (Load() is not null || !attempts!.TryGetValue(key, out var claim) || claim.WordsKey is null) { return; }
            attempts[key] = claim with { Reread = null, WordsKey = null };
            Save();
            Settle();
        }
    }

    // What a read of an unconfirmed answer has come to since (the request's own receipt long gone).
    internal string? RecordReread(Guid request, AnswerResult later)
    {
        lock (gate)
        {
            if (Load() is { } problem) { return problem; }
            var changed = Advance(request, later);
            if (changed.Count == 0) { return null; }
            if (Save() is { } failed)
            {
                foreach (var (k, c) in changed) { attempts![k] = c with { Reread = null, WordsKey = null }; }
                Settle();
                return failed;
            }
            Settle();
            return null;
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
                ? new(AnswerOutcome.Unconfirmed, NotRecorded, AnswerStage.Typed)
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

    public string? RecordOutcome(Guid request, AnswerResult result) => RecordOutcome(request, result, null);

    // by: who the request was admitted for; null keeps the owner already known for it. The claim
    // it made, if any, keeps this as its first result, or as what an unconfirmed one has come to.
    internal string? RecordOutcome(Guid request, AnswerResult result, Owner? by)
    {
        lock (gate)
        {
            if (Load() is { } problem) { return problem; }
            var receipts = outcomes!;
            var before = receipts.GetValueOrDefault(request);
            var owner = by ?? before?.By ?? attempts!.Values.Where(a => a.Request == request).MaxBy(a => a.At)?.By;
            var changed = Advance(request, result);
            receipts[request] = new(time.GetUtcNow(), result, owner);
            if (Save() is { } failed)
            {
                if (before is null) { receipts.Remove(request); }
                else { receipts[request] = before; }
                foreach (var (k, c) in changed) { attempts![k] = c with { Reread = null, WordsKey = null }; }
                Settle();
                return failed;
            }
            Settle();
            return null;
        }
    }

    // Each claim of the request keeps its first result, or, while that is unconfirmed, what it has
    // come to since. Anything but unconfirmed ends its words. Returns each claim as it was.
    List<(string Key, ClaimEntry Was)> Advance(Guid request, AnswerResult result)
    {
        var claims = attempts!;
        var changed = new List<(string, ClaimEntry)>();
        foreach (var (k, c) in claims.Where(a => a.Value.Request == request).ToList())
        {
            if (c.Result is { Outcome: not AnswerOutcome.Unconfirmed }) { continue; }
            changed.Add((k, c));
            claims[k] = result.Outcome == AnswerOutcome.Unconfirmed ? c with { Result = result } : c with { Result = result, Reread = null, WordsKey = null };
        }
        return changed;
    }

    // The words a claim may still be read again from: unconfirmed or not yet settled, within its
    // 24 hours, and still held.
    AnswerWords.Words? Words(ClaimEntry claim, DateTimeOffset now) =>
        claim.WordsKey is { } key && claim.Reread is not null && Live(claim, now) ? words.Get(key) : null;

    static bool Live(ClaimEntry claim, DateTimeOffset now) =>
        claim.At >= now - WordsFor && claim.Result is null or { Outcome: AnswerOutcome.Unconfirmed };

    // Every exit an entry can have reached by now: a claim past Keep, a receipt past RememberFor,
    // and words past their 24 hours or their answer settled anything but unconfirmed.
    void Exits(DateTimeOffset now)
    {
        var claims = attempts!;
        var receipts = outcomes!;
        foreach (var old in claims.Where(a => a.Value.At < now - Keep).Select(a => a.Key).ToList()) { claims.Remove(old); }
        foreach (var old in receipts.Where(o => o.Value.At < now - RememberFor).Select(o => o.Key).ToList()) { receipts.Remove(old); }
        foreach (var (k, c) in claims.Where(a => a.Value.WordsKey is not null && !Live(a.Value, now)).ToList())
        {
            claims[k] = c with { Reread = null, WordsKey = null };
        }
    }

    bool Referenced(string key) => attempts!.Values.Any(c => c.WordsKey == key);

    // After every write, whether it reached the disk or not: words no claim refers to now go (a
    // delete that failed before is tried again), and the next 24 hours to end is timed.
    void Settle()
    {
        words.Retain(Referenced);
        Arm();
    }

    void Arm()
    {
        if (!expire) { return; }
        var soonest = attempts!.Values.Where(c => c.WordsKey is not null).Select(c => (DateTimeOffset?)(c.At + WordsFor)).Min();
        if (soonest == expiryAt) { return; }
        expiry?.Dispose();
        (expiry, expiryAt) = (null, soonest);
        if (soonest is not { } at) { return; }
        var due = at - time.GetUtcNow();
        expiry = time.CreateTimer(_ => Expire(), null, due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    void Expire()
    {
        lock (gate)
        {
            if (attempts is null) { return; }
            expiryAt = null;
            Save();
            Settle();
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
            words.Load(_ => false);
            return null;
        }
        try
        {
            var (durableAttempts, durableOutcomes, plain) = ReadDurable();
            if (attempts is null)
            {
                (attempts, outcomes) = (durableAttempts, durableOutcomes);
                // At start, before anything is served: the words of every entry past an exit, and
                // words no entry refers to, go (with every leftover temporary file).
                Exits(time.GetUtcNow());
                words.Load(Referenced);
                Arm();
            }
            else
            {
                // A write failed since the last good read: keep every claim and receipt held
                // in memory, whatever the file does or does not yet say.
                foreach (var (k, v) in durableAttempts) { attempts.TryAdd(k, v); }
                foreach (var (k, v) in durableOutcomes) { outcomes!.TryAdd(k, v); }
            }
            unverified = false;
            // A plain hash an earlier build wrote (#1184 review): never read, but on disk it is a
            // guessing target for anyone who can read this file, so the file is written again at
            // once without it, every claim kept. Until that write succeeds nothing is typed (the
            // ledger reads as unwritable, every prompt as claimed), and each later look tries again.
            if (plain && Save() is { } failed) { return failed; }
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
            // No entry can be read, so no words are kept for any.
            words.Load(_ => false);
            words.Retain(_ => false);
            return broken;
        }
    }

    // plain: a claim still holds the plain hash (Digest) an earlier build wrote, to be dropped.
    (Dictionary<string, ClaimEntry>, Dictionary<Guid, Receipt>, bool Plain) ReadDurable()
    {
        try
        {
            using var stream = Transient(() => new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read));
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new JsonException("ledger is not an object"); }
            CheckUniqueProperties(root);
            if (root.TryGetProperty(nameof(FileData.Version), out _))
            {
                var data = root.Deserialize<FileData>(StrictJson) ?? throw new JsonException("empty ledger");
                if (data.Version != Version || data.Attempts is null || data.Outcomes is null
                    || data.Attempts.Any(a => a.Value is null || a.Value.At == default || !Valid(a.Value.Result) || !Valid(a.Value.By)
                        || a.Value.Reread is { } r && (r.Agent is null || r.Transcript is null || r.Call is null))
                    || data.Outcomes.Any(o => o.Value is null || o.Value.At == default || o.Value.Result is null || !Valid(o.Value.Result) || !Valid(o.Value.By)))
                {
                    throw new JsonException("unknown or invalid ledger");
                }
                var plain = root.GetProperty(nameof(FileData.Attempts)).EnumerateObject()
                    .Any(a => a.Value.ValueKind == JsonValueKind.Object && a.Value.TryGetProperty("Digest", out _));
                return (new(data.Attempts, StringComparer.Ordinal), data.Outcomes, plain);
            }
            else
            {
                // The old format was a map from prompt key to claim time, with no request id.
                var old = root.Deserialize<Dictionary<string, DateTimeOffset>>() ?? throw new JsonException("empty ledger");
                if (old.Any(a => a.Value == default)) { throw new JsonException("invalid claim"); }
                return (old.ToDictionary(a => a.Key, a => new ClaimEntry(a.Value, null), StringComparer.Ordinal), [], false);
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (new(StringComparer.Ordinal), [], false);
        }
    }

    static bool Valid(AnswerResult? result) =>
        result is null || Enum.IsDefined(result.Outcome) && (result.Stage is not { } stage || Enum.IsDefined(stage));

    static bool Valid(Owner? by) => by is null || by.Session is not null && by.Question is not null;

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

    // On Windows a virus scanner or indexer briefly holds a file it has just seen written, so the
    // next open or replace of the ledger fails with a sharing violation for a few milliseconds
    // (#1389: a fresh request was refused "couldn't deliver it" by a read that would have
    // succeeded a moment later). A missing file is not transient, and the wait is bounded.
    internal static T Transient<T>(Func<T> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return operation(); }
            catch (Exception e) when (attempt < TransientAttempts && e is (IOException and not FileNotFoundException and not DirectoryNotFoundException) or UnauthorizedAccessException)
            {
                Thread.Sleep(TransientWait * attempt);
            }
        }
    }

    const int TransientAttempts = 6;
    static readonly TimeSpan TransientWait = TimeSpan.FromMilliseconds(20);

    string? Save()
    {
        var now = time.GetUtcNow();
        Exits(now);
        if (path is null) { return null; }
        var claims = attempts!;
        var receipts = outcomes!;
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            // The temporary file too: a scanner that has just seen it written holds it as it does the
            // ledger (#1237), and a refused save leaves the next request to a failed read.
            using (var stream = Transient(() => new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)))
            {
                JsonSerializer.Serialize(stream, new FileData(Version, claims, receipts));
                stream.Flush(flushToDisk: true);
            }
            Transient(() => { File.Move(temp, path, overwrite: true); return 0; });
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
