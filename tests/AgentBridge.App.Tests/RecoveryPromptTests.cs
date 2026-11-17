using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class RecoveryPromptTests
{
    [Fact]
    public async Task Recovery_waits_for_the_window_and_opens_only_once_across_loaded_and_activation_events()
    {
        var offer = new RecoveryReportOffer(new PreviousRun(true));
        var closed = new TaskCompletionSource();
        var calls = 0;
        async Task Show() { calls++; await closed.Task; offer.Dismiss(); }
        Assert.False(await offer.OpenWhenReadyAsync(false, Show));
        Assert.Equal(0, calls);
        var first = offer.OpenWhenReadyAsync(true, Show);
        Assert.Equal(1, calls);
        Assert.False(await offer.OpenWhenReadyAsync(true, Show));
        closed.SetResult();
        Assert.True(await first);
        Assert.False(await offer.OpenWhenReadyAsync(true, Show));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_dialog_that_cannot_open_keeps_the_snapshot_for_the_next_window_activation()
    {
        var previous = new PreviousRun(true, "previous");
        var offer = new RecoveryReportOffer(previous);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offer.OpenWhenReadyAsync(true, () => throw new InvalidOperationException("another dialog is open")));
        Assert.Same(previous, offer.Previous);
        var calls = 0;
        Assert.True(await offer.OpenWhenReadyAsync(true, () => { calls++; offer.Dismiss(); return Task.CompletedTask; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Clean_or_dismissed_runs_do_not_open_a_window_ready_prompt()
    {
        var clean = new RecoveryReportOffer(new PreviousRun(false));
        var dismissed = new RecoveryReportOffer(new PreviousRun(true));
        dismissed.Dismiss();
        Task Unexpected() => throw new InvalidOperationException("must not open");
        Assert.False(await clean.OpenWhenReadyAsync(true, Unexpected));
        Assert.False(await dismissed.OpenWhenReadyAsync(true, Unexpected));
    }
}
