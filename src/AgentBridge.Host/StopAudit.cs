using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal static class StopAudit
{
    internal static async Task<StopResult> StopAsync(StopService stops, ISessionSource source, AgentAuditLog audit,
        StopRequest request, string path, string principal, CancellationToken ct = default, ClientIdentity? client = null)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key) || !Guid.TryParseExact(request.Id, "D", out var target)
            || target == Guid.Empty) { return Refused("the stop needs valid request and session UUIDs"); }
        var (rows, complete) = GateAuditProjection.Load(audit.Profile);
        if (!complete) { return Refused("the stop audit is incomplete; check at the machine"); }
        var prior = rows.Where(r => r.Action == Ops.SessionStop && r.Request == key.ToString()).ToArray();
        if (prior.Any(r => r.Target != target.ToString() || r.Principal != principal || r.Gate is null))
        { return Refused("the stop request belongs to another operation"); }
        var process = source.ProcessOf(request.Id);
        var incarnation = process is { StartedAt: { } started } ? Identity(target, process.Id, started) : (Guid?)null;
        var terminals = prior.Where(r => r.Gate!.Phase == "terminal").ToArray();
        if (prior.Select(r => (r.Gate!.Operation, r.Gate.Run, r.Gate.Incarnation, r.Gate.Path, r.Gate.ActorKind)).Distinct().Count() > 1
            || incarnation is { } current && prior.Any(r => r.Gate!.Incarnation != current)
            || terminals.Select(r => (r.Outcome, r.Gate!.ReasonCode)).Distinct().Count() > 1)
        { return Refused("the stop operation cannot be verified; check at the machine"); }
        if (terminals.FirstOrDefault() is { Outcome: "ended" } ended && prior.Any(r => r.Gate!.Phase == "intent"))
        { return new StopResult(StopOutcome.Ended) { Forced = ended.Gate!.ReasonCode == "forced" ? true : null }; }

        AgentAuditEntry? intent = null;
        var attempted = false;
        var conflict = false;
        var replayed = false;
        AgentAuditEntry Entry(Guid identity, string phase, string outcome, string reason) =>
            new(path == "relay" ? "in" : "local", key.ToString(), principal, [], Ops.SessionStop, target.ToString(), outcome,
                Gate: new GateAuditMetadata(1, Guid.NewGuid(), Guid.Empty, 0, phase, path,
                    path == "legacy-ha" ? "ha-component" : "person-client", identity, reason,
                    ClientKind: path == "relay" ? client?.Kind : null, ClientPlatform: path == "relay" ? client?.Platform : null));
        bool Prepare(int pid, DateTimeOffset at)
        {
            attempted = true;
            // A cache miss after restart must never replay an old or uncertain side effect.
            if (prior.Length != 0) { return false; }
            intent = Entry(Identity(target, pid, at), "intent", "attempted", "none");
            if (audit.Append(intent)) { return true; }
            intent = null;
            return false;
        }
        var result = await stops.StopAuditedAsync(request, Prepare, ct,
            principal: principal, onConflict: () => conflict = true, onReplay: () => replayed = true);
        if (conflict || replayed) { return result; }
        var outcome = result.Outcome.ToString().ToLowerInvariant();
        if (intent is { Gate: { } gate })
        {
            // Append reports failure itself; the durable intent then remains unresolved.
            audit.Append(intent with { Outcome = outcome, Gate = gate with { Phase = "terminal", ReasonCode = Reason(result) } });
        }
        else if (!attempted && prior.Length == 0 && incarnation is { } known
            && result.Outcome is StopOutcome.Refused or StopOutcome.Busy or StopOutcome.Failed)
        { audit.Append(Entry(known, "terminal", outcome, Reason(result))); }
        return result;
    }

    internal static Guid Identity(Guid session, int pid, DateTimeOffset started) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"stop/1/{session}/{pid}/{started.UtcTicks}"))).AsSpan(0, 16));

    static string Reason(StopResult result) => result.Forced == true ? "forced" : result.Outcome switch
    {
        StopOutcome.Refused => "refused", StopOutcome.Busy => "busy", StopOutcome.Failed => "failed", _ => "none",
    };

    static StopResult Refused(string reason) => new(StopOutcome.Refused, reason);
}
