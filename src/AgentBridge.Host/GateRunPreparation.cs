using System.Text.Json;

namespace AgentBridge.Host;

internal static class GateRunPreparation
{
    internal const string PersonRequired = "gate controls require a host-pinned person through the authenticated relay";
    internal static bool IsControl(string op) => op is "gate.start" or "gate.pause" or "gate.reset" or "gate.seal";

    internal static object Prepare(AgentAuditLog? audit, string principal, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 1
            || !data.TryGetProperty("request", out var value) || value.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(value.GetString(), "D", out var request) || request == Guid.Empty)
        { throw new ArgumentException("gate.start accepts only a request UUID"); }
        if (audit is null) { throw Unavailable(); }
        if (audit.ControlWriterRun is not null) { throw new InvalidOperationException("gate preparation refused: a run is already active"); }
        long retained = 0;
        var (rows, complete) = GateAuditProjection.Load(audit.Profile, boundary: (floor, _) => retained = floor);
        if (!complete || retained > 0) { throw Unavailable(); }
        var matching = rows.Where(r => r.Action == "gate.start" && r.Request == request.ToString()).ToArray();
        Guid run;
        if (matching.Length > 0)
        {
            if (matching.Length != 1 || matching[0] is not { Outcome: "pending", Principal: var owner,
                Gate: { Phase: "control", Path: "relay", ActorKind: "person-client" } previous } || owner != principal)
            { throw new InvalidOperationException("gate preparation refused: conflicting request binding"); }
            run = previous.Run;
        }
        else
        {
            run = Guid.NewGuid();
            var metadata = new GateAuditMetadata(1, Guid.NewGuid(), run, 0, "control", "relay", "person-client", Guid.Empty, "none");
            if (!audit.Append(new AgentAuditEntry("in", request.ToString(), principal, [], "gate.start", "profile", "pending", Gate: metadata)))
            { throw Unavailable(); }
        }
        // This is a durable candidate, not an all-host begin marker or an admission grant.
        return new { outcome = "pending", run, reason = "prepare-barrier-unavailable" };
    }

    static InvalidOperationException Unavailable() => new("gate preparation refused: audit unavailable");
}
