using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

public static class AgentLaunchWorktreeCheck
{
    // Read-only W1 check. The launch service owns refusal, audit, cleanup and cap release (W2).
    public static string? Check(WorkspaceConfig workspace, WorktreeResult created, string agent)
    {
        if (!workspace.Isolate) { return "Agent launches require an isolated workspace."; }
        if (agent != "claude") { return "This agent has no confirmed project configuration list for agent launches."; }
        if (created.Problem is not null || created.Worktree is null || created.Path is null)
        {
            return "A fresh isolated worktree was not created.";
        }
        try
        {
            if (Folders.Physical(created.Worktree) is not { } tree || Folders.Physical(created.Path) is not { } launch
                || Folders.Physical(workspace.Path) is not { } source)
            {
                return "The isolated worktree or approved workspace cannot be read.";
            }
            if (!Worktrees.IsManaged(tree)) { return "The worktree is not marked as created by this host."; }
            if (Top(tree) is not { } top || !Folders.Same(top, tree)
                || Top(source) is not { } repository || Folders.Same(repository, tree)
                || Common(repository) is not { } owner || Common(tree) is not { } actual || !Folders.Same(owner, actual))
            {
                return "The worktree does not belong to the approved workspace repository.";
            }
            var relative = Path.GetRelativePath(repository, source);
            if (!Folders.IsAtOrUnder(source, repository) || !Folders.IsAtOrUnder(launch, tree)
                || Folders.Physical(Path.Combine(tree, relative)) is not { } expected || !Folders.Same(expected, launch))
            {
                return "The launch path does not match the approved isolated workspace.";
            }
            var branch = Git.Run(tree, ["symbolic-ref", "--quiet", "HEAD"]);
            if (!branch.Ran || branch.ExitCode != 1) { return "The isolated worktree must have a verified detached HEAD."; }

            // Presence, not the JSON contents, is the policy. Do not follow links while looking.
            if (Configuration(tree, recursive: true) is { } problem) { return problem; }
            // Claude can read main-checkout local settings even when it starts in a worktree.
            var listed = Git.Run(tree, ["worktree", "list", "--porcelain", "-z"]);
            var first = listed.Output.Split('\0')[0];
            if (!listed.Ok || !first.StartsWith("worktree ", StringComparison.Ordinal)
                || Folders.Physical(first["worktree ".Length..]) is not { } primary)
            {
                return "The main checkout's project configuration cannot be verified.";
            }
            if (Configuration(primary, recursive: false) is { } inherited) { return inherited; }

            var status = Git.Run(tree, ["--no-optional-locks", "-c", "core.fsmonitor=false", "status",
                "--porcelain=v1", "--untracked-files=all", "--ignored=matching", "--ignore-submodules=none"]);
            if (!status.Ok) { return "The worktree's clean state cannot be verified."; }
            return status.Output.Length == 0 ? null : "Agent launches require a clean worktree, including untracked and ignored files.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "The worktree's paths or project configuration cannot be verified.";
        }
    }

    static string? Top(string path)
    {
        var result = Git.Run(path, ["rev-parse", "--show-toplevel"]);
        return result.Ok ? Folders.Physical(result.Output.Trim()) : null;
    }

    static string? Common(string path)
    {
        var result = Git.Run(path, ["rev-parse", "--path-format=absolute", "--git-common-dir"]);
        return result.Ok ? Folders.Physical(result.Output.Trim()) : null;
    }

    static string? Configuration(string root, bool recursive)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (Folders.Same(folder, root) && entry.Name == ".git") { continue; }
                if (string.Equals(entry.Name, ".claude", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Name, ".mcp.json", StringComparison.OrdinalIgnoreCase))
                {
                    return "The repository contains Claude project configuration; agent launch is refused.";
                }
                if (!recursive) { continue; }
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return "A worktree link prevents verification of project configuration.";
                }
                if (entry is DirectoryInfo) { pending.Push(entry.FullName); }
            }
        }
        return null;
    }
}
