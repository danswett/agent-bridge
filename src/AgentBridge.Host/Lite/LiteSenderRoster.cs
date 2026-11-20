using System.Security.Cryptography;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;

namespace AgentBridge.Host.Lite;

// TEMPORARY: declared here only so this seam builds before brief 171 lands. 171 declares this exact
// contract in LiteReplyGate.cs; this declaration is deleted in the plain main merge that brings it in.
public interface ILiteSenderRoster { bool IsRosterVerified(VerifiedEnvelope env); }

// The relay A-roster check (#740): a verified lite sender counts only when security.roster is on and
// the sending machine is a serving host on this host's pinned, verified roster, bound by the same
// device key. `rosterOn` is the security.roster switch and `pinned` the pinned roster
// (HostRosterReader.Pinned); both are read per call, so turning the roster off takes effect at once.
// Every doubt refuses: off, no pin, an unreadable pin, an empty roster, another account, a device
// that is not a serving host, or a key that is not the roster's. Nothing here logs roster contents.
public sealed class LiteSenderRoster(Func<bool> rosterOn, Func<RosterVerifier?> pinned) : ILiteSenderRoster
{
    public bool IsRosterVerified(VerifiedEnvelope env)
    {
        try
        {
            if (!rosterOn() || pinned() is not { } roster) { return false; }
            var body = env.Body;
            var principal = body.Principal;
            // Ordinal and exact: the roster's ids are lowercase hex and its keys are fixed bytes, so a
            // principal differing in case or whitespace is a different, unlisted sender.
            if (!string.Equals(body.Account, roster.Account, StringComparison.Ordinal) || !Codec.IsId(principal.Machine)) { return false; }
            // The roster reader holds the verifier's lock while it changes it (as FleetList reads it).
            lock (roster)
            {
                if (!roster.Serving.TryGetValue(principal.Machine, out var add)) { return false; }
                // principal.add is SHA-256 of the sender's device static key (LiteReceiver checks it
                // against the channel), and the roster's dh is that same key, so both must agree.
                return roster.Graph[add].Body.Device is { Kind: RosterKinds.Host } device
                    && string.Equals(Codec.Base64Url(SHA256.HashData(device.Dh)), principal.Add, StringComparison.Ordinal);
            }
        }
        catch (Exception)
        {
            // A pin that cannot be read throws (HostRosterReader.Pinned); so might a switch. Fail closed.
            return false;
        }
    }
}
