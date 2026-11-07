using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public sealed record VerificationSelection(string Host, string Agent, string AgentLabel, string Workspace,
    string Path, bool? Isolate, string? Profile, string ProfileLabel)
{
    public string Consent => $"Open a new {AgentLabel} session in {Workspace} ({Path})"
        + (Isolate == true ? " using its approved isolated worktree" : "")
        + (Agent == "agency" ? $" with {ProfileLabel}" : "")
        + "? It will receive a harmless prompt asking for a unique reply without tools, file access or delegation. "
        + "This is a real provider request and may consume paid usage. Standard permission prompts stay on. "
        + "Answer sign-in, hook-trust and folder-trust questions yourself in its window. "
        + "Stopping observation does not stop or undo the launch; close the test window yourself if no longer needed.";
}

public sealed partial class SettingsViewModel
{
    LaunchOptions? verificationOptions;
    string? verificationHost;
    Choice? verificationAgent;
    Choice? verificationWorkspace;
    Choice? verificationProfile;
    string verificationActionStatus = "";

    public IReadOnlyList<Choice> VerificationAgents { get; private set; } = [];
    public IReadOnlyList<Choice> VerificationWorkspaces { get; private set; } = [];
    public IReadOnlyList<Choice> VerificationProfiles { get; private set; } = [];
    public Choice? VerificationAgent
    {
        get => verificationAgent;
        set
        {
            if (!Set(ref verificationAgent, value)) { return; }
            var agent = verificationOptions?.Agents.FirstOrDefault(a => a.Id == value?.Value);
            VerificationProfiles = agent?.Id == "agency"
                ? [new("", "CLI default (no Agency profile)"), .. (agent.Profiles ?? []).Select(p => new Choice(p, p))] : [];
            verificationProfile = VerificationProfiles.FirstOrDefault();
            RaiseVerification();
        }
    }
    public Choice? VerificationWorkspace { get => verificationWorkspace; set { if (Set(ref verificationWorkspace, value)) { RaiseVerification(); } } }
    public Choice? VerificationProfile { get => verificationProfile; set { if (Set(ref verificationProfile, value)) { RaiseVerification(); } } }
    public bool ShowVerificationProfiles => VerificationAgent?.Value == "agency";
    public string VerificationActionStatus { get => verificationActionStatus; private set => Set(ref verificationActionStatus, value); }
    public IReadOnlyList<AgentVerificationRow> Verifications => HostRunning && host.Can(HostOps.AgentVerificationCapability)
        ? [.. (Status?.AgentVerifications ?? []).Select(r => new AgentVerificationRow(r, !Busy))] : [];
    public bool CanRefreshVerification => HostRunning && host.Can(HostOps.AgentVerificationCapability) && !Busy;
    public bool CanLoadVerificationChoices => CanRefreshVerification && VerificationUnavailable is null
        && !(Status?.AgentVerifications?.Any(r => r.State == VerificationState.Running) ?? false);
    public bool CanRunVerification => CanLoadVerificationChoices && verificationHost == host.HostInstance
        && VerificationAgent is { } agent && VerificationAgents.Contains(agent)
        && VerificationWorkspace is { } workspace && VerificationWorkspaces.Contains(workspace)
        && (!ShowVerificationProfiles || VerificationProfile is { } profile && VerificationProfiles.Contains(profile));
    public string VerificationStateText => VerificationUnavailable
        ?? (verificationHost != host.HostInstance || verificationOptions is null
            ? "Load this host's launch choices, then explicitly choose an agent and approved workspace."
            : VerificationWorkspaces.Count == 0 ? "No workspace is approved. Add a workspace and restart the host before testing."
            : "This tests only the selected agent and workspace. It does not verify your relay or other devices. Recheck status before finishing setup.");

    string? VerificationUnavailable => !HostRunning ? "Connect to this machine's host to run a test."
        : !host.Can(HostOps.AgentVerificationCapability) ? "This host does not support new-session verification. Update the app and restart the host."
        : !host.Can(LaunchProtocol.Capability) ? "This host does not currently offer normal session launching. Configure its launch settings and explicitly restart it before testing."
        : Status?.ReadOnly is { } why ? $"Verification is unavailable while this host is read-only: {why}"
        : Status?.AgentHaBridge is { } legacy ? $"Verification is unavailable: {legacy}"
        : Status?.RestartRequired == true ? "Settings or hook setup changed. Explicitly restart the host before testing."
        : null;

    public Task LoadVerificationChoicesAsync() => RunAsync(async () =>
    {
        RequireVerification();
        var instance = host.HostInstance ?? throw new InvalidOperationException("The host instance is not known yet.");
        var options = Received.Read<LaunchOptions>(await host.CallAsync(LaunchProtocol.Options, null,
            TimeSpan.FromSeconds(90), knownHost: true, onlyTo: instance, out _));
        if (host.HostInstance != instance) { throw new HostRestartedException(); }
        verificationOptions = options;
        verificationHost = instance;
        VerificationAgents = [.. options.Agents.Where(a => LaunchProtocol.Agents.Contains(a.Id)).Select(a => new Choice(a.Id, a.Label))];
        VerificationWorkspaces = [.. options.Workspaces.Select(w => new Choice(w.Label, $"{w.Label} - {w.Path}{(w.Isolate == true ? " (isolated worktree)" : "")}"))];
        verificationAgent = verificationWorkspace = verificationProfile = null;
        VerificationProfiles = [];
        VerificationActionStatus = "";
        RaiseVerification();
    });

