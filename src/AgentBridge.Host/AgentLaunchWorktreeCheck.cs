using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

public static class AgentLaunchWorktreeCheck
{
    // What each agent could load from a project folder; presence is the refusal. Each name's source, and what this check
    // cannot guarantee, is in docs/design/agent-launch-scope.md section 3.
    static readonly string[] Claude = [".claude", ".mcp.json"];
    static readonly string[] CodexOwn = [".codex", ".agents", ".codex-plugin", ".claude-plugin", ".cursor-plugin"];
    // In the repository, Claude's names too: Codex can import them, and reads .mcp.json under a plugin folder.
    static readonly string[] Codex = [.. CodexOwn, .. Claude];
    const string ClaudeRefusal = "The repository contains Claude project configuration; agent launch is refused.";
    const string CodexRefusal = "The repository contains project configuration Codex could load; agent launch is refused.";

    // Read-only W1 check. The launch service owns refusal, audit, cleanup and cap release (W2).
    public static string? Check(WorkspaceConfig workspace, WorktreeResult created, string agent) =>
        Check(workspace, created, agent, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string? Check(WorkspaceConfig workspace, WorktreeResult created, string agent, string profile)
    {
        if (!workspace.Isolate) { return "Agent launches require an isolated workspace."; }
        if (agent is not ("claude" or "codex")) { return "This agent has no confirmed project configuration list for agent launches."; }
        var (names, refusal) = agent == "codex" ? (Codex, CodexRefusal) : (Claude, ClaudeRefusal);
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
            if (Configuration(tree, names, _ => refusal, recursive: true) is { } problem) { return problem; }
            // Claude can read main-checkout local settings, and Codex its hooks, even when started in a worktree.
            var listed = Git.Run(tree, ["worktree", "list", "--porcelain", "-z"]);
            var first = listed.Output.Split('\0')[0];
            if (!listed.Ok || !first.StartsWith("worktree ", StringComparison.Ordinal)
                || Folders.Physical(first["worktree ".Length..]) is not { } primary)
            {
                return "The main checkout's project configuration cannot be verified.";
            }
            if (Configuration(primary, names, _ => refusal, recursive: false) is { } inherited) { return inherited; }
            if (agent == "codex")
            {
                // The hooks come from the main checkout's copy of every folder down to the launch folder.
                var folder = primary;
                foreach (var part in relative.Split(Path.DirectorySeparatorChar))
                {
                    folder = Path.Combine(folder, part);
                    if (Directory.Exists(folder) && Configuration(folder, names, _ => refusal, recursive: false) is { } nested) { return nested; }
                }
                var home = Folders.Physical(profile);
                if ((Above(tree, home) ?? Above(primary, home)) is { } above) { return above; }
            }

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

    // Codex's own loader stops at the project root. A folder above it is refused anyway, short of the user profile, whose
    // .codex and .agents are the person's own settings. Claude's names are left out: Codex reads none of them there.
    static string? Above(string path, string? profile)
    {
        for (var folder = Path.GetDirectoryName(path); folder is not null && (profile is null || !Folders.Same(folder, profile));
            folder = Path.GetDirectoryName(folder))
        {
            if (Configuration(folder, CodexOwn, name => $"A folder above the worktree or main checkout holds {name}, which Codex could load; agent launch is refused.",
                recursive: false) is { } problem) { return problem; }
        }
        return null;
    }

    static string? Configuration(string root, string[] names, Func<string, string> refuse, bool recursive)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (Folders.Same(folder, root) && entry.Name == ".git") { continue; }
                if (names.FirstOrDefault(n => string.Equals(entry.Name, n, StringComparison.OrdinalIgnoreCase)) is { } name)
                {
                    return refuse(name);
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
