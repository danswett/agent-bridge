using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed class LaunchGrantDispatchTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Fact]
    public async Task A_grant_revoked_while_queued_is_checked_before_any_worktree_or_audit_prepare()
    {
        ISessionLauncher launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(() => launcher.LaunchAuditedAsync(Request(), "agent:first", _ => _ => { }, () =>
        {
            reached.TrySetResult();
            release.Task.WaitAsync(Patience).GetAwaiter().GetResult();
            return "test-finished";
        }));
        Task<LaunchResult>? queued = null;
        var revoked = false;
        var checkedAfterWait = false;
        var prepared = false;
        try
        {
            await reached.Task.WaitAsync(Patience);
            queued = launcher.LaunchAuditedAsync(Request(), "agent:second", _ =>
            {
                prepared = true;
                return _ => { };
            }, () =>
            {
                checkedAfterWait = true;
                return revoked ? "grant-revoked" : null;
            });
            Assert.False(checkedAfterWait);
            revoked = true;
            release.TrySetResult();
            var result = await queued.WaitAsync(Patience);
            Assert.True(checkedAfterWait);
            Assert.Equal(LaunchOutcome.Refused, result.Outcome);
            Assert.Equal("grant-revoked", result.Reason);
            Assert.False(prepared);
            Assert.Empty(starter.Specs);
            Assert.False(Directory.Exists(Path.Combine(f.Root, "wt")));
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(Patience);
            if (queued is not null) { await queued.WaitAsync(Patience); }
        }
    }

    [Fact]
    public async Task An_audited_launch_without_a_grant_keeps_the_existing_path()
    {
        var prepared = false;
        var result = await Launcher(Config()).LaunchAuditedAsync(Request(), "phone", _ =>
        {
            prepared = true;
            return _ => { };
        });
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.True(prepared);
        Assert.Single(starter.Specs);
    }

    static LaunchRequest Request() => new() { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "App" };
}
