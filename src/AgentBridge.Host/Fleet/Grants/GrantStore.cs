using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Transport;

namespace AgentBridge.Host.Fleet.Grants;

public enum GrantStoreProblem { None, Missing, Unreadable, Oversize, BadMac, Version, Malformed, AuditIncomplete, Generation, WriteFailed, NotFound, Revoked, NotYetValid, Expired }
public sealed record GrantStoreResult(GrantStoreProblem Problem, GrantDocument? Grant = null)
{
    public bool Usable => Problem == GrantStoreProblem.None;
}
public sealed record GrantListResult(GrantStoreProblem Problem, GrantDocument[] Grants);

// Inputs are already person-authenticated by the receiver. No ingress or acting authority is enabled here.
public sealed class GrantStore
{
    public const string FileName = "fleet-grants.bin";
    public const int MaxBytes = 1024 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 16
    };
    internal sealed record Intent(Guid Operation, Guid Grant, long Generation, string Action);
    internal sealed record State(int Version, long Generation, GrantDocument[] Grants, Guid[] Tombstones, Intent[] Intents);
    readonly string folder, path;
    readonly P256KeyPair key;
    readonly AgentAuditLog audit;
    readonly GrantScope knownOptions;
    readonly TimeProvider time;
    readonly Action<string, byte[]> write;
    GrantStoreProblem failure;

    public GrantStore(string profileFolder, P256KeyPair key, AgentAuditLog audit, GrantScope knownOptions, TimeProvider? time = null)
        : this(profileFolder, key, audit, knownOptions, time, null) { }
    internal GrantStore(string profileFolder, P256KeyPair key, AgentAuditLog audit, GrantScope knownOptions, TimeProvider? time, Action<string, byte[]>? write)
    {
        folder = Path.GetFullPath(profileFolder); path = Path.Combine(folder, FileName);
        if (folder != Path.GetFullPath(audit.Profile)) { throw new ArgumentException("audit profile mismatch", nameof(audit)); }
        this.key = key; this.audit = audit; this.time = time ?? TimeProvider.System;
        this.knownOptions = knownOptions;
        this.write = write ?? ((file, bytes) => DeviceMacFile.Write(file, bytes, message => throw new IOException(message)));
    }

    public GrantStoreResult Issue(GrantDocument acceptedGrant) => Transaction(state =>
    {
        var valid = GrantValidator.Validate(acceptedGrant, knownOptions);
        if (!valid.Valid) { return (null, new(GrantStoreProblem.Malformed)); }
        var grant = valid.Grant!;
        if (state.Tombstones.Contains(grant.Id)) { return (null, new(GrantStoreProblem.Revoked)); }
        if (state.Grants.Any(g => g.Id == grant.Id)) { return (null, new(GrantStoreProblem.Generation)); }
        grant = grant with { ExpiresAt = GrantValidator.Expiry(grant), Roles = grant.Roles.IsDefault ? [] : grant.Roles,
            StopTargets = grant.StopTargets.IsDefault ? [] : grant.StopTargets };
        return (Next(state with { Grants = [.. state.Grants, grant] }, grant.Id, "grant.issue"), new(GrantStoreProblem.None, grant));
    });

    public GrantStoreResult Revoke(Guid id, long generation) => Transaction(state =>
    {
        var result = Find(state, id, generation, checkTime: false);
        if (!result.Usable) { return (null, result); }
        return (Next(state with { Grants = state.Grants.Where(g => g.Id != id).ToArray(),
            Tombstones = [.. state.Tombstones, id] }, id, "grant.revoke"), new(GrantStoreProblem.None));
    });

    public GrantStoreResult TryGetUsable(Guid id, long generation) =>
        Transaction(state => (null, Find(state, id, generation, checkTime: true)), allowNew: false);

    public GrantListResult List()
    {
        GrantDocument[] grants = [];
        var result = Transaction(state => { grants = state.Grants; return (null, new(GrantStoreProblem.None)); },
            allowNew: false, readOnly: true);
        return new(result.Problem, grants);
    }

    static State Next(State state, Guid id, string action) => state with { Generation = state.Generation + 1,
        Intents = [.. state.Intents, new(Guid.NewGuid(), id, state.Generation + 1, action)] };
    GrantStoreResult Find(State state, Guid id, long generation, bool checkTime)
    {
        if (state.Tombstones.Contains(id)) { return new(GrantStoreProblem.Revoked); }
        var grant = state.Grants.SingleOrDefault(g => g.Id == id);
        if (grant is null) { return new(GrantStoreProblem.NotFound); }
        if (grant.Generation != generation) { return new(GrantStoreProblem.Generation); }
        var now = time.GetUtcNow();
        return checkTime && now < grant.NotBefore ? new(GrantStoreProblem.NotYetValid)
            : checkTime && now >= GrantValidator.Expiry(grant) ? new(GrantStoreProblem.Expired) : new(GrantStoreProblem.None, grant);
    }

    GrantStoreResult Transaction(Func<State, (State? Next, GrantStoreResult Result)> change, bool allowNew = true, bool readOnly = false)
    {
        if (failure != GrantStoreProblem.None) { return new(failure); }
        try
        {
            if (!readOnly) { PrivateFolder.Ensure(folder); }
            using var held = AgentAuditLog.Lock(path, read: readOnly);
            using var auditHeld = readOnly ? AgentAuditLog.Lock(Path.Combine(folder, "audit"), read: true) : null;
            if (failure != GrantStoreProblem.None) { return new(failure); }
            var state = Load(allowNew, readOnly);
            var (next, result) = change(state);
            if (next is null) { return result; }
            var body = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            if (body.Length > MaxBytes - 32 || next.Intents.Length > 256) { return new(GrantStoreProblem.Oversize); }
            var intent = next.Intents[^1];
            var metadata = new GateAuditMetadata(1, intent.Operation, Guid.Empty, 0, "intent", "local",
                "person-client", intent.Grant, "none", GrantBinding: Convert.ToHexString(SHA256.HashData(body)));
            var entry = new AgentAuditEntry("local", intent.Operation.ToString(), "person:local", [], intent.Action,
                intent.Grant.ToString(), "reserved", Gate: metadata) { Grant = intent.Grant.ToString() };
            if (!audit.Append(entry)) { return new(failure = GrantStoreProblem.WriteFailed); }
            try { write(path, [.. Mac(body), .. body]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or System.Security.SecurityException)
            { return new(failure = GrantStoreProblem.WriteFailed); }
            return audit.Append(entry with { Outcome = GateAuditProjection.SuccessFor(intent.Action)!,
                Gate = metadata with { Phase = "terminal" } }) ? result : new(failure = GrantStoreProblem.WriteFailed);
        }
        catch (StoreFault e) { return new(failure = e.Problem); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(failure = GrantStoreProblem.Unreadable); }
        catch (CryptographicException) { return new(failure = GrantStoreProblem.BadMac); }
        catch (JsonException) { return new(failure = GrantStoreProblem.Malformed); }
    }

    State Load(bool allowNew, bool readOnly = false)
    {
        byte[]? body = null;
        State state;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new StoreFault(GrantStoreProblem.Unreadable); }
            using var stream = File.OpenRead(path);
            if (!DeviceMacFile.IsPrivate(path)) { throw new StoreFault(GrantStoreProblem.Unreadable); }
            if (stream.Length > MaxBytes) { throw new StoreFault(GrantStoreProblem.Oversize); }
            if (stream.Length < 32) { throw new StoreFault(GrantStoreProblem.BadMac); }
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); body = bytes[32..];
            if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 32), Mac(body))) { throw new StoreFault(GrantStoreProblem.BadMac); }
            state = JsonSerializer.Deserialize<State>(body, Json) ?? throw new StoreFault(GrantStoreProblem.Malformed);
            if (state.Version != 1) { throw new StoreFault(GrantStoreProblem.Version); }
            if (state.Generation <= 0 || state.Grants is null || state.Tombstones is null || state.Intents is null
                || state.Intents.Length is 0 or > 256 || state.Generation != state.Intents.Length
                || state.Grants.Any(g => !GrantValidator.ValidateShape(g).Valid)
                || state.Grants.Select(g => g.Id).Distinct().Count() != state.Grants.Length
                || state.Tombstones.Any(id => id == Guid.Empty || state.Grants.Any(g => g.Id == id))
                || state.Tombstones.Distinct().Count() != state.Tombstones.Length)
            { throw new StoreFault(GrantStoreProblem.Malformed); }
        }
        catch (FileNotFoundException) { state = new(1, 0, [], [], []); }
        long retained = 0;
        var (rows, complete) = GateAuditProjection.Load(folder, alreadyLocked: readOnly, boundary: (floor, _) => retained = floor);
        if (!complete || retained > 0 || !audit.Verify(readOnly: readOnly).Intact || !audit.Verify(launch: true, readOnly: readOnly).Intact)
        { throw new StoreFault(GrantStoreProblem.AuditIncomplete); }
        var history = rows.Where(r => r.Action is "grant.issue" or "grant.revoke").ToArray();
        if (body is not null && history.Length == 0) { throw new StoreFault(GrantStoreProblem.AuditIncomplete); }
        var operations = history.GroupBy(r => r.Gate?.Operation).OrderBy(g => g.First().Gate?.Sequence).ToArray();
        if (operations.Any(g => g.Count() != 2 || g.First().Gate is not { Phase: "intent" }
            || g.Last().Gate is not { Phase: "terminal" } || g.Last().Outcome != GateAuditProjection.SuccessFor(g.First().Action)
            || g.First().Gate!.GrantBinding != g.Last().Gate!.GrantBinding))
        { throw new StoreFault(GrantStoreProblem.AuditIncomplete); }
        if (body is null && operations.Length > 0) { throw new StoreFault(GrantStoreProblem.Missing); }
        if (operations.Length != state.Generation) { throw new StoreFault(GrantStoreProblem.Generation); }
        for (var i = 0; i < operations.Length; i++)
        {
            var intent = state.Intents[i];
            if (intent is null || intent.Generation != i + 1 || operations[i].Any(r => r.Gate!.Operation != intent.Operation
                || r.Request != intent.Operation.ToString() || r.Target != intent.Grant.ToString() || r.Action != intent.Action
                || r.Gate.Incarnation != intent.Grant || r.Principal != "person:local"))
            { throw new StoreFault(GrantStoreProblem.Generation); }
        }
        if (body is not null && operations[^1].First().Gate!.GrantBinding != Convert.ToHexString(SHA256.HashData(body)))
        { throw new StoreFault(GrantStoreProblem.Generation); }
        if (body is null && !allowNew) { throw new StoreFault(GrantStoreProblem.Missing); }
        return state;
    }
    byte[] Mac(byte[] body) => DeviceMacFile.Mac(body, key, "agent-bridge fleet grant store v1"u8);
    sealed class StoreFault(GrantStoreProblem problem) : Exception { internal GrantStoreProblem Problem => problem; }
}
