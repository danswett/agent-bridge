using System.Diagnostics;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;
using Xunit;

public class PendingQuestionTests
{
    internal static Question Q(string id, string? agent = null) =>
        new() { Id = id, Agent = agent, Kind = QuestionKind.Form, Message = id, AskedAt = Fixture.T0 };

    [Fact]
    public void Reasking_moves_the_id_to_the_tail_and_replaces_its_owner()
    {
        var state = new SessionState();
        state.Ask(Q("a"));
        state.Ask(Q("b", "delegate"));
        state.Ask(Q("A", "other"));
        Assert.Equal("a", state.ShownQuestion!.Id);
        state.Ask(Q("a", "delegate"));
        Assert.Equal(["b", "A", "a"], state.Pending.Select(q => q.Id));
        Assert.Equal("a", state.ShownQuestion!.Id);
        Assert.True(state.Answer("a"));
        Assert.False(state.Answer("a"));
        Assert.False(state.Answer(null));
        Assert.Equal("A", state.ShownQuestion!.Id);
        state.Ask(Q("main"));
        state.Ask(Q("latest", "delegate"));
        Assert.Equal("main", state.ShownQuestion!.Id);
    }

    [Fact]
    public void Failing_the_main_turn_keeps_delegate_questions_in_order()
    {
        var state = new SessionState();
        state.Ask(Q("one", "delegate"));
        state.Ask(Q("main"));
        state.Ask(Q("two", "delegate"));
        state.FailTurn(Fixture.T0);
        state.FailTurn(Fixture.T0);
        Assert.Equal(["one", "two"], state.Pending.Select(q => q.Id));
        Assert.Equal("two", state.ShownQuestion!.Id);
        Assert.Equal(SessionStatus.Waiting, state.Effective);
        state.Answer("one");
        state.Answer("two");
        Assert.Null(state.ShownQuestion);
        Assert.Equal(SessionStatus.Idle, state.Effective);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_hides_interrupted_questions_but_not_new_asks_after_the_backlog(bool known)
    {
        var state = new SessionState();
        state.Ask(Q("old"));
        state.Ask(Q("delegate", "d"));
        if (known) { state.Known = new HashSet<string>(["old", "delegate"], StringComparer.Ordinal); }
        state.AskedFrom = Fixture.T0.AddSeconds(1);
        Assert.Empty(state.Asking);
        Assert.Null(state.ShownQuestion);
        Assert.Equal(SessionStatus.Idle, state.Effective);
        Assert.Equal(2, state.Interrupted.Count);
        state.EndBacklog();
        state.Ask(Q("old") with { AskedAt = Fixture.T0.AddDays(-1) });
        Assert.Equal(["old"], state.Asking.Select(q => q.Id));
        Assert.Equal("old", state.ShownQuestion!.Id);
        Assert.DoesNotContain("old", state.Interrupted);
        state.Answer("old");
        Assert.Null(state.ShownQuestion);
        state.AskedFrom = null;
        Assert.Equal("delegate", state.ShownQuestion!.Id);
    }

    [Fact]
    public void A_known_id_reasked_during_the_backlog_is_new_and_reset_replays_the_boundary()
    {
        var state = new SessionState { Known = new HashSet<string>(["q"]), AskedFrom = Fixture.T0.AddSeconds(1) };
        state.Ask(Q("q"));
        Assert.Null(state.ShownQuestion);
        state.Ask(Q("q"));
        Assert.Equal("q", state.ShownQuestion!.Id);
        state.Reset();
        Assert.Empty(state.Pending);
        Assert.Null(state.ShownQuestion);
        state.Ask(Q("q"));
        Assert.Empty(state.Asking);
        state.Abandon(Fixture.T0);
        Assert.Empty(state.Pending);
        Assert.Equal(SessionStatus.Idle, state.Effective);
    }

    [Fact]
    public void Ending_a_delegate_removes_its_questions_and_ended_children_not_other_agents()
    {
        var state = new SessionState();
        state.Background.AgentStarted("root-call", "root", Fixture.T0);
        state.Background.AgentStarted("child-call", "child", Fixture.T0, false, new(Parent: "root"));
        state.Ask(Q("root-q", "root"));
        state.Ask(Q("main"));
        state.Ask(Q("child-q", "child"));
        state.Ask(Q("live-q", "live"));
        state.Background.AgentEnded("root", Fixture.T0);
        Assert.Equal(2, state.Pending.RemoveEnded(state.Background, "root"));
        Assert.Equal(0, state.Pending.RemoveEnded(state.Background, "root"));
        Assert.Equal(["main", "live-q"], state.Pending.Select(q => q.Id));
        state.Pending.Clear();
        Assert.Empty(state.Asking);
        Assert.Null(state.ShownQuestion);
        state.Ask(Q("main", "live"));
        Assert.Equal("main", state.ShownQuestion!.Id);
    }
}

[Collection(nameof(PendingQuestionGrowthTests))]
public class PendingQuestionGrowthTests
{
    // Azure/CI only: do not include this timing probe in focused runs on a live bridge machine.
    [Fact]
    [Trait("Category", "Performance")]
    public void Delegate_asks_and_repeated_main_failures_grow_linearly()
    {
        static (double Ask, double Fail) Measure(int n)
        {
            var state = new SessionState();
            var questions = Enumerable.Range(0, n).Select(i => PendingQuestionTests.Q("q" + i, "delegate")).ToArray();
            var clock = Stopwatch.StartNew();
            foreach (var question in questions) { state.Ask(question); }
            var ask = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            for (var i = 0; i < n; i++) { state.FailTurn(Fixture.T0); }
            var fail = clock.Elapsed.TotalMilliseconds;
            Assert.Equal(n, state.Pending.Count);
            return (ask, fail);
        }

        Measure(256);
        var small = (Ask: double.MaxValue, Fail: double.MaxValue);
        var large = small;
        for (var run = 0; run < 3; run++)
        {
            var s = Measure(1_000);
            var l = Measure(16_000);
            small = (Math.Min(small.Ask, s.Ask), Math.Min(small.Fail, s.Fail));
            large = (Math.Min(large.Ask, l.Ask), Math.Min(large.Fail, l.Fail));
        }
        Assert.True(large.Ask < 100 * Math.Max(small.Ask, 0.1), $"16x the asks took {large.Ask / Math.Max(small.Ask, 0.1):F1}x as long");
        Assert.True(large.Fail < 100 * Math.Max(small.Fail, 0.1), $"16x the failures took {large.Fail / Math.Max(small.Fail, 0.1):F1}x as long");
    }
}

[CollectionDefinition(nameof(PendingQuestionGrowthTests), DisableParallelization = true)]
public sealed class PendingQuestionGrowthCollection;
