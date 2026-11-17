using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public enum WorkState { Unknown, Cleared, Refused }
public sealed record WorkClearance(WorkState State, string Reason);
public sealed record WorkStateClearance(string Session, SessionProcess Process, DateTimeOffset CheckedAt,
    WorkClearance OpenOwnedPr, WorkClearance UnpushedWork, WorkClearance Held, WorkClearance Private);
public interface IWorkStateClearance
{
    WorkStateClearance Read(string session, SessionProcess process);
}
public sealed class UnknownWorkStateClearance : IWorkStateClearance
{
    public WorkStateClearance Read(string session, SessionProcess process)
    {
        var unknown = new WorkClearance(WorkState.Unknown, "no work-state clearance provider is configured");
        return new(session, process, DateTimeOffset.UtcNow, unknown, unknown, unknown, unknown);
    }
}

public sealed class FleetController(AgentCredentials credentials, ISessionSource source, ISessionLauncher launcher,
    StopService? stops, AgentAuditLog audit, IWorkStateClearance? clearance = null, TimeProvider? time = null)
{
    public const string LaunchOp = "fleet.launch", StopOp = "fleet.stop";
    readonly Dictionary<string, AgentCredential> grants = new(StringComparer.Ordinal);
    readonly SemaphoreSlim operations = new(1, 1);
    readonly IWorkStateClearance work = clearance ?? new UnknownWorkStateClearance();
    readonly TimeProvider clock = time ?? TimeProvider.System;

    void Exclusive(Action action)
    {
        operations.Wait();
        try { action(); }
        finally { operations.Release(); }
    }

    async Task<T> Exclusive<T>(Func<Task<T>> action)
    {
        await operations.WaitAsync();
        try { return await action(); }
        finally { operations.Release(); }
    }

    // Host-only provisioning: neither script names nor possession of a read credential grants control.
    internal void RegisterController(AgentCredential credential) => Exclusive(() =>
    {
        if (credential.Kind != "script" || !credentials.IsLive(credential))
        { throw new InvalidOperationException("refused: a live registered script credential is required"); }
        grants[credential.Id] = credential;
    });

    internal void RevokeController(string principal) => Exclusive(() =>
    {
        foreach (var grant in grants.Where(entry => entry.Value.Principal == principal))
        { grants.Remove(grant.Key); }
    });

    AgentCredential Require(IProtocolChannel channel)
    {
        if (credentials.Of(channel) is { Kind: "script" } caller
            && grants.TryGetValue(caller.Id, out var granted) && caller == granted) { return caller; }
        throw new InvalidOperationException("refused: a live host-registered fleet-controller grant is required");
    }

    internal Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data) =>
        Exclusive(() => HandleWithGrantAsync(channel, op, data));

    async Task<object?> HandleWithGrantAsync(IProtocolChannel channel, string op, JsonElement data)
    {
        var caller = Require(channel);
        var principal = credentials.PrincipalOf(caller);
        if (data.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > LaunchProtocol.MaxRequest)
        { throw new ArgumentException("fleet control needs a bounded request object"); }
        if (op == LaunchOp)
        {
            if (credentials.Of(channel) != caller) { throw new InvalidOperationException("refused: the fleet-controller grant was revoked"); }
            var launch = data.Deserialize<LaunchRequest>(ProtocolJson.Options) ?? throw new ArgumentException("fleet.launch needs a request");
            if (launch.Resume is not null || launch.Transfer is not null || launch.Attachments is { Count: > 0 }
                || launch.Permissions is not (null or LaunchProtocol.Ask))
            { return new LaunchResult(LaunchOutcome.Refused, "fleet launches require a fresh isolated session with standard permissions and no attachments"); }
            return await GateLaunchAudit.LaunchAsync(launcher, launch with { Permissions = LaunchProtocol.Ask }, $"agent:{principal}", audit,
                new("local", "agent", principal, null, FleetController: true));
        }
        var request = data.Deserialize<FleetStopRequest>(ProtocolJson.Options) ?? throw new ArgumentException("fleet.stop needs a request");
        var matches = source.Snapshot().Where(s => HaContract.NodeId(s.Id) == request.Node).ToArray();
        if (matches.Length != 1 || source.ProcessOf(matches[0].Id) is not { Id: > 0, StartedAt: not null } process)
        { return Refused("the exact registered node and its current process are required"); }
        var session = matches[0].Id;
        string? refusal = null;
        bool Check(int pid, DateTimeOffset started)
        {
            if (credentials.Of(channel) != caller || source.Held is not null || source.Get(session) is null || source.ProcessOf(session) != process
                || process.Id != pid || process.StartedAt != started || !launcher.FleetLaunchedSession(session, process)
                || source.Snapshot().Count(s => HaContract.NodeId(s.Id) == request.Node) != 1)
            { refusal = "the verified fleet origin, exact node or current process cannot be established"; return false; }
            var state = work.Read(session, process);
            var now = clock.GetUtcNow();
            if (state is null || state.Session != session || state.Process != process || state.CheckedAt > now
                || now - state.CheckedAt > TimeSpan.FromSeconds(5))
            { refusal = "the work-state clearance is stale or belongs to another process"; return false; }
            foreach (var check in new[] { state.OpenOwnedPr, state.UnpushedWork, state.Held, state.Private })
            {
                if (check is not { State: WorkState.Cleared } || string.IsNullOrWhiteSpace(check.Reason))
                { refusal = "work-state clearance refused or unknown: " + check?.Reason; return false; }
            }
            if (credentials.Of(channel) == caller && source.Held is null && source.ProcessOf(session) == process
                && launcher.FleetLaunchedSession(session, process)
                && source.Snapshot().Count(s => HaContract.NodeId(s.Id) == request.Node) == 1) { return true; }
            refusal = "the controller credential or session discovery changed during clearance";
            return false;
        }
        if (!Check(process.Id, process.StartedAt.Value)) { return Refused(refusal ?? "session discovery changed"); }
        if (stops is null) { return Refused("this host cannot stop fleet sessions"); }
        var result = await StopAudit.StopAsync(stops, source, audit,
            new StopRequest { Id = session, Request = request.Request, Confirm = request.Confirm },
            "local", principal, authorize: Check, actorKind: "agent");
        return refusal is null ? result : Refused(refusal);
    }

    sealed record FleetStopRequest(string Node, string Request, string? Confirm);
    static StopResult Refused(string reason) => new(StopOutcome.Refused, reason);
}
