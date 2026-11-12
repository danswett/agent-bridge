using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

[Trait("Category", "Network")]
[Trait("Category", "Process")]
public sealed class AgentLaunchWorktreeFlowTests : LaunchFlowFixture
{
    const string Principal = "agent:test/parent";

    LaunchService Service(bool isolated = true, int limit = 10, Func<WorkspaceConfig, WorktreeResult, string, string?>? check = null, string? workspaceDirectory = null)
    {
        var audit = new AgentAuditLog(Path.Combine(f.Root, "audit-profile"));
        return Launcher(Config(c => c with
        {
            Workspaces = [new("App", workspaceDirectory ?? app, isolated)], WorktreeLimit = limit,
        }), wait: TimeSpan.FromSeconds(5), follow: TimeSpan.FromSeconds(10),
            agentWorktreeCheck: check ?? AgentLaunchWorktreeCheck.Check,
            agentWorktreeRefusalAudit: (request, principal, reason) => audit.Append(
                new("local", request.ToString("D"), principal, [], LaunchProtocol.Launch, "local", "refused", reason)));
    }

    JsonElement Audit() => JsonSerializer.Deserialize<JsonElement>(Assert.Single(File.ReadAllLines(
        Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "audit-profile", AgentAuditLog.FolderName), "launches-*.jsonl"))).Skip(1)));

    LaunchRequest Request() => new()
    {
        Request = Guid.NewGuid().ToString("D"), Agent = "claude", Workspace = "App",
        Permissions = LaunchProtocol.Ask, Prompt = "fixture prompt",
    };

    void Repository(string? configuration = null)
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        if (configuration is not null)
        {
            var path = configuration == ".claude" ? Path.Combine(app, ".claude", "settings.json") : Path.Combine(app, configuration);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
        }
        Commit();
    }

    void Commit()
    {
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "fixture");
    }

    [Theory]
    [InlineData(".claude")]
    [InlineData(".mcp.json")]
    public async Task An_agent_launch_with_project_configuration_is_refused_and_only_its_new_tree_is_removed(string configuration)
    {
        Repository(configuration);
        var older = Worktrees.Create(app, Config());
        Assert.Null(older.Problem);
        var service = Service();

        var result = await service.LaunchAsync(Request(), Principal);

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("project configuration", result.Reason);
        Assert.Empty(starter.Specs);
        Assert.True(Directory.Exists(older.Worktree));
        Assert.Equal([older.Worktree], Directory.GetDirectories(Path.Combine(f.Root, "wt")));
        Assert.True(File.Exists(Path.Combine(app, configuration)) || Directory.Exists(Path.Combine(app, configuration)));
        Assert.Equal(Principal, Audit().GetProperty("principal").GetString());
        Assert.Equal(result.Reason, Audit().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_refused_request_is_not_checked_twice_and_does_not_fill_the_worktree_cap()
    {
        Repository(".mcp.json");
        var service = Service(limit: 1);
        var request = Request();
        var first = await service.LaunchAsync(request, Principal);

        Assert.Equal(LaunchOutcome.Refused, first.Outcome);
        Assert.Equal(first, await service.LaunchAsync(request, Principal));
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Root, "wt")));
        Assert.Equal(request.Request, Audit().GetProperty("request").GetString());
        File.Delete(Path.Combine(app, ".mcp.json"));
        Commit();

        var next = await service.LaunchAsync(Request(), Principal);

        Assert.Equal(LaunchOutcome.Started, next.Outcome);
        await Unlocked(Assert.Single(starter.Specs).WorkingDirectory);
    }

    [Theory]
    [InlineData("agent:test/parent", true, null)]
    [InlineData("person-device", false, ".mcp.json")]
    [InlineData("person-device", true, ".mcp.json")]
    public async Task A_clean_agent_tree_launches_and_person_launches_keep_their_existing_behavior(string peer, bool isolated, string? configuration)
    {
        Repository(configuration);

        var result = await Service(isolated).LaunchAsync(Request(), peer);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var directory = Assert.Single(starter.Specs).WorkingDirectory;
        if (isolated) { Assert.True(Worktrees.IsManaged(directory)); Assert.NotEqual(app, directory); }
        else { Assert.Equal(app, directory); }
        if (isolated) { await Unlocked(directory); }
    }

    Task Unlocked(string directory) => WaitAsync(() => Task.FromResult(
        !File.Exists(Path.Combine(Worktrees.AdminFolder(directory)!, "locked"))));

    [Fact]
    public async Task A_thrown_worktree_check_is_an_audited_refusal_and_removes_the_new_tree()
    {
        Repository();

        var result = await Service(check: (_, _, _) => throw new InvalidOperationException("private exception text"))
            .LaunchAsync(Request(), Principal);

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("check could not be completed", result.Reason);
        Assert.Empty(starter.Specs);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Root, "wt")));
        Assert.Equal(result.Reason, Audit().GetProperty("reason").GetString());
        Assert.DoesNotContain("private", Audit().GetRawText());
    }

    [Fact]
    public async Task An_agent_cannot_launch_without_creating_a_fresh_isolated_tree()
    {
        Repository();

        var result = await Service(isolated: false).LaunchAsync(Request(), Principal);

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("fresh isolated worktree", result.Reason);
        Assert.Empty(starter.Specs);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "wt")));
        Assert.Equal("refused", Audit().GetProperty("outcome").GetString());
    }

    [Fact]
    public void Cleanup_cannot_remove_a_tree_created_for_another_launch_or_a_fabricated_result()
    {
        Repository();
        var request = Guid.NewGuid();
        var older = Worktrees.Create(app, Config(), launchRequest: request);
        Assert.Null(older.Problem);

        Assert.Contains("not created by this launch", Worktrees.DiscardCreated(older, Guid.NewGuid()));
        Assert.Contains("not created by this launch", Worktrees.DiscardCreated(older with { }, request));
        Assert.True(File.Exists(Path.Combine(older.Worktree!, "README.md")));
        Assert.True(Worktrees.IsManaged(older.Worktree!));
    }

    [Fact]
    public async Task A_refused_tree_that_gained_local_data_is_retained_without_deleting_that_data()
    {
        Repository();
        string? tree = null;

        var result = await Service(check: (_, created, _) =>
        {
            tree = created.Worktree!;
            File.WriteAllText(Path.Combine(tree, "local-data.txt"), "preserve");
            return "Fixture refusal.";
        }).LaunchAsync(Request(), Principal);

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("retained", result.Reason);
        Assert.Empty(starter.Specs);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(tree!, "local-data.txt")));
        Assert.Equal("refused", Audit().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_tree_created_without_the_approved_subdirectory_is_removed_and_the_launch_is_audited()
    {
        Repository();
        var workspace = Directory.CreateDirectory(Path.Combine(app, "uncommitted-subdirectory")).FullName;

        var result = await Service(workspaceDirectory: workspace).LaunchAsync(Request(), Principal);

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("not created", result.Reason);
        Assert.Empty(starter.Specs);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Root, "wt")));
        Assert.True(Directory.Exists(workspace));
        Assert.Equal(result.Reason, Audit().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_checkout_hook_commit_is_retained_when_its_project_configuration_refuses_the_launch()
    {
        Repository();
        var hook = Path.Combine(app, ".git", "hooks", "post-checkout");
        Git(app, "config", "core.hooksPath", Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\nprintf '%s\\n' '{}' > .mcp.json\ngit add -- .mcp.json\ngit -c user.name=t -c user.email=t@example.com commit -qm hook\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        var result = await Service().LaunchAsync(Request(), Principal);

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("project configuration", result.Reason);
        Assert.Contains("retained", result.Reason);
        Assert.Empty(starter.Specs);
        var tree = Assert.Single(Directory.GetDirectories(Path.Combine(f.Root, "wt")));
        Assert.Equal("{}\n", File.ReadAllText(Path.Combine(tree, ".mcp.json")));
        Assert.Equal("refused", Audit().GetProperty("outcome").GetString());
    }
}
