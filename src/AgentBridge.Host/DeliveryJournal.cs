using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public enum DeliveryState { Enqueued, Handoff, Typed, Delivered, Acked, Dropped, Unconfirmed, Demoted, Priority }
public sealed record DeliveryJournalRecord(string Session, DeliveryHeader Header, string State,
    DateTimeOffset RecordedAt, string Hmac, int? InjectorResult = null, bool? BoxClear = null, string? Reason = null)
{
    public PriorityCapability? Capability { get; init; }
    public string? PriorityReason { get; init; }
    public string? PriorityPrevious { get; init; }
}

// Node-bound evidence only: replay metadata, never payloads or key sends.
public sealed partial class DeliveryJournal : IDisposable
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    readonly object gate = new();
    readonly HMACSHA256 hmac;
    readonly Action<byte[]> append;
    readonly Action<string> log;
    readonly Dictionary<(string, string), DeliveryJournalRecord> latest = [];
    readonly Dictionary<(string, string), DeliveryJournalRecord> handoffs = [];
    readonly Dictionary<(string, char), long> reserved = [];
    readonly HashSet<(string, string)> replayed = [];
    readonly HashSet<(string, string)> unverified = [];
    internal bool Uncertain { get; private set; }

    public DeliveryJournal(string diagnosticsFile, Action<string> log)
        : this(HostKey(diagnosticsFile), b => Append(JournalFile(diagnosticsFile), b), log,
            Reservations(JournalFile(diagnosticsFile))) { }

    internal DeliveryJournal(byte[] key, Action<byte[]> append, Action<string> log,
        IEnumerable<DeliveryJournalRecord>? reservations = null)
    {
        hmac = new HMACSHA256(key);
        CryptographicOperations.ZeroMemory(key);
        this.append = append;
        this.log = log;
        var records = (reservations ?? []).ToArray();
        var facts = records.Where(r => r.State == "priority" && Matches(r, "")).ToArray();
        foreach (var r in records)
        {
            var validState = Enum.TryParse<DeliveryState>(r.State, true, out var state) && Enum.IsDefined(state);
            if (validState && state == DeliveryState.Priority) { ReplayPriority(r, facts); continue; }
            var refused = RefusePriorityChain(r, facts);
            if (!validState || r.Capability is not null || r.PriorityReason is not null || r.PriorityPrevious is not null ||
                r.Header.N == 0 && !Matches(r, ""))
            {
                if (refused) { UnconfirmedReplay(r); continue; }
                throw new InvalidDataException("Delivery journal evidence cannot enter legacy replay.");
            }
            var id = (r.Session, r.Header.Id);
            if (r.Header.N != 0 && (refused || state is not (DeliveryState.Enqueued or DeliveryState.Demoted)))
            { unverified.Add(id); log("delivery journal body unavailable; replay is unconfirmed and inert for authority"); }
            else { unverified.Remove(id); }
            latest[id] = r;
            replayed.Add(id);
            if (state != DeliveryState.Handoff || refused && r.Header.Seq is null) { continue; }
            _ = DeliveryEnvelope.Format(r.Header, "");
            handoffs.TryAdd(id, r);
            var seq = r.Header.Seq!;
            var lane = (r.Session, seq[0]);
            reserved[lane] = Math.Max(reserved.GetValueOrDefault(lane), long.Parse(seq[1..], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    static string JournalFile(string diagnosticsFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(diagnosticsFile))!, "delivery.jsonl");
    static IEnumerable<DeliveryJournalRecord> Reservations(string file) => !File.Exists(file) ? [] :
        File.ReadLines(file).Select(line => JsonSerializer.Deserialize<DeliveryJournalRecord>(line, ProtocolJson.Options)
            ?? throw new InvalidDataException("Invalid delivery journal reservation."));

    internal DeliveryHeader ReserveHandoff(string session, DeliveryHeader header, string body, char lane)
    {
        lock (gate)
        {
            if (lane is not ('u' or 'q')) { throw new ArgumentException("Invalid delivery lane.", nameof(lane)); }
            if (replayed.Contains((session, header.Id))) { throw new InvalidOperationException("Replayed delivery UUID cannot be handed off again."); }
            var next = checked(reserved.GetValueOrDefault((session, lane)) + 1);
            header = header with { Seq = lane + next.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            Write(session, header, body, DeliveryState.Handoff);
            return handoffs[(session, header.Id)].Header;
        }
    }

    public DeliveryState? State(string session, string id)
    {
        lock (gate)
        {
            var key = (session, id);
            return replayed.Contains(key) ? unverified.Contains(key) || handoffs.ContainsKey(key) ? DeliveryState.Unconfirmed : DeliveryState.Dropped :
                latest.TryGetValue(key, out var r) ? Enum.Parse<DeliveryState>(r.State, true) : null;
        }
    }

    internal DeliveryJournalRecord? Find(Guid request)
    {
        lock (gate) { return latest.Values.SingleOrDefault(r => r.Header.Id == request.ToString("D")); }
    }

    internal bool MatchesOffer(DeliveryJournalRecord record, string body)
    {
        lock (gate)
        {
            return Matches(handoffs.GetValueOrDefault((record.Session, record.Header.Id), record),
                DeliveryEnvelope.Prepare(record.Header, body).Body);
        }
    }

    public DeliveryState Write(string session, DeliveryHeader header, string body, DeliveryState state,
        int? injectorResult = null, bool? boxClear = null, string? reason = null)
    {
        lock (gate)
        {
            (header, body) = DeliveryEnvelope.Prepare(header, body);
            _ = DeliveryEnvelope.Format(header with { Seq = header.Seq ?? "q1" }, body);
            var id = (session, header.Id);
            latest.TryGetValue(id, out var prior);
            if (!Enum.IsDefined(state) || state == DeliveryState.Priority || state == DeliveryState.Enqueued && prior is not null ||
                state == DeliveryState.Handoff && (header.Seq is null || prior?.State is not ("enqueued" or "demoted") ||
                    prior.Header != header with { Seq = prior.Header.Seq } || !Matches(prior, body)) ||
                state == DeliveryState.Demoted && (prior?.State != "enqueued" || reason is null ||
                    prior.Header != header with { Priority = prior.Header.Priority } || !Matches(prior, body)) ||
                state is DeliveryState.Typed or DeliveryState.Delivered or DeliveryState.Acked or DeliveryState.Unconfirmed &&
                    !VerifyHandoff(session, header, body) ||
                state == DeliveryState.Acked && prior?.State != "delivered")
            {
                throw new InvalidOperationException("Delivery journal transition lacks durable evidence.");
            }
            var record = new DeliveryJournalRecord(session, header, state.ToString().ToLowerInvariant(),
                DateTimeOffset.UtcNow, "", injectorResult, boxClear, reason);
            record = record with { Hmac = Convert.ToBase64String(Mac(record, body)) };
            try { append(JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Uncertain = true;
                log($"delivery journal write failed ({e.GetType().Name}); no success acknowledgement");
                if (!handoffs.ContainsKey(id)) { throw; }
                latest[id] = record with { State = "unconfirmed" };
                return DeliveryState.Unconfirmed;
            }
            latest[id] = record;
            replayed.Remove(id);
            unverified.Remove(id);
            if (state == DeliveryState.Handoff)
            {
                handoffs[id] = record;
                var seq = header.Seq!;
                var key = (session, seq[0]);
                reserved[key] = Math.Max(reserved.GetValueOrDefault(key),
                    long.Parse(seq[1..], System.Globalization.CultureInfo.InvariantCulture));
            }
            return state;
        }
    }

    public bool VerifyHandoff(string session, DeliveryHeader header, string body)
    {
        lock (gate)
        {
            return handoffs.TryGetValue((session, header.Id), out var record) &&
                record.Header == header && Matches(record, body);
        }
    }

    bool Matches(DeliveryJournalRecord record, string body)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(record.Hmac), Mac(record with { Hmac = "" }, body));
        }
        catch (EncoderFallbackException)
        {
            log("delivery journal verification refused (invalid UTF-16); no success acknowledgement");
            return false;
        }
        catch (FormatException)
        { log("delivery journal verification refused (invalid MAC); no success acknowledgement"); return false; }
    }

    byte[] Mac(DeliveryJournalRecord record, string body) =>
        hmac.ComputeHash(StrictUtf8.GetBytes(JsonSerializer.Serialize(record, ProtocolJson.Options) + "\n" + body));

    static void Append(string file, byte[] bytes)
    {
        using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n') { throw new InvalidDataException("Delivery journal has an incomplete record; no acknowledgement."); }
        }
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(true);
    }

    static byte[] HostKey(string diagnosticsFile)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Delivery journal host keys require DPAPI."); }
        var folder = Delivery.Staging.PrivateFolder.Ensure(Path.GetDirectoryName(Path.GetFullPath(diagnosticsFile))!,
            "the delivery journal folder", "no delivery journal is opened");
        var file = Path.Combine(folder, "delivery.key");
        using var lease = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(file))
        {
            var existing = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            if (existing.Length == 32) { return existing; }
            CryptographicOperations.ZeroMemory(existing);
            throw new InvalidDataException("The delivery journal key is damaged; it is not replaced.");
        }
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var stream = Delivery.Staging.PrivateFolder.CreateNew(file);
            stream.Write(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
            stream.Flush(true);
            return key;
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    public void Dispose() { lock (gate) { hmac.Dispose(); } }
}
