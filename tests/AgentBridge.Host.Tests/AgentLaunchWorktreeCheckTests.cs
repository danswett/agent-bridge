using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

// Each test needs a repository and a detached host worktree of it. Making them with git took four git processes a test (about a
// hundred a run), and on the loaded windows-latest hunt one of them took over Git.Run's 30 s now and then ("git init did not finish
// in time", #2733, #4236, #4237). The pair is made once per class, with git, and copied for each test with no git process at all;
// the copy is a real repository, so every check still runs against real git.
[Trait("Category", "Process")]
public sealed class AgentLaunchWorktreeCheckTests : IDisposable, IClassFixture<AgentLaunchWorktreeCheckTests.BaseRepository>
{
    readonly string root = Directory.CreateTempSubdirectory("ab-agent-tree-").FullName;
    readonly string repository;
    readonly string tree;
    readonly WorktreeResult created;
    WorkspaceConfig Workspace => new("app", repository, true);

    public AgentLaunchWorktreeCheckTests(BaseRepository baseline)
    {
        (repository, tree) = baseline.CopyTo(root);
        File.WriteAllText(Marker, DateTimeOffset.UtcNow.ToString("o"));
        created = new(tree, tree, null, "");
    }

    // The pair git made once for the whole class.
    public sealed class BaseRepository : IDisposable
    {
        const string WorktreeName = "tree";
        readonly string home = Directory.CreateTempSubdirectory("ab-agent-tree-base-").FullName;
        string Repository => Path.Combine(home, "repo");
        string Tree => Path.Combine(home, WorktreeName);

