using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// The Home Assistant connector's answer path (#1401, #1396; HA deletion gate E4(f), docs/design/ha-gate-counters.md). An answer
// pressed on a card is audited as launches and stops from the card already are: path legacy-ha, actorKind ha-component,
// principal ha:connector, the intent flushed before any key is typed and a terminal after the confirmed delivery. Without a row
// the windowed gate could not see an HA approval as an HA fallback.
//
// OWNER CHANGE, pending: an answer whose intent cannot be written is refused (as a launch or a stop from the card already is),
// so an HA user whose audit log is unwritable can no longer approve from the card.
internal static class HaAnswerAudit
{
    internal static Func<AnswerRequest, CancellationToken, Task<AnswerResult>> Wrap(ISessionAnswerer answers, AgentAuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return (request, ct) => answers.AnswerAuditedAsync(request, (operation, session, question) => Prepare(audit, operation, session, question), ct, sender: "home-assistant");
    }

    // Null refuses: the question has no gate identity, or the intent could not be written.
    static Action<AnswerOutcome>? Prepare(AgentAuditLog audit, Guid operation, Session session, Question question)
    {
        if (SessionService.AnswerGate(session, question, operation, "ha-component") is not { } gate) { return null; }
        gate = gate with { Path = "legacy-ha" };
        var intent = new AgentAuditEntry("local", gate.Operation.ToString(), HaRunning.Principal, [], Ops.SessionAnswer, gate.Incarnation.ToString(), "attempted", Gate: gate);
        if (!audit.Append(intent)) { return null; }
        return outcome => audit.Append(intent with
        {
            Outcome = outcome == AnswerOutcome.Delivered ? "delivered" : "refused",
            Gate = gate with { Phase = "terminal", ReasonCode = outcome == AnswerOutcome.Delivered ? "none" : "refused" },
        });
    }
}
