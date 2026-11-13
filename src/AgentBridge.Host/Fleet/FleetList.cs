using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.Fleet;

// One machine of the caller's own account, as the relay's session registry reports it.
// Verified stays false until hostSign exists (design "Signing"); Truncated is the record's own cap flag.
public sealed record FleetMachine(string Id, bool Online, IReadOnlyList<FleetSession> Sessions, bool Truncated = false, bool Verified = false, string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastSeen = null);

public sealed record FleetSession(string Node, string? Name, string State, DateTimeOffset Since, string? Id = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null);

public interface IFleetRoster
{
    Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct);
}

public sealed record FleetRosterSnapshot(string FleetScope, IReadOnlyList<FleetMachine> Machines);

public interface IScopedFleetRoster : IFleetRoster
{
    Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct);
}

// Reads the registry over the host's relay connection, which is the host's own account: the relay
// never sends another account's machines, so nothing here has to filter them.
public sealed class RelayFleetRoster : IScopedFleetRoster
{
    RelayClient? relay;

    public void Attach(RelayClient? connection) => Volatile.Write(ref relay, connection);

    public async Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => (await ReadScopedAsync(ct)).Machines;

    public async Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref relay) ?? throw new InvalidOperationException("refused: this host isn't connected to the relay");
        if (string.IsNullOrWhiteSpace(current.AccountId)) { throw new InvalidOperationException("refused: the fleet connection has no authenticated account scope"); }
        var frames = await current.RegistryGetAsync(includeKnown: true, ct: ct);
        if (!ReferenceEquals(current, Volatile.Read(ref relay))) { throw new InvalidOperationException("refused: the fleet connection changed during lookup"); }
        return new(current.AccountId, Parse(frames));
    }

    // Known-only machines have no sessions; registry liveness and supplied metadata take precedence.
    public static IReadOnlyList<FleetMachine> Parse(IEnumerable<JsonElement> frames)
    {
        var machines = new List<FleetMachine>();
        var known = new List<FleetMachine>();
        foreach (var f in frames)
        {
            if (f.TryGetProperty("known", out var k) && k.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in k.EnumerateArray())
                {
                    if (!entry.TryGetProperty("machine", out var km) || km.GetString() is not { } knownId) { continue; }
                    known.Add(new FleetMachine(knownId, entry.TryGetProperty("online", out var knownOnline) && knownOnline.ValueKind == JsonValueKind.True, [],
                        Name: MachineName(entry), LastSeen: LastSeen(entry)));
                }
            }
            if (!f.TryGetProperty("machine", out var m) || m.GetString() is not { } id) { continue; }
            var name = MachineName(f);
            var sessions = new List<FleetSession>();
            var truncated = false;
            if (f.TryGetProperty("record", out var rec) && rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                truncated = rec.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True;
                foreach (var s in list.EnumerateArray())
                {
                    sessions.Add(new FleetSession(s.GetProperty("node").GetString()!, s.TryGetProperty("name", out var n) ? n.GetString() : null,
                        s.GetProperty("state").GetString()!, s.GetProperty("since").GetDateTimeOffset(), s.TryGetProperty("id", out var sid) ? sid.GetString() : null,
                        s.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : null));
                }
            }
            machines.Add(new FleetMachine(id, f.TryGetProperty("online", out var on) && on.ValueKind == JsonValueKind.True, sessions, truncated, Name: name, LastSeen: LastSeen(f)));
        }
        foreach (var entry in known)
        {
            var index = machines.FindIndex(m => m.Id == entry.Id);
            if (index < 0) { machines.Add(entry); }
            else
            {
                var machine = machines[index];
                // A fallback name must not change legacy session or find replies.
                machines[index] = machine with
                {
                    Name = machine.Name ?? (machine.Sessions.Count == 0 ? entry.Name : null),
                    LastSeen = machine.LastSeen ?? entry.LastSeen,
                };
            }
        }
        return machines;
    }

    static DateTimeOffset? LastSeen(JsonElement frame) =>
        frame.TryGetProperty("lastSeen", out var value) && value.ValueKind != JsonValueKind.Null ? value.GetDateTimeOffset() : null;

    static string? MachineName(JsonElement frame)
    {
        if (!frame.TryGetProperty("name", out var value) || value.ValueKind != JsonValueKind.String) { return null; }
        string? name;
        // GetString rejects unpaired JSON surrogate escapes before rune validation can inspect them.
        try { name = value.GetString(); }
        catch (InvalidOperationException) { return null; }
        if (name is not { Length: > 0 and <= 64 }) { return null; }
        var rest = name.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) { return null; }
            rest = rest[used..];
        }
        var trimmed = name.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }
}

public sealed record FleetListSession(string Machine, string Node, string State, DateTimeOffset Since, long SecondsInState, bool MachineOnline,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MachineName = null);

public sealed record FleetListMachine(string Machine, string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MachineName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastSeen);

// The fleet.list pipe op (#1412, design/fleet-session-registry.md slice 4): an agent that passed
// agent.hello lists its own account's sessions. The reply is built field by field from
// FleetListSession, so nothing else the registry or a session holds can reach the agent.
public sealed class FleetList(AgentCredentials? credentials, IFleetRoster? roster, TimeProvider? time = null, AgentAuditLog? audit = null)
{
    public const string Op = "fleet.list";
    public const string FindOp = "fleet.find";

    // Notification only: the required audit append succeeds before this hook or any roster read.
    public Action<string>? AuditFleetList { get; set; }

