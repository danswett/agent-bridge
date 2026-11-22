using AgentBridge.AgentEnvelope;

namespace AgentBridge.Host.Fleet.Grants;

// The only place a sealed grant.read problem is made. Each value is derived from what the receiver's own
// store reported, never from a default: None means the store read cleanly, RotationRequired means it refused with
// exactly that, Unavailable means the receiver is not ready to answer (G4, PR2 or AcceptHostSign is not in place),
// and anything else, including a store read that threw, is Other. Without a signer nothing is returned: an
// unsealed answer would be indistinguishable from a forged one.
public sealed class GrantReadProducer(Func<GrantSnapshotResult> snapshot, Func<bool> ready, Func<IEnvelopeSigner?> signer)
{
    public FleetGrantReadResult Produce(FleetGrantProofContext context)
    {
        if (context is not { Action: "grant.read" }) { throw new InvalidDataException("read-problem"); }
        var sealer = signer() ?? throw new InvalidOperationException("refused: the receiver cannot seal a read reply");
        return FleetGrantReadProof.Seal(FleetGrantReadProof.Reply(Problem()), context, sealer);
    }

    FleetGrantReadProblem Problem()
    {
        if (!ready()) { return FleetGrantReadProblem.Unavailable; }
        try
        {
            return snapshot().Problem switch
            {
                GrantStoreProblem.None => FleetGrantReadProblem.None,
                GrantStoreProblem.RotationRequired => FleetGrantReadProblem.RotationRequired,
                _ => FleetGrantReadProblem.Other
            };
        }
        catch (Exception) { return FleetGrantReadProblem.Other; }
    }
}
