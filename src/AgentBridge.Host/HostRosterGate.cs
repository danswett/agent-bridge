using AgentBridge.Roster;

namespace AgentBridge.Host;

// security.roster "enforce" (#55 R3b, #1359): whether a channel's peer may run an op, from the host's pinned and verified
// device roster (HostRosterReader.Pinned). Set on ChannelHub.RosterGate only in that mode; off and observe never reach it.
// It refuses on any doubt: no pin, a roster that is unreadable, another account's or not yet verified, a device the roster
// does not list as active, one it has removed, and, for an op in SeniorOps, a device still on probation. A refused peer is
// told "approve this machine" (ChannelHub.NotApproved) and no handler runs. SeniorOps is empty in this slice: which ops need
// a senior device is decided with the commands that use it (R6).
internal sealed class HostRosterGate(Func<RosterVerifier?> verifier, IReadOnlySet<string>? seniorOps = null)
{
    readonly IReadOnlySet<string> senior = seniorOps ?? new HashSet<string>();

    public static HostRosterGate For(HostRosterReader reader, Func<string?> account, IReadOnlySet<string>? seniorOps = null) =>
        new(() =>
        {
            try { return reader.Pinned(account()); }
            catch (InvalidOperationException) { return null; }
        }, seniorOps);

    public bool Allows(string peerId, string op)
    {
        if (verifier() is not { } v) { return false; }
        var id = peerId;
        if (!v.State.Active.TryGetValue(id, out var add) || v.Held.Contains(id)) { return false; }
        return !senior.Contains(op) || !v.OnProbation(add);
    }
}
