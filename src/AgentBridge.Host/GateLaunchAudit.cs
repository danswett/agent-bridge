using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// Only the run-control writer assigns this context; ingress never infers it from a request or journal.
internal sealed record GateRunContext(Guid Run);
internal sealed record GateLaunchIngress(string Path, string ActorKind, string Principal, GateRunContext? Run);

internal static class GateLaunchAudit
{
    internal static async Task<LaunchResult> LaunchAsync(ISessionLauncher launcher, LaunchRequest request, string peer,
        AgentAuditLog audit, GateLaunchIngress ingress, CancellationToken ct = default, bool fleet = false, Func<string?>? refusal = null)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var id)) { return Refused("request must be a UUID"); }
        if (!audit.Bootstrap()) { return Refused("the launch audit could not establish its existing evidence"); }
        long retained = 0;
        var (rows, complete) = GateAuditProjection.Load(audit.Profile, boundary: (floor, _) => retained = floor);
        var matching = rows.Where(r => r.Action == "session.launch" && r.Request == id.ToString()).ToArray();
        var first = matching.FirstOrDefault();
        var report = GateAuditProjection.Project(rows, complete, first?.Gate?.Run ?? ingress.Run?.Run ?? Guid.Empty, first?.Gate?.Operation,
            retainedThrough: retained);
        if (!complete || (first is not null && report.Invalidated) || report.IncompleteReasons.Contains("sequence-break")
            || !audit.Verify().Intact || !audit.Verify(launch: true).Intact)
        { return Refused("the launch audit is incomplete; check for an existing launch at the machine"); }
        var binding = LaunchService.RequestBinding(request);
        if (first is not null)
        {
            if (first.Gate is not { Phase: "intent" } gate || first.Target != id.ToString() || gate.Incarnation != gate.Operation
                || first.Principal != ingress.Principal || gate.LaunchBinding != binding
                || gate.Path != ingress.Path || gate.ActorKind != ingress.ActorKind
                || matching.Any(r => r.Principal != first.Principal || r.Gate is not { } other
                    || other.Operation != gate.Operation || other.LaunchBinding != binding))
            { return Refused("the request conflicts with its durable launch binding"); }
            var terminal = matching.FirstOrDefault(r => r.Gate?.Phase == "terminal");
            if (launcher.ReplayAuditedAsync(request, peer) is { } remembered)
            {
                var result = await remembered;
                if (terminal is null || result.Outcome.ToString().ToLowerInvariant() == terminal.Outcome
                    && (result.Outcome != LaunchOutcome.Started || result.Session == terminal.Target)) { return result; }
            }
            return terminal?.Outcome switch
            {
                "started" => new(LaunchOutcome.Started) { Session = terminal.Target },
                "failed" => new(LaunchOutcome.Failed, "the launch failed before session registration"),
                "refused" => Refused("the launch was refused before the agent started"),
                "unconfirmed" => new(LaunchOutcome.Unconfirmed, "the launch outcome is not proven"),
                _ => new(LaunchOutcome.Unconfirmed, "the launch was already admitted; its registration is not proven"),
            };
        }

        string? denied = null;
        LaunchAuditCallbacks? Prepare(Guid operation)
        {
            if ((denied = refusal?.Invoke()) is not null) { return null; }
            var gate = new GateAuditMetadata(1, operation, ingress.Run?.Run ?? Guid.Empty, 0, "intent",
                ingress.Path, ingress.ActorKind, operation, "none", LaunchBinding: binding);
            var intent = new AgentAuditEntry(ingress.Path == "relay" ? "in" : "local", id.ToString(),
                ingress.Principal, [], "session.launch", id.ToString(), "reserved", Gate: gate) { Grant = fleet ? "fleet" : null };
            if (!audit.Append(intent)) { return null; }
            return new LaunchAuditCallbacks(
                session => audit.Append(intent with { Target = session, Outcome = "started", Gate = gate with { Phase = "terminal" } }),
                outcome => (outcome is "failed" or "refused")
                    && audit.Append(intent with { Outcome = outcome, Gate = gate with { Phase = "terminal", ReasonCode = outcome } }));
        }
        var outcome = fleet ? await launcher.LaunchFleetAsync(request, peer, Prepare, ct)
            : await launcher.LaunchAuditedAsync(request, peer, Prepare, ct);
        return denied is null ? outcome : Refused(denied);
    }

    internal static IEnumerable<AuditCommands.Row> FleetReservations(IReadOnlyList<AuditCommands.Row> rows)
    {
        var completed = rows.Where(row => row.Action == "session.launch" && row.Gate is { Phase: "terminal" }
            && row.Outcome is "started" or "failed" or "refused").Select(row => row.Gate!.Operation).ToHashSet();
        return rows.Where(row => row.Grant == "fleet" && row.Action == "session.launch" && row.Outcome == "reserved"
            && row.Gate is { Phase: "intent" } gate && !completed.Contains(gate.Operation));
    }

    internal static bool CloseReservation(AgentAuditLog audit, AuditCommands.Row intent)
    {
        if (intent.Action != "session.launch" || intent.Grant != "fleet" || intent.Outcome != "reserved"
            || intent.Gate is not { Phase: "intent" } gate || intent.Request is not { } request
            || intent.Target != request || !Guid.TryParseExact(request, "D", out _))
        { return false; }
        if (!audit.Verify(launch: true).Intact) { return false; }
        return audit.Append(new AgentAuditEntry(intent.Direction ?? "local", request, intent.Principal,
            intent.Chain ?? [], intent.Action, request, "failed",
            Gate: gate with { Sequence = 0, Phase = "terminal", ReasonCode = "failed" }) { Grant = intent.Grant });
    }

    static LaunchResult Refused(string reason) => new(LaunchOutcome.Refused, reason);
}
