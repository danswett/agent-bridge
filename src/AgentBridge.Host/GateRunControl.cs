namespace AgentBridge.Host;

// A person-class control entry's proof, supplied by the authenticated relay entry only (slice 5b).
internal sealed record GateControlAuthority(string Path, string Principal);

// Slice 5a of docs/design/ha-gate-counters.md (#1401, #1396): the host-internal run-control writer.
// It records the begin and void markers and assigns the run the action wiring reads from
// AgentAuditLog.ControlWriterRun. It does not pause admission, pin a roster or reach other hosts
// (5c), and nothing calls it yet: the person-class relay entry that constructs the authority is 5b.
// A run exists only after its begin marker is flushed, so an unwritable audit never yields credit.
internal sealed class GateRunControl(AgentAuditLog audit)
{
    readonly object control = new();

    // Null means refused or not recorded; the active run is unchanged unless stated.
    internal GateRunContext? Start(GateControlAuthority who)
    {
        lock (control)
        {
            return Allowed(who) && audit.ControlWriterRun is null ? Begin(who) : null;
        }
    }

    // Voids the current run and issues a new one. The old run keeps its evidence and is never zeroed.
    // If the new begin marker cannot be written there is no run (fail closed), not the old one.
    internal GateRunContext? Reset(GateControlAuthority who)
    {
        lock (control)
        {
            if (!Allowed(who) || audit.ControlWriterRun is not { } old || !Append(who, old.Run, "gate.reset", "invalidated")) { return null; }
            audit.ControlWriterRun = null;
            return Begin(who);
        }
    }

    // Never the local pipe or a shared OS identity, and never an agent or the HA connector.
    static bool Allowed(GateControlAuthority who) => who.Path == "relay" && who.Principal.StartsWith("person:", StringComparison.Ordinal) && who.Principal.Length > "person:".Length;

    GateRunContext? Begin(GateControlAuthority who)
    {
        var run = Guid.NewGuid();
        if (!Append(who, run, "gate.start", "prepared")) { return null; }
        return audit.ControlWriterRun = new GateRunContext(run);
    }

    bool Append(GateControlAuthority who, Guid run, string action, string outcome)
    {
        var operation = Guid.NewGuid();
        var gate = new GateAuditMetadata(1, operation, run, 0, "control", who.Path, "person-client", operation, "none");
        return audit.Append(new AgentAuditEntry("in", operation.ToString(), who.Principal, [], action, "profile", outcome, Gate: gate));
    }
}
