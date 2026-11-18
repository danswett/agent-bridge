using System.Text.Json;
using AgentBridge.Roster;
using AgentBridge.Transport;

namespace AgentBridge.Host.Fleet;

internal sealed class FleetTrust(HostRosterReader reader, string profile, Func<string?> account)
{
    public RosterVerifier? VerifiedRoster()
    {
        var scope = account();
        return scope is null ? null : reader.PinnedMembership(scope);
    }

    public async Task RefreshAsync(Func<CancellationToken, Task<RelayRoster?>> read, string scope, CancellationToken ct)
    {
        var check = reader.Update(await read(ct), scope);
        if (check.Outcome != "verified") { throw new InvalidOperationException($"refused: device roster {check.Outcome}"); }
    }

    public JsonElement Sign(JsonElement record)
    {
        var current = reader.Verified;
        var scope = account();
        return current is not null && scope is not null &&
            reader.Enrollment(scope, current.Generation, _ => true, _ => false)
                ? FleetRecordSigning.SignExisting(record, profile) : record;
    }
}
