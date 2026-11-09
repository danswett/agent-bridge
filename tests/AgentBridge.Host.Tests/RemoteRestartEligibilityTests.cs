using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1454: a remote restart is blocked by a sign-in flow that is running, not by the entrypoint being on.
public sealed class RemoteRestartEligibilityTests
{
    sealed class Flow(Func<bool> running) : IAgentSignInDeparture
    {
        public bool BlocksDeparture => running();
    }

    static (RemoteRestartEligibility Eligibility, FakeTimeProvider Time) Make(Func<bool> running)
    {
        var time = new FakeTimeProvider();
        return (new RemoteRestartEligibility(() => null, () => true, new Flow(running), time), time);
    }

    [Fact]
    public void An_idle_sign_in_does_not_block_update_now_or_the_commit()
    {
        var (e, _) = Make(() => false);
        e.Refresh();

        Assert.False(e.SignInPending);
        Assert.False(e.IsBlocking());
    }

    [Fact]
    public void A_running_flow_blocks_both()
    {
        var (e, _) = Make(() => true);
        e.Refresh();

        Assert.True(e.SignInPending);
        Assert.True(e.IsBlocking());
    }

    [Fact]
    public void A_flow_that_ends_lifts_the_block_on_the_next_refresh()
    {
        var running = true;
        var (e, _) = Make(() => running);
        e.Refresh();
        Assert.True(e.IsBlocking());

        running = false;
        e.Refresh();

        Assert.False(e.SignInPending);
        Assert.False(e.IsBlocking());
    }

    [Fact]
    public void A_flow_that_starts_after_the_snapshot_still_blocks_the_commit()
    {
        var running = false;
        var (e, time) = Make(() => running);
        e.Refresh();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(e.IsBlocking());

        running = true;

        Assert.True(e.IsBlocking());
    }

    [Fact]
    public void An_unreadable_answer_blocks_whether_checked_now_or_at_the_commit()
    {
        var broken = false;
        var (e, _) = Make(() => broken ? throw new InvalidOperationException() : false);
        e.Refresh();
        Assert.False(e.IsBlocking());

        broken = true;

        Assert.True(e.SignInPending);
        Assert.True(e.IsBlocking());
        e.Refresh();
        Assert.True(e.IsBlocking());
    }
}