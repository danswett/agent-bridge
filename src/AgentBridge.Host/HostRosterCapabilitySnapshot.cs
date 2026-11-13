using AgentBridge.Roster;

namespace AgentBridge.Host;

public sealed partial class HostRosterReader
{
    public HostRosterReader(string pinPath, Func<TimeSpan> clock, Func<Instant>? wallUtc) : this(pinPath, clock) { }

    // Fail-closed scaffold for the tests-first head; no evidence or replay state is written.
    internal string? RecordVerifierSupport(byte[] collection, IVerifierSupportRoster view, VerifierSupportContext context) => "transition-store";
}
