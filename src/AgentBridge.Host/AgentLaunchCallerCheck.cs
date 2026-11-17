using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public sealed record AgentLaunchCallerVerdict(bool Allowed, string? Reason);

// Takes an already verified, freshly read credential; pipe wiring remains a separate slice.
public sealed class AgentLaunchCallerCheck(AgentAuditLog audit, TimeProvider? auditTime = null)
{
    readonly LocalApi.RefusalAudits refusals = new(auditTime ?? TimeProvider.System);

    public AgentLaunchCallerVerdict Check(AgentCredential caller, string principal, string request,
        string? workspace, IReadOnlyCollection<string> approvedWorkspaces)
    {
        var reason = caller.Scope != AgentProtocol.ScopeReadReplyLaunch
            ? "the caller needs read+reply+launch scope to start a session"
            : caller.Depth != 0
                ? "an agent-started session cannot start another session"
                : caller.LaunchId == AgentCredentials.AdoptedLaunch
                    ? "an adopted session cannot start another session"
                    : string.IsNullOrEmpty(workspace) || !approvedWorkspaces.Contains(workspace, StringComparer.Ordinal)
                        ? "the launch must use an approved workspace on this machine"
                        : null;
        if (reason is null) { return new(true, null); }
        try
        {
            LocalApi.AuditRefusal(principal, caller.Chain, LaunchProtocol.Launch,
                JsonSerializer.SerializeToElement(new { request }), audit, reason, refusals);
        }
        catch (InvalidOperationException)
        { return new(false, "auditUnwritable: the launch refusal could not be recorded"); }
        return new(false, reason);
    }
}
