using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

[Trait("Category", "Process")]
public sealed class AgentLaunchWorktreeCheckTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ab-agent-tree-").FullName;
    readonly string repository;
    readonly string tree;
    readonly WorktreeResult created;
    WorkspaceConfig Workspace => new("app", repository, true);

    public AgentLaunchWorktreeCheckTests()
    {
        repository = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        tree = Path.Combine(root, "tree");
        Run(repository, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repository, "readme.txt"), "hello\n");
        File.WriteAllText(Path.Combine(repository, ".gitignore"), "ignored\n");
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        File.WriteAllText(Path.Combine(repository, "src", "app.txt"), "app\n");
        Run(repository, "add", "readme.txt", ".gitignore", "src");
        Run(repository, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-qm", "base");
        Run(repository, "worktree", "add", "--detach", tree, "HEAD");
        File.WriteAllText(Marker, DateTimeOffset.UtcNow.ToString("o"));
        created = new(tree, tree, null, "");
    }

    string Marker => Path.Combine(Git.Run(tree, ["rev-parse", "--absolute-git-dir"]).Output.Trim(), Worktrees.Marker);
    string? Check(WorkspaceConfig? workspace = null, WorktreeResult? result = null, string agent = "claude") =>
        AgentLaunchWorktreeCheck.Check(workspace ?? Workspace, result ?? created, agent);

    [Fact]
    public void A_clean_detached_host_worktree_of_the_approved_repository_is_accepted()
    {
        Assert.Null(Check());
        var workspace = Workspace with { Path = Path.Combine(repository, "src") };
        Assert.Null(Check(workspace, created with { Path = Path.Combine(tree, "src") }));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("codex")]
    [InlineData("agency")]
    [InlineData("auto")]
    [InlineData("")]
    public void An_agent_without_a_confirmed_configuration_list_is_refused(string agent) =>
        Assert.Contains("confirmed", Check(agent: agent));

    [Fact]
    public void A_nonisolated_workspace_or_failed_creation_is_refused()
    {
        Assert.Contains("isolated", Check(Workspace with { Isolate = false }));
        Assert.NotNull(Check(result: created with { Problem = "creation failed" }));
        Assert.NotNull(Check(result: created with { Worktree = null }));
        Assert.NotNull(Check(result: created with { Path = null }));
    }

    [Fact]
    public void A_primary_checkout_or_unmarked_worktree_is_not_owned_by_the_host()
    {
        Assert.NotNull(Check(result: created with { Path = repository, Worktree = repository }));
        File.Delete(Marker);
        Assert.Contains("host", Check());
    }

    [Fact]
    public void A_worktree_from_another_repository_is_refused()
    {
        var other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        Run(other, "init", "-q");
        Assert.Contains("repository", Check(Workspace with { Path = other }));
    }

    [Fact]
    public void The_launch_path_must_match_the_approved_subdirectory()
    {
        Assert.NotNull(Check(result: created with { Path = repository }));
        Assert.NotNull(Check(result: created with { Path = Path.Combine(tree, "src") }));
        Assert.NotNull(Check(Workspace with { Path = Path.Combine(repository, "src") }));
        Assert.NotNull(Check(result: created with { Path = Path.Combine(tree, "missing") }));
    }

    [Fact]
    public void A_branch_checkout_is_not_a_fresh_detached_worktree()
    {
        Run(tree, "switch", "-qc", "worker");
        Assert.Contains("detached", Check());
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("staged")]
    [InlineData("untracked")]
    [InlineData("ignored")]
    public void Any_dirty_state_including_ignored_files_is_refused(string state)
    {
        var file = state is "modified" or "staged" ? "readme.txt" : state;
        File.AppendAllText(Path.Combine(tree, file), "changed\n");
        if (state == "staged") { Run(tree, "add", file); }
        Assert.Contains("clean", Check());
    }

    [Theory]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/settings.local.json")]
    [InlineData(".mcp.json")]
    [InlineData("src/.claude/settings.json")]
    [InlineData("src/.mcp.json")]
    public void Committed_configuration_is_refused_without_interpreting_its_contents(string relative)
    {
        var path = Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        Run(tree, "add", ".");
        Run(tree, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-qm", "config");
        Assert.Contains("configuration", Check());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void An_empty_configuration_directory_and_main_checkout_local_settings_are_refused()
    {
        var config = Directory.CreateDirectory(Path.Combine(tree, ".claude")).FullName;
        Assert.Contains("configuration", Check());
        Directory.Delete(config);
        Directory.CreateDirectory(Path.Combine(repository, ".claude"));
        File.WriteAllText(Path.Combine(repository, ".claude", "settings.local.json"), "{}");
        Assert.Contains("configuration", Check());
    }

    [Fact]
    public void A_directory_link_cannot_hide_configuration_or_escape_the_worktree()
    {
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        Directory.CreateDirectory(Path.Combine(outside, ".claude"));
        var link = Path.Combine(tree, "linked");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, outside },
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else { Directory.CreateSymbolicLink(link, outside); }
        Assert.NotNull(Check());
        Directory.Delete(link);
        Assert.True(Directory.Exists(outside));
    }

    static void Run(string directory, params string[] arguments)
    {
        var result = Git.Run(directory, arguments);
        Assert.True(result.Ok, result.Said);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(root, true);
    }
}
