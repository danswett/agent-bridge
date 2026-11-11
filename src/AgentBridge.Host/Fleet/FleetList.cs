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
public sealed record FleetMachine(string Id, bool Online, IReadOnlyList<FleetSession> Sessions, bool Truncated = false, bool Verified = false, string? Name = null);

public sealed record FleetSession(string Node, string? Name, string State, DateTimeOffset Since, string? Id = null);

public interface IFleetRoster
{
    Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct);
}

// Reads the registry over the host's relay connection, which is the host's own account: the relay
// never sends another account's machines, so nothing here has to filter them.
public sealed class RelayFleetRoster : IFleetRoster
{
    RelayClient? relay;

    public void Attach(RelayClient? connection) => Volatile.Write(ref relay, connection);

    public async Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref relay) ?? throw new InvalidOperationException("refused: this host isn't connected to the relay");
        return Parse(await current.RegistryGetAsync(ct));
    }

    // A machine with no fresh record (offline or quiet) has no sessions to list; a frame without a machine is skipped.
    public static IReadOnlyList<FleetMachine> Parse(IEnumerable<JsonElement> frames)
    {
        var machines = new List<FleetMachine>();
        foreach (var f in frames)
        {
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
                        s.GetProperty("state").GetString()!, s.GetProperty("since").GetDateTimeOffset(), s.TryGetProperty("id", out var sid) ? sid.GetString() : null));
                }
            }
            machines.Add(new FleetMachine(id, f.TryGetProperty("online", out var on) && on.ValueKind == JsonValueKind.True, sessions, truncated, Name: name));
        }
        return machines;
    }

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

// The fleet.list pipe op (#1412, design/fleet-session-registry.md slice 4): an agent that passed
// agent.hello lists its own account's sessions. The reply is built field by field from
// FleetListSession, so nothing else the registry or a session holds can reach the agent.
public sealed class FleetList(AgentCredentials? credentials, IFleetRoster? roster, TimeProvider? time = null)
{
    public const string Op = "fleet.list";
    public const string FindOp = "fleet.find";

    // The audit hook (#1401): called once per accepted call with the agent's principal. Nothing
    // is recorded until the audit log exists and is assigned here.
    public Action<string>? AuditFleetList { get; set; }

    public async Task<object?> HandleAsync(IProtocolChannel channel, JsonElement data, CancellationToken ct = default)
    {
        var caller = Authorize(channel, Op);
        // The only member read; a principal or account in the body is ignored. Names are what a
        // person typed, so they come only when asked for (design §5.8).
        var withNames = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.String && inc.GetString() == "content";
        AuditFleetList?.Invoke(credentials!.PrincipalOf(caller));
        return new { sessions = await ListAsync(withNames, ct) };
    }

    // fleet.find (#1399, design "Names and lookup"): a filter over the same roster. The name is
    // trimmed, exact and case-sensitive, and never echoed back. Records are unsigned until
    // hostSign exists, so no machine is ever verified: a live match answers `unverified`, never
    // `found`, and only verified live matches would ever carry a node.
    public async Task<object?> FindAsync(IProtocolChannel channel, JsonElement data, CancellationToken ct = default)
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
        AuditFleetList?.Invoke(credentials!.PrincipalOf(caller));
        var scope = (await roster!.ReadAsync(ct)).Where(x => machine is null || x.Id == machine).ToList();
        var incomplete = scope.Where(x => x.Online && x.Truncated).Select(x => x.Id).ToList();
        if (incomplete.Count > 0) { return new { result = "incomplete", machines = incomplete }; }
        var hits = scope.Select(x => (Machine: x, Sessions: x.Sessions.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal)).ToList())).Where(h => h.Sessions.Count > 0).ToList();
        var live = hits.Where(h => h.Machine.Online).ToList();
        var stale = hits.Where(h => !h.Machine.Online).ToList();
        if (live.Any(h => !h.Machine.Verified)) { return new { result = "unverified", machines = live.Select(h => new FindMachine(h.Machine.Id, h.Machine.Name, Online: true)) }; }
        var cands = live.SelectMany(h => h.Sessions.Select(s => new FindSession(s.Id ?? s.Node, s.Node, h.Machine.Id, s.State, s.Since, true, h.Machine.Name))).ToList();
        var offlineCands = stale.SelectMany(h => h.Sessions.Select(s => new FindSession(s.Id ?? s.Node, null, h.Machine.Id, s.State, s.Since, false, h.Machine.Name))).ToList();
        if (cands.Count >= 2 || (cands.Count == 1 && stale.Count > 0)) { return new { result = "ambiguous", candidates = cands.Concat(offlineCands).ToList() }; }
        if (cands.Count == 0)
        {
            if (stale.Count > 0) { return new { result = "offline", machines = stale.Select(h => new FindMachine(h.Machine.Id, h.Machine.Name)).ToList() }; }
            return new { result = "notFound" };
        }
        // Only reachable once records can be verified (hostSign): one verified live match.
        if (machine is null) { return new { result = "unpinned", machines = live.Select(h => new FindMachine(h.Machine.Id, h.Machine.Name, Online: true, Verified: true, State: h.Sessions[0].State, Since: h.Sessions[0].Since)).ToList() }; }
        return new { result = "found", session = cands[0] };
    }

    sealed record FindMachine(string Machine,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MachineName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Online = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Verified = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Since = null);

    sealed record FindSession(string Id, string? Node, string Machine, string State, DateTimeOffset Since, bool Online,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MachineName);

    AgentCredential Authorize(IProtocolChannel channel, string op)
    {
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

    async Task<List<FleetListSession>> ListAsync(bool withNames, CancellationToken ct)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        return (await roster!.ReadAsync(ct)).SelectMany(m => m.Sessions.Select(s => new FleetListSession(
            m.Id, s.Node, s.State, s.Since, Math.Max(0, (long)(now - s.Since).TotalSeconds), m.Online, withNames ? s.Name : null, m.Name))).ToList();
    }
}