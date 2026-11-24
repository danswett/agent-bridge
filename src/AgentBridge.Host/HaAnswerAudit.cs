using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// The Home Assistant connector's answer path (#1401, #1396; HA deletion gate E4(f), docs/design/ha-gate-counters.md). An answer
// pressed on a card is audited as launches and stops from the card already are: path legacy-ha, actorKind ha-component,
// principal ha:connector, the intent flushed before any key is typed and a terminal after the confirmed delivery. Without a row
// the windowed gate could not see an HA approval as an HA fallback.
//
// OWNER DECISION (b), via the chief: an answer whose audit row cannot be written is STILL typed, never refused for the audit's sake. The failure is
// logged as audit_write_failed (host diagnostic log, plus an in-memory count the next successful audit write flushes) and the gate counts it as
// an unaudited HA dispatch, so the verdict is FAIL (design/ha-gate-counters.md).
internal static class HaAnswerAudit
{
    internal static Func<AnswerRequest, CancellationToken, Task<AnswerResult>> Wrap(ISessionAnswerer answers, AgentAuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return (request, ct) => answers.AnswerAuditedAsync(request, (operation, session, question) => Prepare(audit, operation, session, question), ct, sender: "home-assistant");
    }

    // Never refuses for the audit: with no gate identity or an unwritable audit the answer proceeds and the failure is recorded.
    static Action<AnswerOutcome>? Prepare(AgentAuditLog audit, Guid operation, Session session, Question question)
    {
        // Owner decision (b): an answer from the card is still typed when its audit row cannot be written (no gate identity, or an
        // unwritable audit); the failure is recorded as audit_write_failed and counted by the gate as an unaudited HA dispatch.
        Action<AnswerOutcome> unaudited = _ => { };
        if (SessionService.AnswerGate(session, question, operation, "ha-component") is not { } gate) { audit.RecordUnauditedHa("answer", session.Id); return unaudited; }
        gate = gate with { Path = "legacy-ha" };
        var intent = new AgentAuditEntry("local", gate.Operation.ToString(), HaRunning.Principal, [], Ops.SessionAnswer, gate.Incarnation.ToString(), "attempted", Gate: gate);
        if (!audit.Append(intent)) { audit.RecordUnauditedHa("answer", session.Id); return unaudited; }
        return outcome => audit.Append(intent with
        {
            Outcome = outcome == AnswerOutcome.Delivered ? "delivered" : "refused",
            Gate = gate with { Phase = "terminal", ReasonCode = outcome == AnswerOutcome.Delivered ? "none" : "refused" },
        });
    }
}
