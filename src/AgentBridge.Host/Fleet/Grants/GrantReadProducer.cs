using AgentBridge.AgentEnvelope;

namespace AgentBridge.Host.Fleet.Grants;

// The only place a sealed grant.read problem is made. Each value is derived from what the receiver's own
// store reported, never from a default: None means the store read cleanly, Unavailable means the receiver is not
// ready (G4, PR2 or AcceptHostSign is not in place), and anything else, including a store read that threw, is Other.
// RotationRequired lets first issuance begin, and a missing, emptied or deleted profile folder looks the same to the
// store as a never-issued one, so it is sealed only when `neverIssued` proves it from evidence held OUTSIDE the profile
// folder (no retained receiver key, no audit tip or watermark, no prior receipt reference). Unproven, or no way to
// check, is Unavailable. Without a signer nothing is returned: an unsealed answer is indistinguishable from a forged one.
public sealed class GrantReadProducer(Func<GrantSnapshotResult> snapshot, Func<bool> ready, Func<IEnvelopeSigner?> signer,
    Func<bool> neverIssued)
{
    public FleetGrantReadResult Produce(FleetGrantProofContext context)
    {
        if (context is not { Action: "grant.read" }) { throw new InvalidDataException("read-problem"); }
        var sealer = signer() ?? throw new InvalidOperationException("refused: the receiver cannot seal a read reply");
        return FleetGrantReadProof.Seal(FleetGrantReadProof.Reply(Problem()), context, sealer);
    }

    internal static FleetGrantReadProblem Map(GrantSnapshotResult read, Func<bool> neverIssued)
    {
        switch (read)
        {
            case { Problem: GrantStoreProblem.None, Grants.IsDefault: false }: return FleetGrantReadProblem.None;
            case { Problem: GrantStoreProblem.RotationRequired }:
                try { return neverIssued() ? FleetGrantReadProblem.RotationRequired : FleetGrantReadProblem.Unavailable; }
                catch (Exception) { return FleetGrantReadProblem.Unavailable; }
            default: return FleetGrantReadProblem.Other;
        }
    }

    FleetGrantReadProblem Problem()
    {
        if (!ready()) { return FleetGrantReadProblem.Unavailable; }
        try
        {
            var read = snapshot();
            // Readiness can end while the store is read; an answer sealed after that must not be actionable.
            return ready() ? Map(read, neverIssued) : FleetGrantReadProblem.Unavailable;
        }
        catch (Exception) { return FleetGrantReadProblem.Other; }
    }
}
