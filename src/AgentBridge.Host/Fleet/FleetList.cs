using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.Fleet;

// One machine of the caller's own account, as the relay's session registry reports it.
public sealed record FleetMachine(string Id, bool Online, IReadOnlyList<FleetSession> Sessions);

public sealed record FleetSession(string Node, string? Name, string State, DateTimeOffset Since);

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
        var machines = new List<FleetMachine>();
        foreach (var f in await current.RegistryGetAsync(ct))
        {
            if (f.GetProperty("machine").GetString() is not { } id) { continue; }
            var sessions = new List<FleetSession>();
            if (f.TryGetProperty("record", out var rec) && rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in list.EnumerateArray())
                {
                    sessions.Add(new FleetSession(s.GetProperty("node").GetString()!, s.TryGetProperty("name", out var n) ? n.GetString() : null,
                        s.GetProperty("state").GetString()!, s.GetProperty("since").GetDateTimeOffset()));
                }
            }
            machines.Add(new FleetMachine(id, f.TryGetProperty("online", out var on) && on.ValueKind == JsonValueKind.True, sessions));
        }
        return machines;
    }
}

public sealed record FleetListSession(string Machine, string Node, string State, DateTimeOffset Since, long SecondsInState, bool MachineOnline, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Name);

// The fleet.list pipe op (#1412, design/fleet-session-registry.md slice 4): an agent that passed
// agent.hello lists its own account's sessions. The reply is built field by field from
// FleetListSession, so nothing else the registry or a session holds can reach the agent.
public sealed class FleetList(AgentCredentials? credentials, IFleetRoster? roster, TimeProvider? time = null)
{
    public const string Op = "fleet.list";

    // The audit hook (#1401): called once per accepted call with the agent's principal. Nothing
    // is recorded until the audit log exists and is assigned here.
    public Action<string>? AuditFleetList { get; set; }

    public async Task<object?> HandleAsync(IProtocolChannel channel, JsonElement data, CancellationToken ct = default)
    {
        if (credentials is null || !OperatingSystem.IsWindows()) { throw new InvalidOperationException("refused: fleet.list isn't available on this host"); }
        if (credentials.Of(channel) is not { } caller)
        {
            throw new InvalidOperationException("refused: fleet.list is for an agent connection that has passed agent.hello");
        }
        if (Array.IndexOf(AgentProtocol.Scopes.ToArray(), caller.Scope) < Array.IndexOf(AgentProtocol.Scopes.ToArray(), AgentProtocol.ScopeRead))
        {
            throw new InvalidOperationException($"refused: this agent's credential has scope '{caller.Scope}'; fleet.list needs at least '{AgentProtocol.ScopeRead}'");
        }
        if (roster is null) { throw new InvalidOperationException("refused: fleet.list has no session registry on this host"); }
        // The only member read; a principal or account in the body is ignored. Names are what a
        // person typed, so they come only when asked for (design §5.8).
        var withNames = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.String && inc.GetString() == "content";
        AuditFleetList?.Invoke(credentials.PrincipalOf(caller));
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var sessions = (await roster.ReadAsync(ct)).SelectMany(m => m.Sessions.Select(s => new FleetListSession(
            m.Id, s.Node, s.State, s.Since, Math.Max(0, (long)(now - s.Since).TotalSeconds), m.Online, withNames ? s.Name : null))).ToList();
        return new { sessions };
    }
}