    public async Task<object?> HandleAsync(IProtocolChannel channel, JsonElement data, CancellationToken ct = default, AgentAuditLog? auditLog = null)
    {
        var caller = Authorize(channel, Op);
        // The only member read; a principal or account in the body is ignored. Names are what a
        // person typed, so they come only when asked for (design §5.8).
        var withNames = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.String && inc.GetString() == "content";
        LocalApi.AuditAgentAction(credentials!, caller, Op, "fleet", "attempted", auditLog ?? audit);
        AuditFleetList?.Invoke(credentials!.PrincipalOf(caller));
        var result = await ListAsync(withNames, ct);
        credentials!.CompleteFleetRead(caller, result);
        return result;
    }

    // fleet.find (#1399, design "Names and lookup"): a filter over the same roster. The name is
    // trimmed, exact and case-sensitive, and never echoed back. Records are unsigned until
    // hostSign exists, so a real live match still answers `unverified`. A unique verified live
    // match needs no machine pin; its scope and optional model come only from the scoped roster.
    public async Task<object?> FindAsync(IProtocolChannel channel, JsonElement data, CancellationToken ct = default, AgentAuditLog? auditLog = null)
    {
        var caller = Authorize(channel, FindOp);
        var name = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
        if (name.Length is 0 or > 64) { throw new InvalidOperationException("refused: fleet.find needs a \"name\" of 1 to 64 characters"); }
        string? machine = null;
        if (data.TryGetProperty("machine", out var m) && m.ValueKind != JsonValueKind.Null)
        {
            machine = m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (machine is null || machine.Length is 0 or > 64 || !machine.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            {
                throw new InvalidOperationException("refused: fleet.find \"machine\" must be 1 to 64 of letters, digits, '.', '_', '-'");
            }
        }
        LocalApi.AuditAgentAction(credentials!, caller, FindOp, "fleet", "attempted", auditLog ?? audit);
        AuditFleetList?.Invoke(credentials!.PrincipalOf(caller));
        if (roster is not IScopedFleetRoster scoped) { throw new InvalidOperationException("refused: fleet.find needs an authenticated account-scoped registry"); }
        var snapshot = await scoped.ReadScopedAsync(ct);
        var fleetScope = snapshot.FleetScope;
        if (string.IsNullOrWhiteSpace(fleetScope)) { throw new InvalidOperationException("refused: fleet.find has no authenticated account scope"); }
        var result = Find(snapshot, name, machine);
        credentials!.CompleteFleetRead(caller, result);
        return result;
    }

    internal static object Find(FleetRosterSnapshot snapshot, string name, string? machine)
    {
        var fleetScope = snapshot.FleetScope;
        var scope = snapshot.Machines.Where(x => machine is null || x.Id == machine).ToList();
        var incomplete = scope.Where(x => x.Online && x.Truncated).Select(x => x.Id).ToList();
        if (incomplete.Count > 0) { return new { result = "incomplete", fleetScope, machines = incomplete }; }
        var hits = scope.Select(x => (Machine: x, Sessions: x.Sessions.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal)).ToList())).Where(h => h.Sessions.Count > 0).ToList();
        var live = hits.Where(h => h.Machine.Online).ToList();
        var stale = hits.Where(h => !h.Machine.Online).ToList();
        if (live.Any(h => !h.Machine.Verified)) { return new { result = "unverified", fleetScope, machines = live.Select(h => new FindMachine(h.Machine.Id, h.Machine.Name, Online: true)) }; }
        var cands = live.SelectMany(h => h.Sessions.Select(s => new FindSession(s.Id ?? s.Node, s.Node, h.Machine.Id, s.State, s.Since, true, h.Machine.Name, s.Model))).ToList();
        if (cands.Count >= 2) { return new { result = "ambiguous", fleetScope, candidates = cands }; }
        if (cands.Count == 0)
        {
            if (stale.Count > 0) { return new { result = "offline", fleetScope, machines = stale.Select(h => new FindMachine(h.Machine.Id, h.Machine.Name)).ToList() }; }
            return new { result = "notFound", fleetScope };
        }
        // Only reachable once records can be verified (hostSign): one verified live match.
        return new { result = "found", fleetScope, session = cands[0] };
    }

    sealed record FindMachine(string Machine,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MachineName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Online = null);

    sealed record FindSession(string Id, string? Node, string Machine, string State, DateTimeOffset Since, bool Online,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MachineName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model);

    AgentCredential Authorize(IProtocolChannel channel, string op)
    {
        credentials?.CheckScriptOperation(channel, op);
        if (credentials is null || !OperatingSystem.IsWindows()) { throw new InvalidOperationException($"refused: {op} isn't available on this host"); }
        if (credentials.Of(channel) is not { } caller)
        {
            throw new InvalidOperationException($"refused: {op} is for an agent connection that has passed agent.hello");
        }
        if (Array.IndexOf(AgentProtocol.Scopes.ToArray(), caller.Scope) < Array.IndexOf(AgentProtocol.Scopes.ToArray(), AgentProtocol.ScopeRead))
        {
            throw new InvalidOperationException($"refused: this agent's credential has scope '{caller.Scope}'; {op} needs at least '{AgentProtocol.ScopeRead}'");
        }
        if (roster is null) { throw new InvalidOperationException($"refused: {op} has no session registry on this host"); }
        return caller;
    }

    async Task<object> ListAsync(bool withNames, CancellationToken ct)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var machines = await roster!.ReadAsync(ct);
        return new
        {
            sessions = machines.SelectMany(m => m.Sessions.Select(s => new FleetListSession(
                m.Id, s.Node, s.State, s.Since, Math.Max(0, (long)(now - s.Since).TotalSeconds), m.Online, withNames ? s.Name : null, m.Name))).ToList(),
            machines = machines.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => new FleetListMachine(
                m.Id, m.Online ? "online" : "offline", m.Name, m.LastSeen)).ToList(),
        };
    }
}