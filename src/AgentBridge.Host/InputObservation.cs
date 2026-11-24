namespace AgentBridge.Host;

public enum InputObservationSource { Unknown, CopilotScreen, QueuedReceipt }
public enum InputObservationEvidence { Uncertain, Occupied, Empty, Queued, Missing, Ambiguous, Refused, Unavailable }
public enum InputObservationState { Unknown, Occupied, Empty, Queued }
public enum InputObservationReason { Uncertain, Observed, QueuedReceipt, Stale, Missing, Ambiguous, Refused, Unavailable }

// Pure contract projection, not an observation source or an authorization decision.
public sealed record InputObservation
{
    public Guid Session { get; }
    public InputObservationSource Source { get; }
    public long? AgeMs { get; }
    public InputObservationState State { get; }
    public InputObservationReason Reason { get; }

    InputObservation(Guid session, InputObservationSource source, long? ageMs, InputObservationState state, InputObservationReason reason)
    {
        Session = session;
        Source = source;
        AgeMs = ageMs;
        State = state;
        Reason = reason;
    }

    public static InputObservation Classify(Guid session, InputObservationSource source, InputObservationEvidence evidence,
        DateTimeOffset? observedAt, DateTimeOffset now, TimeSpan maxAge)
    {
        TimeSpan? age = observedAt is { } at && at <= now ? now - at : null;
        if (!Enum.IsDefined(source)) { source = InputObservationSource.Unknown; }
        var state = InputObservationState.Unknown;
        var reason = evidence switch
        {
            InputObservationEvidence.Missing => InputObservationReason.Missing,
            InputObservationEvidence.Ambiguous => InputObservationReason.Ambiguous,
            InputObservationEvidence.Refused => InputObservationReason.Refused,
            InputObservationEvidence.Unavailable => InputObservationReason.Unavailable,
            _ => InputObservationReason.Uncertain,
        };
        if (session == Guid.Empty) { reason = InputObservationReason.Missing; }
        else if (evidence is InputObservationEvidence.Occupied or InputObservationEvidence.Empty or InputObservationEvidence.Queued)
        {
            if (observedAt is null) { reason = InputObservationReason.Missing; }
            else if (age is null || maxAge <= TimeSpan.Zero) { reason = InputObservationReason.Uncertain; }
            else if (age > maxAge) { reason = InputObservationReason.Stale; }
            else if (source == InputObservationSource.CopilotScreen && evidence is InputObservationEvidence.Occupied or InputObservationEvidence.Empty)
            {
                state = evidence == InputObservationEvidence.Occupied ? InputObservationState.Occupied : InputObservationState.Empty;
                reason = InputObservationReason.Observed;
            }
            else if (source == InputObservationSource.QueuedReceipt && evidence == InputObservationEvidence.Queued)
            {
                state = InputObservationState.Queued;
                reason = InputObservationReason.QueuedReceipt;
            }
        }
        return new(session, source, age?.Ticks / TimeSpan.TicksPerMillisecond, state, reason);
    }
}
