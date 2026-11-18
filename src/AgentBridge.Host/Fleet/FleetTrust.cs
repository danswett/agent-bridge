using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.Host.Fleet;

internal sealed class FleetTrust(HostRosterReader reader, string profile, Func<string?> account)
{
    public RosterVerifier? VerifiedRoster()
    {
        var current = reader.Verified;
        var scope = account();
        return current is null || scope is null ? null :
            reader.Enrollment<RosterVerifier?>(scope, current.Generation, verified => verified, _ => null);
    }

    public JsonElement Sign(JsonElement record) =>
        VerifiedRoster() is null ? record : FleetRecordSigning.SignExisting(record, profile);
}
