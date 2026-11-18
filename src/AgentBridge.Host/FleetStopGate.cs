using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed partial class SessionService
{
    internal async Task<StopResult> StopFleetAsync(IProtocolChannel channel, JsonElement data,
        AgentCredentials credentials, AgentAuditLog? audit)
    {
        if (credentials.Of(channel) is not { Kind: "script", FleetStop: true } caller)
        { throw new InvalidOperationException("refused: a live host-granted SRE stop controller is required"); }
        StopResult Refused() => new(StopOutcome.Refused, "fleet stop grant, exact origin or current work clearance cannot be proved");
        if (data.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > 2048
            || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
            || data.EnumerateObject().Any(p => p.Name is not ("node" or "request" or "confirm")))
        { return Refused(); }
        var request = data.Deserialize<FleetStopRequest>(ProtocolJson.Options);
        if (request is null || !Guid.TryParseExact(request.Request, "D", out _)) { return Refused(); }
        var matches = source.Snapshot().Where(s => HaContract.NodeId(s.Id) == request.Node).ToArray();
        if (matches.Length != 1 || source.ProcessOf(matches[0].Id) is not { Id: > 0, StartedAt: not null } process
            || stops is not StopService stopper || audit is null || FleetStopClearance is not { } clearance) { return Refused(); }
        var session = matches[0].Id;
        var rejected = false;
        bool Current()
        {
            var (policy, problem) = HostSettings.CurrentAgents(audit.Profile);
            return problem is null && policy.Enabled && channel.IsOpen && credentials.Of(channel) == caller && ReadOnly?.Invoke() is null
                && source.Held is null && source.Get(session) is not null && source.ProcessOf(session) == process
                && source.Snapshot().Count(s => HaContract.NodeId(s.Id) == request.Node) == 1
                && launcher?.IsFleetWorker(session, process) == true;
        }
        bool Check(int pid, DateTimeOffset started)
        {
            if (rejected) { return false; }
            rejected = true;
            try
            {
                if (process.Id != pid || process.StartedAt != started || !Current()) { return false; }
                var proof = clearance.Read(session, process);
                var now = Time.GetUtcNow();
                if (proof is null || proof.Session != session || proof.Process != process || proof.CheckedAt > now
                    || now - proof.CheckedAt > TimeSpan.FromSeconds(5)
                    || new[] { proof.OpenOwnedPr, proof.UnpushedWork, proof.Held, proof.Private }
                        .Any(c => c is not { State: WorkState.Cleared } || string.IsNullOrWhiteSpace(c.Reason))
                    || !Current()) { return false; }
                rejected = false;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            { log($"fleet stop refused: evidence unavailable ({e.GetType().Name})"); return false; }
        }
        if (!Check(process.Id, process.StartedAt.Value)) { return Refused(); }
        var result = await StopAudit.StopAsync(stopper, source, audit,
            new StopRequest { Id = session, Request = request.Request, Confirm = request.Confirm },
            "local", credentials.PrincipalOf(caller), authorize: Check, actorKind: "agent");
        return rejected ? Refused() : result;
    }

    sealed record FleetStopRequest(string Node, string Request, string? Confirm);
}
