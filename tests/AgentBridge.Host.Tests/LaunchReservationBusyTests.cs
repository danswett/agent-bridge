using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

[Trait("Category", "Process")]
public sealed class LaunchReservationBusyTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Fact]
    public async Task An_already_reserved_worktree_is_busy_without_a_process_or_token_disclosure()
    {
        Git(app, "init", "-q");
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "fixture");
        var config = Config(c => c with { Workspaces = [new("App", app, true)] });
        var made = Worktrees.Create(app, config, launchRequest: Guid.NewGuid());
        Assert.Null(made.Problem);
        const string Id = "77777777-7777-4777-8777-777777777777";
        Resumable(Id, made.Path!, "Reserved session");
        var launcher = Launcher(config);

        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Resume = Id }, "fixture").WaitAsync(Patience);

        Assert.Equal(LaunchOutcome.Busy, result.Outcome);
        Assert.DoesNotContain(made.Reservation!, result.Reason ?? "");
        Assert.Empty(starter.Specs);
        Assert.True(Worktrees.Release(made.Worktree!, made.Reservation!));
    }
}
