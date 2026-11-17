using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

public sealed class FleetLaunchGrant(string profileFolder)
{
    public string? CheckDispatch(LaunchConfig actual, LaunchRequest request)
    {
        if (request.Agent is null) { return "fleet launch must name its agent"; }
        var (settings, problem) = HostSettings.Load(profileFolder);
        var approved = settings.Launch.Workspaces.SingleOrDefault(w => w.Label == request.Workspace);
        var selected = actual.Workspaces.SingleOrDefault(w => w.Label == request.Workspace);
        return problem is null && !settings.SectionProblems.Any() && approved?.Isolate == true && selected?.Isolate == true
            && Folders.Same(approved.Path, selected.Path) && actual.Agent(request.Agent).ExtraArgs.Count == 0
            ? null : "the active launch workspace or extra arguments no longer match current fleet approval";
    }

    public string? CheckGrant(AgentCredential script)
    {
        if (script.Kind != "script" || script.Scope != AgentProtocol.ScopeReadReply)
        { return "launch:fleet requires a script credential with only read+reply scope"; }
        if (!script.FleetLaunch) { return "launch:fleet grant is off; no fallback"; }
        var (policy, problem) = HostSettings.CurrentAgents(profileFolder);
        return problem ?? (policy.Enabled ? null : "agents.enabled is off");
    }

    public string? CheckLaunch(AgentCredential script, ProcessAncestry caller, LaunchRequest request)
    {
        if (CheckGrant(script) is { } denied) { return denied; }
        if (!caller.Complete || caller.Processes.Count == 0
            || caller.Processes.Any(p => p.Id <= 0 || p.StartedAt is null || string.IsNullOrWhiteSpace(p.Image)))
        { return "the script caller's ancestry must be read in full"; }
        if (AgentAncestry.Find(caller.Processes, []) is not null) { return "fleet launch is refused inside an agent session"; }
        if (request.Resume is not null || request.Transfer is not null || request.Profile is not null || request.Attachments is not null)
        { return "fleet launch cannot resume, transfer, select a profile or carry attachments"; }
        if (request.Agent is null || request.Permissions is not (LaunchProtocol.Ask or LaunchProtocol.AllowAll))
        { return "fleet launch must name its agent and permissions"; }
        if (LaunchProtocol.Check(request) is { } invalid) { return invalid; }
        var (settings, problem) = HostSettings.Load(profileFolder);
        if (problem is not null || settings.SectionProblems.Any()) { return "fleet launch settings cannot be read safely"; }
        var workspace = settings.Launch.Workspaces.SingleOrDefault(w => w.Label == request.Workspace);
        if (workspace is null) { return "fleet workspace is not listed on this machine"; }
        if (!settings.Agents.FleetWorkspaces.Contains(workspace.Label, StringComparer.Ordinal))
        { return "fleet workspace is not ticked in agents.fleet.workspaces"; }
        if (!workspace.Isolate) { return "fleet workspace must be isolated"; }
        return settings.Launch.Agent(request.Agent).ExtraArgs.Count == 0 ? null : "fleet launch refuses configured extra arguments";
    }
}
