using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The app the host starts headless with --update-only (docs/protocol/v1-update.md): it updates between
// turns whatever the person chose, keeps the person's choice in their file, and ends once there is
// nothing left for it to do.
public sealed class UpdateOnlyRunTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    readonly FakeTimeProvider time = new(T0);
    UpdateState state = new() { Current = "0.1.7401", Phase = UpdatePhase.Checking };

    UpdateOnlyRun Run() => new(() => state, time);

    [Fact]
    public void The_run_updates_between_turns_with_downloads_on_whatever_the_person_chose()
    {
        var chosen = new AppPreferences { AutoUpdate = false, WhenUpdateReady = UpdateWhenReady.RestartToUpdate };

        var used = UpdateOnlyRun.Prefs(chosen);

        Assert.True(used.AutoUpdate);
        Assert.Equal(UpdateWhenReady.InstallBetweenTurns, used.WhenReady);
    }

    [Fact]
    public void What_the_run_saves_keeps_the_persons_two_choices_and_takes_the_rest()
    {
        var chosen = new AppPreferences { AutoUpdate = false, WhenUpdateReady = UpdateWhenReady.RestartToUpdate };
        var next = UpdateOnlyRun.Prefs(chosen) with { UpdateHandedOver = true, UpdatedFrom = "0.1.7401" };

        var saved = UpdateOnlyRun.ForFile(next, chosen);

        Assert.False(saved.AutoUpdate);
        Assert.Equal(UpdateWhenReady.RestartToUpdate, saved.WhenReady);
        Assert.True(saved.UpdateHandedOver);
        Assert.Equal("0.1.7401", saved.UpdatedFrom);
    }

    [Theory]
    [InlineData(UpdatePhase.Checking)]
    [InlineData(UpdatePhase.Downloading)]
    [InlineData(UpdatePhase.Waiting)]
    [InlineData(UpdatePhase.Installing)]
    public void The_run_stays_while_it_checks_downloads_waits_for_a_gap_or_installs(UpdatePhase phase)
    {
        state = state with { Phase = phase, CheckedAt = T0, Available = "0.1.7500" };

        Assert.Null(Run().Finished());
    }

    [Fact]
    public void The_run_ends_when_a_check_found_nothing_newer()
    {
        state = state with { Phase = UpdatePhase.Idle, CheckedAt = T0, Available = null };

        Assert.NotNull(Run().Finished());
    }

    [Fact]
    public void The_run_does_not_end_before_its_first_check_has_happened()
    {
        state = state with { Phase = UpdatePhase.Idle, CheckedAt = null };

        Assert.Null(Run().Finished());
    }

    [Fact]
    public void A_copy_the_updater_cannot_update_ends_at_once()
    {
        state = new UpdateState { Current = null, Phase = UpdatePhase.Idle };

        Assert.NotNull(Run().Finished());
    }

    [Fact]
    public void A_run_that_waited_past_the_backstop_ends_so_the_host_can_start_another_later()
    {
        state = state with { Phase = UpdatePhase.Waiting, CheckedAt = T0, Available = "0.1.7500" };
        var run = Run();
        time.Advance(UpdateOnlyRun.Limit - TimeSpan.FromSeconds(1));
        Assert.Null(run.Finished());

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.NotNull(run.Finished());
    }
}
