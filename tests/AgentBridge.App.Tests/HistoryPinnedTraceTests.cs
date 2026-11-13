using Xunit;

namespace AgentBridge.App.Tests;

public sealed class HistoryPinnedTraceTests
{
    static HistoryPinnedSample Sample(double ms, double offset, double extent, double? target = null) =>
        new(ms, "geometry", offset, extent, 500, 7, 493, target, null);

    [Fact]
    public void Growth_and_offset_changes_between_wait_polls_keep_their_timestamps()
    {
        var trace = new HistoryPinnedTrace();
        trace.Record(Sample(0, 100, 100));
        trace.Record(Sample(11.25, 100, 451));
        trace.Record(Sample(12.5, 451, 451));
        Assert.Equal(new[] { 0, 11.25, 12.5 }, trace.Entries.Select(e => e.Milliseconds));
        Assert.Equal(new[] { 100d, 451, 451 }, trace.Entries.Select(e => e.Extent));
        Assert.Equal(new[] { 100d, 100, 451 }, trace.Entries.Select(e => e.Offset));
        Assert.False(trace.EndCorrectionObserved);
    }

    [Fact]
    public void End_targets_record_correction_times_even_without_an_offset_change()
    {
        var trace = new HistoryPinnedTrace();
        trace.Record(Sample(1, 100, 451, 200));
        trace.Record(Sample(2, 100, 451, 451));
        trace.Record(Sample(3, 100, 800, 451));
        trace.Record(Sample(4, 100, 800, 800));
        Assert.True(trace.EndCorrectionObserved);
        Assert.Equal(2, trace.EndCorrectionCount);
        Assert.Equal(2, trace.FirstEndCorrectionMs);
        Assert.Equal(4, trace.LastEndCorrectionMs);
    }

    [Fact]
    public void A_full_trace_keeps_recent_events_and_discloses_dropped_early_events()
    {
        var trace = new HistoryPinnedTrace();
        for (var i = 0; i < HistoryPinnedTrace.Capacity + 2; i++)
        {
            trace.Record(Sample(i, i, i, i));
        }
        Assert.Equal(HistoryPinnedTrace.Capacity, trace.Entries.Count);
        Assert.Equal(2, trace.DroppedEntries);
        Assert.True(trace.Truncated);
        Assert.Equal(2, trace.Entries.First().Milliseconds);
        Assert.Equal(HistoryPinnedTrace.Capacity + 1, trace.Entries.Last().Milliseconds);
        Assert.Equal(0, trace.FirstEndCorrectionMs);
        Assert.Equal(HistoryPinnedTrace.Capacity + 2, trace.EndCorrectionCount);
    }
}