        public BaseRepository()
        {
            Directory.CreateDirectory(Repository);
            Run(Repository, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(Repository, "readme.txt"), "hello\n");
            File.WriteAllText(Path.Combine(Repository, ".gitignore"), "ignored\n");
            Directory.CreateDirectory(Path.Combine(Repository, "src"));
            File.WriteAllText(Path.Combine(Repository, "src", "app.txt"), "app\n");
            Run(Repository, "add", "readme.txt", ".gitignore", "src");
            Run(Repository, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-qm", "base");
            Run(Repository, "worktree", "add", "--detach", Tree, "HEAD");
        }

        // A copy of the pair under destination, with the two files that name each other by absolute path pointed at the copy.
        public (string Repository, string Tree) CopyTo(string destination)
        {
            var repository = Path.Combine(destination, "repo");
            var tree = Path.Combine(destination, WorktreeName);
            Copy(Repository, repository);
            Copy(Tree, tree);
            var admin = Path.Combine(repository, ".git", "worktrees", WorktreeName);
            // git may mark these read-only; the copy keeps that, and they are about to be rewritten.
            foreach (var file in new[] { Path.Combine(tree, ".git"), Path.Combine(admin, "gitdir") }) { File.SetAttributes(file, FileAttributes.Normal); }
            File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {admin.Replace('\\', '/')}\n");
            File.WriteAllText(Path.Combine(admin, "gitdir"), $"{Path.Combine(tree, ".git").Replace('\\', '/')}\n");
            return (repository, tree);
        }

        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from)) { File.Copy(file, Path.Combine(to, Path.GetFileName(file))); }
            foreach (var folder in Directory.EnumerateDirectories(from)) { Copy(folder, Path.Combine(to, Path.GetFileName(folder))); }
        }

        public void Dispose() => Delete(home);
    }

    string Marker => Path.Combine(repository, ".git", "worktrees", "tree", Worktrees.Marker);
    string? Check(WorkspaceConfig? workspace = null, WorktreeResult? result = null, string agent = "claude") =>
        AgentLaunchWorktreeCheck.Check(workspace ?? Workspace, result ?? created, agent);

    [Fact]
    public void The_copy_is_a_real_clean_detached_worktree_of_its_own_repository_and_leaves_the_base_alone()
    {
        var common = Git.Run(tree, ["rev-parse", "--path-format=absolute", "--git-common-dir"]);
        Assert.True(common.Ok, common.Said);
        Assert.Equal(Path.GetFullPath(Path.Combine(repository, ".git")), Path.GetFullPath(common.Output.Trim()), ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal("", Git.Run(tree, ["status", "--porcelain", "--ignored"]).Output.Trim());
        Assert.Equal("HEAD", Git.Run(tree, ["rev-parse", "--abbrev-ref", "HEAD"]).Output.Trim());
        // A change in this copy is not in the next test's.
        File.AppendAllText(Path.Combine(tree, "readme.txt"), "changed\n");
        Assert.NotEqual("", Git.Run(tree, ["status", "--porcelain"]).Output.Trim());
    }

    [Fact]
    public void A_clean_detached_host_worktree_of_the_approved_repository_is_accepted()
    {
        Assert.Null(Check());
        var workspace = Workspace with { Path = Path.Combine(repository, "src") };
        Assert.Null(Check(workspace, created with { Path = Path.Combine(tree, "src") }));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("agency")]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("Claude")]
    [InlineData("CODEX")]
    [InlineData("codex ")]
    public void An_agent_without_a_confirmed_configuration_list_is_refused(string agent) =>
        Assert.Contains("confirmed", Check(agent: agent));

    // The folder above this test's root stands in for the user profile where none is given, so no test reads the real one.
    string? Codex(WorkspaceConfig? workspace = null, WorktreeResult? result = null, string? profile = null) =>
        AgentLaunchWorktreeCheck.Check(workspace ?? Workspace, result ?? created, "codex", profile ?? Path.GetDirectoryName(root)!);

    [Fact]
    public void Codex_meets_the_same_worktree_rules_as_Claude()
    {
        Assert.Null(Codex());
        Assert.Equal("Agent launches require an isolated workspace.", Codex(Workspace with { Isolate = false }));
        Assert.Equal("A fresh isolated worktree was not created.", Codex(result: created with { Problem = "creation failed" }));
        Assert.Equal("The launch path does not match the approved isolated workspace.", Codex(result: created with { Path = repository }));
        File.WriteAllText(Path.Combine(tree, "untracked"), "x");
        Assert.Contains("clean", Codex());
        File.Delete(Path.Combine(tree, "untracked"));
        Run(tree, "switch", "-qc", "worker");
        Assert.Contains("detached", Codex());
        File.Delete(Marker);
        Assert.Contains("host", Codex());
    }

    // One row or more for each name on Codex's list; the paths are what Codex reads there (docs/design/agent-launch-scope.md §3).
    [Theory]
    [InlineData(".codex/config.toml")]
    [InlineData(".codex/hooks.json")]
    [InlineData(".codex/rules/allow.rules")]
    [InlineData(".agents/plugins/marketplace.json")]
    [InlineData(".agents/skills/build/SKILL.md")]
    [InlineData(".codex-plugin/plugin.json")]
    [InlineData(".claude-plugin/marketplace.json")]
    [InlineData(".cursor-plugin/marketplace.json")]
    [InlineData(".claude/settings.json")]
    [InlineData(".mcp.json")]
    [InlineData("src/.codex/config.toml")]
    [InlineData("src/.CODEX/config.toml")]
    public void Configuration_Codex_could_load_is_refused(string relative)
    {
        // Left uncommitted, to spend no git process (see the class note): the configuration answer comes before the clean one.
        Write(relative);
        Assert.Equal("The repository contains project configuration Codex could load; agent launch is refused.", Codex());
    }

    [Fact]
    public void Instruction_files_carry_no_authority_and_do_not_refuse_Codex()
    {
        Commit("AGENTS.md");
        Commit("src/AGENTS.override.md");
        Assert.Null(Codex());
    }

    [Theory]
    [InlineData(".codex")]
    [InlineData(".agents")]
    public void Claude_is_not_refused_for_configuration_only_Codex_reads(string name)
    {
        Commit(name + "/file");
        Directory.CreateDirectory(Path.Combine(repository, name));
        Directory.CreateDirectory(Path.Combine(root, name));
        Assert.Null(Check());
    }

    // A linked worktree takes Codex hooks from the same subfolder of the main checkout.
    [Theory]
    [InlineData(".codex")]
    [InlineData("src/.codex")]
    [InlineData("src/.agents")]
    public void Main_checkout_configuration_down_to_the_launch_folder_refuses_Codex(string relative)
    {
        var workspace = Workspace with { Path = Path.Combine(repository, "src") };
        var result = created with { Path = Path.Combine(tree, "src") };
        Assert.Null(Codex(workspace, result));
        Directory.CreateDirectory(Path.Combine(repository, relative));
        Assert.Contains("Codex could load", Codex(workspace, result));
    }

    [Theory]
    [InlineData(".codex")]
    [InlineData(".agents")]
    [InlineData(".codex-plugin")]
    [InlineData(".claude-plugin")]
    [InlineData(".cursor-plugin")]
    public void Configuration_above_the_worktree_and_below_the_profile_refuses_Codex_and_the_profiles_own_does_not(string name)
    {
        Directory.CreateDirectory(Path.Combine(root, name));
        Assert.Equal($"A folder above the worktree or main checkout holds {name}, which Codex could load; agent launch is refused.", Codex());
        Assert.Null(Codex(profile: root));
    }

    [Fact]
    public void Folders_above_the_worktree_and_above_the_main_checkout_are_each_checked()
    {
        var apart = Directory.CreateDirectory(Path.Combine(root, "apart")).FullName;
        var moved = Path.Combine(apart, "tree");
        Directory.Move(tree, moved);
        File.WriteAllText(Path.Combine(repository, ".git", "worktrees", "tree", "gitdir"), Path.Combine(moved, ".git").Replace('\\', '/') + "\n");
        var result = new WorktreeResult(moved, moved, null, "");
        Assert.Null(Codex(result: result));
        var config = Directory.CreateDirectory(Path.Combine(apart, ".codex")).FullName;
        Assert.Contains("above", Codex(result: result));
        Directory.Delete(config);

        var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        var main = Path.Combine(home, "repo");
        Directory.Move(repository, main);
        File.WriteAllText(Path.Combine(moved, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "tree").Replace('\\', '/')}\n");
        var workspace = Workspace with { Path = main };
        Assert.Null(Codex(workspace, result));
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        Assert.Contains("above", Codex(workspace, result));
    }

    // Claude's own folders above a worktree are common (a person's repos folder), and Codex never reads them there.
    [Fact]
    public void Claude_folders_above_the_worktree_do_not_refuse_Codex()
    {
        Directory.CreateDirectory(Path.Combine(root, ".claude"));
        File.WriteAllText(Path.Combine(root, ".mcp.json"), "{}");
        Assert.Null(Codex());
    }

    void Write(string relative)
    {
        var path = Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
    }

    void Commit(string relative)
    {
        Write(relative);
        Run(tree, "add", ".");
        Run(tree, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-qm", "config");
    }

    [Fact]
    public void A_nonisolated_workspace_or_failed_creation_is_refused()
    {
        Assert.Equal("Agent launches require an isolated workspace.", Check(Workspace with { Isolate = false }));
        Assert.Equal("A fresh isolated worktree was not created.", Check(result: created with { Problem = "creation failed" }));
        Assert.Equal("A fresh isolated worktree was not created.", Check(result: created with { Worktree = null }));
        Assert.Equal("A fresh isolated worktree was not created.", Check(result: created with { Path = null }));
    }

    [Fact]
    public void A_primary_checkout_or_unmarked_worktree_is_not_owned_by_the_host()
    {
        Assert.Equal("The worktree is not marked as created by this host.", Check(result: created with { Path = repository, Worktree = repository }));
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
        Assert.Equal("The launch path does not match the approved isolated workspace.", Check(result: created with { Path = repository }));
        Assert.Equal("The launch path does not match the approved isolated workspace.", Check(result: created with { Path = Path.Combine(tree, "src") }));
        Assert.Equal("The launch path does not match the approved isolated workspace.", Check(Workspace with { Path = Path.Combine(repository, "src") }));
        Assert.Equal("The isolated worktree or approved workspace cannot be read.", Check(result: created with { Path = Path.Combine(tree, "missing") }));
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

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void A_directory_link_cannot_hide_configuration_or_escape_the_worktree(string agent)
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
        try
        {
            Assert.Equal("A worktree link prevents verification of project configuration.",
                AgentLaunchWorktreeCheck.Check(Workspace, created, agent, Path.GetDirectoryName(root)!));
        }
        finally
        {
            Directory.Delete(link);
        }
        Assert.True(Directory.Exists(outside));
    }

    static void Run(string directory, params string[] arguments)
    {
        var result = Git.Run(directory, arguments);
        Assert.True(result.Ok, result.Said);
    }

    static void Delete(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(folder, true);
    }

    public void Dispose() => Delete(root);
}
