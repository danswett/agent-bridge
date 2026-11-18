using AgentBridge.Local;

namespace AgentBridge.App.Core;

public sealed partial class SettingsViewModel
{
    AgentFleetLaunchGrantState? fleetGrant;
    string? fleetGrantHost;
    public bool CanManageFleetGrant => HostRunning && host.Can(HostOps.FleetLaunchGrant) && !Busy;
    bool FleetGrantCurrent => HostRunning && fleetGrantHost == host.HostInstance;
    public bool CanRevokeFleetGrant => CanManageFleetGrant && FleetGrantCurrent && fleetGrant?.State is "active" or "expired";
    public string FleetGrantText => FleetGrantCurrent && fleetGrant is { } state
        ? state.Grant is { } grant ? $"Last read: {state.State}. Fleet-launcher, fleet roles, session.launch only. Expires {grant.ExpiresAt.ToLocalTime():g}."
            : "No agent fleet-launch grant."
        : "Grant state not checked. Native fleet launching remains staged.";

    public Task FleetLaunchGrantAsync(string action) => RunAsync(async () =>
    {
        if (!HostRunning || !host.Can(HostOps.FleetLaunchGrant)) { throw new InvalidOperationException("This host does not offer fleet-launch grants."); }
        var instance = host.HostInstance;
        object request = action switch
        {
            "read" => new { action },
            "issue" => new { action, principal = "fleet-launcher", scope = "session.launch", role = "fleet", expiresAt = host.Time.GetUtcNow().AddHours(1) },
            "revoke" when FleetGrantCurrent && fleetGrant?.Grant is { } grant => new { action, grant = grant.Id },
            _ => throw new InvalidOperationException("Read the existing grant before revoking it."),
        };
        fleetGrant = null;
        var result = await host.CallAsync<AgentFleetLaunchGrantState>(HostOps.FleetLaunchGrant, request);
        if (instance != host.HostInstance) { throw new HostRestartedException(); }
        fleetGrant = result;
        fleetGrantHost = instance;
    });
}