    public VerificationSelection CaptureVerificationSelection()
    {
        if (!CanRunVerification || verificationOptions is null || verificationHost is null) { throw new InvalidOperationException(VerificationStateText); }
        var agent = verificationOptions.Agents.Single(a => a.Id == VerificationAgent!.Value);
        var workspace = verificationOptions.Workspaces.Single(w => w.Label == VerificationWorkspace!.Value);
        return new(verificationHost, agent.Id, agent.Label, workspace.Label, workspace.Path, workspace.Isolate,
            ShowVerificationProfiles ? VerificationProfile!.Value : null, VerificationProfile?.Label ?? "");
    }

    // The confirmation names a particular host and folder, not a mutable picker or a label
    // another host/configuration could have reassigned while the dialog was open.
    public Task RunVerificationAsync(VerificationSelection selection) => RunAsync(async () =>
    {
        RequireVerification();
        var options = Received.Read<LaunchOptions>(await host.CallAsync(LaunchProtocol.Options, null,
            TimeSpan.FromSeconds(90), knownHost: true, onlyTo: selection.Host, out _));
        var agent = options.Agents.FirstOrDefault(a => a.Id == selection.Agent);
        var workspace = options.Workspaces.FirstOrDefault(w => w.Label == selection.Workspace);
        if (agent is null || workspace is null || workspace.Path != selection.Path || workspace.Isolate != selection.Isolate
            || (selection.Agent == "agency" && selection.Profile is { Length: > 0 } profile && !(agent.Profiles ?? []).Contains(profile)))
        {
            throw new InvalidOperationException("The confirmed launch choices changed. Load them again and confirm a new test; nothing was requested.");
        }
        VerificationActionStatus = "The test request may be accepted even if the connection drops. Check current status before opening another test session.";
        await host.CallAsync(HostOps.AgentsVerify,
            new AgentVerificationRequest(Guid.NewGuid().ToString("D"), selection.Agent, selection.Workspace, Confirmed: true, selection.Profile),
            TimeSpan.FromSeconds(15), knownHost: true, onlyTo: selection.Host, out _);
        VerificationActionStatus = "Test requested. Follow the current-host report below and answer any questions in the agent's window.";
        await host.RefreshStatusAsync();
    });

    public Task CancelVerificationAsync(string request) => RunAsync(async () =>
    {
        if (!HostRunning || !host.Can(HostOps.AgentVerificationCapability)) { throw new InvalidOperationException(VerificationStateText); }
        var instance = host.HostInstance ?? throw new InvalidOperationException("The host instance is not known yet.");
        VerificationActionStatus = "Stopping observation, not the agent session. Check current status if the reply is lost.";
        var result = Received.Read<AgentVerificationReport>(await host.CallAsync(HostOps.AgentsVerifyCancel,
            new AgentVerificationCancel(request), TimeSpan.FromSeconds(15), knownHost: true, onlyTo: instance, out _));
        await host.RefreshStatusAsync();
        if (result.Request != request || result.State is VerificationState.Unknown or VerificationState.Running)
        {
            throw new InvalidOperationException("The host did not confirm that observation stopped. Check current status; the session was not closed.");
        }
        VerificationActionStatus = result.State == VerificationState.Cancelled
            ? "Observation stopped. The requested launch may still open and its session was not closed."
            : $"The test had already ended ({result.State}). Its session was not closed; see the current-host report.";
    });

    public Task RefreshVerificationAsync() => RunAsync(() => host.RefreshStatusAsync());

    void RequireVerification()
    {
        if (VerificationUnavailable is { } why) { throw new InvalidOperationException(why); }
        if (Status?.AgentVerifications?.Any(r => r.State == VerificationState.Running) == true)
        {
            throw new InvalidOperationException("A verification is already running. Follow its status or stop observing it before opening another test session.");
        }
    }

    void RaiseVerification()
    {
        foreach (var name in new[] { nameof(VerificationAgent), nameof(VerificationWorkspace), nameof(VerificationProfile),
            nameof(VerificationAgents), nameof(VerificationWorkspaces), nameof(VerificationProfiles), nameof(ShowVerificationProfiles),
            nameof(CanLoadVerificationChoices), nameof(CanRunVerification), nameof(CanRefreshVerification), nameof(Verifications), nameof(VerificationStateText) }) { Raise(name); }
    }
}

public sealed class AgentVerificationRow(AgentVerificationReport report, bool canAct)
{
    public string Request => report.Request;
    public string Selection => $"{report.Agent} in {report.Workspace}{(report.Profile is { Length: > 0 } p ? $" (profile {p})" : "")}";
    public string State => $"Last reported: {report.State} at {At(report.CheckedAt)}.";
    public string Detail => report.Detail;
    public string Checks => $"Launch: {report.Launch.Detail}\nDiscovery: {report.Discovery.Detail}\nProvider: {report.Provider.Detail}\nHooks: {report.Hooks.Detail}";
    public string Evidence => $"Session: {report.Session ?? "not bound"}"
        + (report.ProviderObservedAt is { } response ? $"\nProvider reply observed: {At(response)}" : "")
        + (report.HookReceivedAt is { } hook ? $"\nHook received: {At(hook)}" : "");
    public bool CanStopObserving => canAct && report.State == VerificationState.Running;
    static string At(DateTimeOffset time) => $"{TimeZoneInfo.ConvertTimeBySystemTimeZoneId(time, "America/Los_Angeles"):g} PT";
}
