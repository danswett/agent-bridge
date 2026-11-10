using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

// The move strip's view model over a fake step stream (#1716).
public sealed class MoveProgressTests
{
    static MoveProgressViewModel Moving(out List<(string Text, bool Assertive)> said)
    {
        var vm = new MoveProgressViewModel("Machine A", "Machine B");
        var log = new List<(string, bool)>();
        vm.Announced += (text, assertive) => log.Add((text, assertive));
        said = log;
        return vm;
    }
    static string[] States(MoveProgressViewModel vm) => [.. vm.Steps.Select(s => s.StateText)];

    [Fact]
    public void The_steps_move_on_in_order_with_their_times_and_Send_stays_off_until_the_end()
    {
        var vm = Moving(out _);
        foreach (var e in FakeMoveSteps.Succeeding())
        {
            vm.Apply(e);
            if (vm.Phase == MovePhase.Moving) { Assert.False(vm.AcceptsAnswers); Assert.Equal("Hand-off in progress", vm.SendNote); }
        }
        Assert.Equal(MovePhase.Moved, vm.Phase);
        Assert.Equal(["done", "done", "done", "done", "done"], States(vm));
        Assert.Equal("done · 3 s", vm.Steps[1].TimeText);
        Assert.Equal("Moved to Machine B", vm.Head);
        Assert.Equal("fork-1", vm.NewSessionId);
    }

    [Fact]
    public void A_failed_third_step_shows_its_reason_marks_the_rest_not_started_and_makes_the_original_answerable()
    {
        var vm = Moving(out _);
        foreach (var e in FakeMoveSteps.FailingAt(MoveStepName.ArtifactsPushed, "Machine B is offline. Pick another machine or try again when it is back.")) { vm.Apply(e); }
        Assert.Equal(MovePhase.Failed, vm.Phase);
        Assert.Equal(["done", "done", "failed", "not started", "not started"], States(vm));
        Assert.Equal("Machine B is offline. Pick another machine or try again when it is back.", vm.Steps[2].Reason);
        Assert.Equal("Move to Machine B didn't finish", vm.Head);
        Assert.True(vm.AcceptsAnswers);
    }

    [Fact]
    public void Each_step_is_announced_once_politely_and_a_failure_once_assertively()
    {
        var vm = Moving(out var said);
        var first = FakeMoveSteps.Succeeding().First();
        vm.Apply(first);
        vm.Apply(first);
        Assert.Equal([("Hand-off requested", false)], said);

        var failing = Moving(out var failed);
        failing.Apply(MoveEvent.Fail("Machine B is offline."));
        failing.Apply(MoveEvent.Fail("Machine B is offline."));
        Assert.Equal([("Move to Machine B didn't finish. Machine B is offline.", true)], failed);
    }

    [Fact]
    public void Every_step_has_a_name_that_starts_with_the_words_drawn_and_says_its_state()
    {
        var vm = Moving(out _);
        vm.Apply(MoveEvent.Step(MoveStepName.HandoffRequested, null));
        vm.Tick(TimeSpan.FromSeconds(12));
        Assert.Equal("1 Hand-off requested, done · –", vm.Steps[0].Name);
        Assert.Equal("2 Hand-off confirmed, working · 12 s", vm.Steps[1].Name);
        Assert.Equal("3 Files sent to Machine B, waiting", vm.Steps[2].Name);
        vm.Tick(TimeSpan.FromSeconds(75));
        Assert.Equal("still working · 1 min 15 s", vm.Steps[1].TimeText);
    }}
