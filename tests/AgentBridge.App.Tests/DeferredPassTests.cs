using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class DeferredPassTests
{
    [Fact]
    public void A_request_posts_the_work_and_does_not_run_it_in_the_layout_event()
    {
        var pass = new DeferredPass(); var posted = new List<Action>(); var runs = 0;
        pass.Request(posted.Add, () => runs++);
        Assert.Equal(0, runs);
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Requests_before_the_posted_turn_runs_are_one_run()
    {
        var pass = new DeferredPass(); var posted = new List<Action>(); var runs = 0;
        for (var i = 0; i < 50; i++) { pass.Request(posted.Add, () => runs++); }
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Layout_the_work_causes_runs_it_at_most_once_more_in_that_turn()
    {
        var pass = new DeferredPass(); var posted = new List<Action>(); var runs = 0;
        void Work() { runs++; pass.Request(posted.Add, Work); }
        pass.Request(posted.Add, Work);
        posted[0]();
        Assert.Equal(2, runs);
        Assert.Single(posted);
    }

    [Fact]
    public void A_later_request_posts_again()
    {
        var pass = new DeferredPass(); var posted = new List<Action>(); var runs = 0;
        pass.Request(posted.Add, () => runs++);
        posted[0]();
        pass.Request(posted.Add, () => runs++);
        Assert.Equal(2, posted.Count);
        posted[1]();
        Assert.Equal(2, runs);
    }

    [Fact]
    public void A_throwing_run_does_not_wedge_the_pass()
    {
        var pass = new DeferredPass(); var posted = new List<Action>(); var runs = 0;
        pass.Request(posted.Add, () => throw new InvalidOperationException("boom"));
        Assert.Throws<InvalidOperationException>(() => posted[0]());
        pass.Request(posted.Add, () => runs++);
        Assert.Equal(2, posted.Count);
        posted[1]();
        Assert.Equal(1, runs);
    }
}