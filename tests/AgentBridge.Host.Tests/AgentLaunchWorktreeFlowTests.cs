using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

[Trait("Category", "Network")]
[Trait("Category", "Process")]
public sealed class AgentLaunchWorktreeFlowTests : LaunchFlowFixture
{
    const string Principal = "agent:test/parent";

    LaunchService Service(bool isolated = true, int limit = 10) => Launcher(Config(c => c with
    {
        Workspaces = [new("App", app, isolated)], WorktreeLimit = limit,
    }), wait: TimeSpan.FromSeconds(5), follow: TimeSpan.FromSeconds(10));

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
}
