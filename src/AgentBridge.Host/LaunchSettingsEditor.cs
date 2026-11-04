using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public static class LaunchSettingsEditor
{
    public static HostLaunchSettings View(LaunchConfig config) => new(
        [.. config.Workspaces.Select(w => new ApprovedWorkspace(w.Label, w.Path, w.Isolate))],
        config.DefaultAgent, config.DefaultWorkspace, config.Permissions, config.Enabled);

    public static HostLaunchSettings Check(HostLaunchSettings asked, HostLaunchSettings current, Func<string, bool>? isSystemDirectory = null)
    {
        if (asked.Workspaces is null || asked.Workspaces.Count > LaunchProtocol.MaxWorkspaces)
        {
            throw new ArgumentException($"Choose at most {LaunchProtocol.MaxWorkspaces} workspaces.");
        }
        asked.CheckMessageBudget();
        if (asked.DefaultAgent != "auto" && !LaunchProtocol.Agents.Contains(asked.DefaultAgent))
        {
            throw new ArgumentException("Choose a supported default agent or auto.");
        }
        if (asked.Permissions is not (LaunchProtocol.Ask or LaunchProtocol.AllowAll))
        {
            throw new ArgumentException("Choose standard permission prompts or allowAll explicitly.");
        }
        var workspaces = new List<ApprovedWorkspace>();
        var physicalPaths = new List<string>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workspace in asked.Workspaces)
        {
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Label) || workspace.Label.Any(char.IsControl)
                || Limits.KeepStart(workspace.Label.Trim(), LaunchProtocol.MaxLabel) != workspace.Label.Trim())
            {
                throw new ArgumentException("Each workspace needs a visible, bounded label without control characters.");
            }
            var label = workspace.Label.Trim();
            if (!labels.Add(label)) { throw new ArgumentException($"Another workspace is already labelled '{label}'."); }
            if (string.IsNullOrWhiteSpace(workspace.Path) || workspace.Path.Any(char.IsControl) || Folders.Expand(workspace.Path) is not { } full)
            {
                throw new ArgumentException($"'{label}' needs an absolute folder path.");
            }
            var existing = current.Workspaces.FirstOrDefault(w => Folders.Same(w.Path, full));
            var physical = Folders.Physical(full);
            // An offline drive already approved is not a new approval. Keep it editable without
            // authorizing a new missing folder or enabling isolation that cannot be checked.
            if (physical is null && (existing is null || workspace.Isolate != existing.Isolate))
            {
                throw new ArgumentException($"'{label}' does not exist or cannot be read.");
            }
            if (physical is not null && (isSystemDirectory ?? Folders.IsSystemDirectory)(physical))
            {
                throw new ArgumentException($"'{label}' is a system folder; sessions cannot be launched there.");
            }
            var identity = physical ?? full;
            if (physicalPaths.Any(p => Folders.Same(p, identity))) { throw new ArgumentException($"'{label}' repeats an approved folder."); }
            physicalPaths.Add(identity);
            if (workspace.Isolate && physical is not null && existing?.Isolate != true && !Git.Run(physical, ["rev-parse", "--show-toplevel"]).Ok)
            {
                throw new ArgumentException($"'{label}' is not in a git repository; turn off isolated worktrees.");
            }
            workspaces.Add(new ApprovedWorkspace(label, full, workspace.Isolate));
        }
        var defaultWorkspace = string.IsNullOrWhiteSpace(asked.DefaultWorkspace) ? null : asked.DefaultWorkspace;
        if (defaultWorkspace is not null && !workspaces.Any(w => w.Label == defaultWorkspace))
        {
            throw new ArgumentException("The default workspace must be one of the approved folders, or none.");
        }
        var checkedSettings = asked with { Workspaces = workspaces, DefaultWorkspace = defaultWorkspace };
        checkedSettings.CheckMessageBudget();
        return checkedSettings;
    }
}
