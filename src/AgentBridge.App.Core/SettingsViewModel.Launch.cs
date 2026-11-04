using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public sealed partial class SettingsViewModel
{
    HostLaunchSettings? launchWas;
    HostLaunchSettings? launchDraft;
    bool launchConflict;
    string workspaceLabel = "";
    string workspaceFolder = "";
    bool workspaceIsolated;
    string launchSettingsStatus = "";
    IReadOnlyList<string> defaultWorkspaces = [];
    bool showingLaunchSettings;

    public bool CanEditLaunchSettings => HostRunning && host.Can(HostOps.LaunchSettingsCapability) && launchDraft is not null
        && !Busy && Status?.ReadOnly is null && Status?.AgentHaBridge is null && Status?.Settings.LaunchProblem is null;
    public bool CanReloadLaunchSettings => HostRunning && host.Can(HostOps.LaunchSettingsCapability) && !Busy;
    public bool CanSaveLaunchSettings => CanEditLaunchSettings && launchDraft != launchWas && !LaunchChangedElsewhere;
    public bool LaunchChangedElsewhere => launchConflict || launchWas is not null && Status?.Settings.Launch is { } current && current != launchWas;
    public string LaunchSettingsState => !HostRunning ? "Connect to this machine's host to edit workspaces and defaults."
        : !host.Can(HostOps.LaunchSettingsCapability) ? "This host does not support launch settings. Update the app and restart the host."
        : Status?.ReadOnly is not null || Status?.AgentHaBridge is not null ? $"Launch settings are unavailable while this host is read-only: {Status.ReadOnly ?? Status.AgentHaBridge}."
        : Status?.Settings.LaunchProblem is { } problem ? problem
        : LaunchChangedElsewhere ? "Launch settings changed elsewhere. Reload and review before saving; your draft has not been applied."
        : "Launch or resume in the exact approved folders, or their managed isolated worktrees. Subfolders need separate approval. Changes apply after a host restart.";
    public string LaunchSettingsStatus { get => launchSettingsStatus; private set => Set(ref launchSettingsStatus, value); }
    public IReadOnlyList<ApprovedWorkspace> Workspaces => launchDraft?.Workspaces ?? [];
    public IReadOnlyList<string> DefaultAgents { get; } = ["auto", .. LaunchProtocol.Agents];
    public IReadOnlyList<string> DefaultWorkspaces => defaultWorkspaces;
    public string WorkspaceLabel { get => workspaceLabel; set => Set(ref workspaceLabel, value); }
    public string WorkspaceFolder { get => workspaceFolder; set => Set(ref workspaceFolder, value); }
    public bool WorkspaceIsolated { get => workspaceIsolated; set => Set(ref workspaceIsolated, value); }

    public bool LaunchEnabled
    {
        get => launchDraft?.Enabled ?? true;
        set { if (!showingLaunchSettings && CanEditLaunchSettings && launchDraft is { } draft && draft.Enabled != value) { launchDraft = draft with { Enabled = value }; LaunchDraftChanged(); } }
    }

    public string DefaultAgent
    {
        get => launchDraft?.DefaultAgent ?? "auto";
        set { if (!showingLaunchSettings && CanEditLaunchSettings && launchDraft is { } draft && draft.DefaultAgent != value) { launchDraft = draft with { DefaultAgent = value }; LaunchDraftChanged(); } }
    }

    public string DefaultWorkspace
    {
        get => launchDraft?.DefaultWorkspace ?? "";
        set
        {
            var next = string.IsNullOrEmpty(value) ? null : value;
            if (!showingLaunchSettings && CanEditLaunchSettings && launchDraft is { } draft && draft.DefaultWorkspace != next)
            {
                launchDraft = draft with { DefaultWorkspace = next };
                LaunchDraftChanged();
            }
        }
    }

    public bool UnrestrictedPermissions
    {
        get => launchDraft?.Permissions == LaunchProtocol.AllowAll;
        set
        {
            if (!showingLaunchSettings && CanEditLaunchSettings && launchDraft is { } draft && (draft.Permissions == LaunchProtocol.AllowAll) != value)
            {
                launchDraft = draft with { Permissions = value ? LaunchProtocol.AllowAll : LaunchProtocol.Ask };
                LaunchDraftChanged();
            }
        }
    }

    void InitializeLaunchSettings()
    {
        if (launchWas is null && HostRunning && host.Can(HostOps.LaunchSettingsCapability) && Status?.Settings.LaunchProblem is null && Status?.Settings.Launch is { } saved)
        {
            ShowLaunchSettings(saved);
        }
    }

    void ShowLaunchSettings(HostLaunchSettings saved)
    {
        launchWas = launchDraft = saved;
        launchConflict = false;
        RaiseLaunchSettings(workspacesChanged: true);
    }

    void RaiseLaunchSettings(bool workspacesChanged = false)
    {
        showingLaunchSettings = true;
        try
        {
            if (workspacesChanged)
            {
                defaultWorkspaces = [.. new[] { "" }.Concat(Workspaces.Select(w => w.Label)).Append(launchDraft?.DefaultWorkspace ?? "").Distinct(StringComparer.Ordinal)];
                Raise(nameof(Workspaces));
                Raise(nameof(DefaultWorkspaces));
            }
            foreach (var name in new[] { nameof(CanEditLaunchSettings), nameof(CanReloadLaunchSettings), nameof(CanSaveLaunchSettings), nameof(LaunchChangedElsewhere),
                nameof(LaunchSettingsState), nameof(DefaultAgent), nameof(DefaultWorkspace), nameof(LaunchEnabled), nameof(UnrestrictedPermissions) })
            {
                Raise(name);
            }
        }
        finally { showingLaunchSettings = false; }
    }

    void LaunchDraftChanged(bool workspacesChanged = false)
    {
        LaunchSettingsStatus = "Unsaved draft. Review and save to change launch authorizations and defaults.";
        RaiseLaunchSettings(workspacesChanged);
    }

    public void AddWorkspace()
    {
        if (!CanEditLaunchSettings || launchDraft is not { } draft) { Error = "Connect to an editable host before changing workspaces."; return; }
        if (string.IsNullOrWhiteSpace(WorkspaceLabel) || string.IsNullOrWhiteSpace(WorkspaceFolder))
        {
            Error = "Enter a workspace label and its absolute folder path.";
            return;
        }
        if (draft.Workspaces.Count >= LaunchProtocol.MaxWorkspaces) { Error = $"Choose at most {LaunchProtocol.MaxWorkspaces} workspaces."; return; }
        launchDraft = draft with { Workspaces = [.. draft.Workspaces, new ApprovedWorkspace(WorkspaceLabel.Trim(), WorkspaceFolder.Trim(), WorkspaceIsolated)] };
        WorkspaceLabel = WorkspaceFolder = "";
        WorkspaceIsolated = false;
        Error = null;
        LaunchDraftChanged(workspacesChanged: true);
    }

    public Task ChooseWorkspaceAsync(Func<Task<(string Label, string Path)?>> choose)
    {
        if (!CanEditLaunchSettings) { Error = "Connect to an editable host before choosing a workspace."; return Task.CompletedTask; }
        return RunAsync(async () =>
        {
            if (await choose() is { } selected)
            {
                WorkspaceFolder = selected.Path;
                if (string.IsNullOrWhiteSpace(WorkspaceLabel)) { WorkspaceLabel = selected.Label; }
            }
        });
    }

    public void RemoveWorkspace(string path)
    {
        if (!CanEditLaunchSettings || launchDraft is not { } draft) { Error = "Connect to an editable host before changing workspaces."; return; }
        var removed = draft.Workspaces.Where(w => w.Path == path).ToList();
        launchDraft = draft with
        {
            Workspaces = [.. draft.Workspaces.Except(removed)],
            DefaultWorkspace = removed.Any(w => w.Label == draft.DefaultWorkspace) ? null : draft.DefaultWorkspace,
        };
        LaunchDraftChanged(workspacesChanged: true);
    }

    public Task ReloadLaunchSettingsAsync()
    {
        if (!CanReloadLaunchSettings) { Error = "Connect to the host and finish the current Settings action before reloading."; return Task.CompletedTask; }
        return RunAsync(async () =>
        {
            await host.RefreshStatusAsync();
            ShowLaunchSettings(Status?.Settings.Launch ?? throw new InvalidOperationException(Status?.Settings.LaunchProblem ?? "The host did not report its launch settings."));
            LaunchSettingsStatus = "Loaded the host's current launch settings. Any previous draft was discarded.";
        });
    }

    public Task SaveLaunchSettingsAsync(bool confirmed = false)
    {
        if (!confirmed) { LaunchSettingsStatus = "No launch settings were saved."; return Task.CompletedTask; }
        if (!CanSaveLaunchSettings || launchDraft is not { } asked || launchWas is not { } was)
        {
            Error = LaunchChangedElsewhere ? LaunchSettingsState : "There are no editable launch changes to save.";
            return Task.CompletedTask;
        }
        return RunAsync(async () =>
        {
            LaunchSettingsStatus = "The save has not been confirmed. If the connection is lost, reload before retrying.";
            asked.CheckMessageBudget();
            var result = await host.CallAsync<SettingsResult>(HostOps.Settings, new SettingsRequest(Launch: asked, LaunchBaseline: was.Baseline, LaunchConfirmed: true));
            if (result.Conflict)
            {
                launchConflict = true;
                RaiseLaunchSettings();
                throw new InvalidOperationException("Launch settings changed elsewhere. Reload and review them before saving.");
            }
            ShowLaunchSettings(result.Settings.Launch ?? throw new InvalidOperationException(result.Settings.LaunchProblem ?? "The host did not confirm the launch settings."));
            LaunchSettingsStatus = "Launch settings saved. Restart the host when you are ready; nothing was restarted or trusted automatically.";
            await host.RefreshStatusAsync();
        });
    }
}
