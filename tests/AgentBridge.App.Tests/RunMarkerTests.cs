using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class RunMarkerTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-run-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 17, 20, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Start_leaves_version_and_time_and_complete_makes_the_next_start_clean()
    {
        Assert.False(RunMarker.ReadPrevious(folder).Unclean);
        var marker = RunMarker.Start(folder, "1.2.3", clock);
        Assert.False(marker.Previous.Unclean);
        var previous = RunMarker.ReadPrevious(folder);
        Assert.True(previous.Unclean);
        Assert.Equal("1.2.3", previous.Version);
        Assert.Equal(clock.GetUtcNow(), previous.StartedAt);
        marker.Complete();
        marker.Complete();
        Assert.False(RunMarker.ReadPrevious(folder).Unclean);
    }

    [Fact]
    public void A_second_start_keeps_the_unclean_previous_run_before_writing_its_own_marker()
    {
        RunMarker.Start(folder, "1.2.3", clock);
        var started = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(2));
        var next = RunMarker.Start(folder, "1.2.4", clock);
        Assert.True(next.Previous.Unclean);
        Assert.Equal("1.2.3", next.Previous.Version);
        Assert.Equal(started, next.Previous.StartedAt);
        Assert.Equal("1.2.4", RunMarker.ReadPrevious(folder).Version);
        Assert.Equal(clock.GetUtcNow(), RunMarker.ReadPrevious(folder).StartedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public void An_empty_or_corrupt_marker_is_unclean_without_throwing(string contents)
    {
        RunMarker.Start(folder, "1", clock);
        File.WriteAllText(Assert.Single(Directory.GetFiles(folder)), contents);
        Assert.True(RunMarker.ReadPrevious(folder).Unclean);
    }

    [Fact]
    public void Hang_evidence_uses_the_clock_and_redacts_every_free_text_field_before_writing()
    {
        var credential = "ghp_" + new string('x', 30);
        var marker = RunMarker.Start(folder, credential, clock);
        clock.Advance(TimeSpan.FromSeconds(15));
        marker.RecordHang(42, TimeSpan.FromSeconds(15), ["diagnostic " + credential]);
        var previous = RunMarker.ReadPrevious(folder);
        var hang = Assert.IsType<HangRecord>(previous.Hang);
        Assert.Equal(clock.GetUtcNow(), hang.At);
        Assert.Equal(42, hang.ThreadId);
        Assert.Equal(15, hang.SecondsStalled);
        Assert.Contains(FeedbackRedactor.Mark, Assert.Single(hang.LastLogLines));
        Assert.DoesNotContain(credential, File.ReadAllText(Assert.Single(Directory.GetFiles(folder))));
    }

    [Fact]
    public void A_crash_keeps_type_and_frames_but_never_the_exception_message_or_transcript()
    {
        var marker = RunMarker.Start(folder, "1", clock);
        var error = Assert.Throws<InvalidOperationException>((Action)Crash);
        marker.RecordCrash(error);
        marker.Complete();
        var previous = RunMarker.ReadPrevious(folder);
        Assert.True(previous.Unclean);
        Assert.Contains(nameof(InvalidOperationException), previous.Crash);
        Assert.Contains("RunMarkerTests.Crash", previous.Crash);
        Assert.DoesNotContain("private conversation", File.ReadAllText(Assert.Single(Directory.GetFiles(folder))));
    }

    static void Crash() => throw new InvalidOperationException("private conversation");

    [Fact]
    public void A_late_watchdog_or_crash_callback_does_not_recreate_a_completed_marker()
    {
        var marker = RunMarker.Start(folder, "1", clock);
        marker.Complete();
        marker.RecordHang(42, TimeSpan.FromSeconds(15), ["diagnostic"]);
        marker.RecordCrash(new InvalidOperationException());
        Assert.False(RunMarker.ReadPrevious(folder).Unclean);
    }
}
