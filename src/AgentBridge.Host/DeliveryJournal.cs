using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public enum DeliveryState { Enqueued, Handoff, Typed, Delivered, Acked, Dropped, Unconfirmed }
public sealed record DeliveryJournalRecord(string Session, DeliveryHeader Header, string State,
    DateTimeOffset RecordedAt, string Hmac, int? InjectorResult = null, bool? BoxClear = null);

// Node-bound evidence only: no payload retention, sequence reservation, replay or key sends.
public sealed class DeliveryJournal : IDisposable
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    readonly object gate = new();
    readonly HMACSHA256 hmac;
    readonly Action<byte[]> append;
    readonly Action<string> log;
    readonly Dictionary<(string, string), DeliveryJournalRecord> latest = [];
    readonly Dictionary<(string, string), DeliveryJournalRecord> handoffs = [];

    public DeliveryJournal(string diagnosticsFile, Action<string> log)
        : this(HostKey(diagnosticsFile), b => Append(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(diagnosticsFile))!, "delivery.jsonl"), b), log) { }

    internal DeliveryJournal(byte[] key, Action<byte[]> append, Action<string> log)
    {
        hmac = new HMACSHA256(key);
        CryptographicOperations.ZeroMemory(key);
        this.append = append;
        this.log = log;
    }

    public DeliveryState? State(string session, string id)
    {
        lock (gate) { return latest.TryGetValue((session, id), out var r) ? Enum.Parse<DeliveryState>(r.State, true) : null; }
    }

    public DeliveryState Write(string session, DeliveryHeader header, string body, DeliveryState state,
        int? injectorResult = null, bool? boxClear = null)
    {
        lock (gate)
        {
            (header, body) = DeliveryEnvelope.Prepare(header, body);
            _ = DeliveryEnvelope.Format(header with { Seq = header.Seq ?? "q1" }, body);
            var id = (session, header.Id);
            latest.TryGetValue(id, out var prior);
            if (!Enum.IsDefined(state) || state == DeliveryState.Enqueued && prior is not null ||
                state == DeliveryState.Handoff && (header.Seq is null || prior?.State != "enqueued" ||
                    prior.Header != header with { Seq = prior.Header.Seq } || !Matches(prior, body)) ||
                state is DeliveryState.Typed or DeliveryState.Delivered or DeliveryState.Acked or DeliveryState.Unconfirmed &&
                    !VerifyHandoff(session, header, body) ||
                state == DeliveryState.Acked && prior?.State != "delivered")
            {
                throw new InvalidOperationException("Delivery journal transition lacks durable evidence.");
            }
            var record = new DeliveryJournalRecord(session, header, state.ToString().ToLowerInvariant(),
                DateTimeOffset.UtcNow, "", injectorResult, boxClear);
            record = record with { Hmac = Convert.ToBase64String(Mac(record, body)) };
            try { append(JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log($"delivery journal write failed ({e.GetType().Name}); no success acknowledgement");
                if (!handoffs.ContainsKey(id)) { throw; }
                latest[id] = record with { State = "unconfirmed" };
                return DeliveryState.Unconfirmed;
            }
            latest[id] = record;
            if (state == DeliveryState.Handoff) { handoffs[id] = record; }
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
