using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class RunMarkerTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-run-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 17, 20, 0, 0, TimeSpan.Zero));
    readonly List<RunMarker> markers = [];

    public void Dispose()
    {
        foreach (var marker in markers) { marker.Dispose(); }
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    RunMarker Start(string version)
    {
        var marker = RunMarker.Start(folder, version, clock);
        markers.Add(marker);
        return marker;
    }

    PreviousRun ReadCurrent() => Directory.GetFiles(folder, "run-marker*.json")
        .Select(file => System.Text.Json.JsonSerializer.Deserialize<PreviousRun>(File.ReadAllText(file))!)
        .OrderByDescending(run => run.StartedAt).First();

    [Fact]
    public void Start_leaves_version_and_time_and_complete_makes_the_next_start_clean()
    {
        Assert.False(RunMarker.ReadPrevious(folder).Unclean);
        var marker = Start("1.2.3");
        Assert.False(marker.Previous.Unclean);
        var previous = ReadCurrent();
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
        Start("1.2.3").Dispose();
        var started = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(2));
        var next = Start("1.2.4");
        Assert.True(next.Previous.Unclean);
        Assert.Equal("1.2.3", next.Previous.Version);
        Assert.Equal(started, next.Previous.StartedAt);
        Assert.Equal("1.2.4", ReadCurrent().Version);
        Assert.Equal(clock.GetUtcNow(), ReadCurrent().StartedAt);
        next.Complete();
        Assert.False(RunMarker.ReadPrevious(folder).Unclean);
    }

    [Fact]
    public void Concurrent_instances_do_not_recover_or_overwrite_a_running_instances_crash()
    {
        var first = Start("first");
        RunMarker? second = null;
        try
        {
            first.RecordCrash(new InvalidOperationException());
            second = Start("second");
            Assert.False(second.Previous.Unclean);
            Assert.False(RunMarker.ReadPrevious(folder).Unclean);
            second.Complete();
            Assert.IsAssignableFrom<IDisposable>(first).Dispose();
            Assert.Contains(nameof(InvalidOperationException), RunMarker.ReadPrevious(folder).Crash);
        }
        finally { (first as object as IDisposable)?.Dispose(); (second as object as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Completing_one_instance_cannot_delete_another_instances_record()
    {
        var first = Start("first");
        var second = Start("second");
        try
        {
            first.Complete();
            Assert.Single(Directory.GetFiles(folder, "run-marker*.json"));
            Assert.IsAssignableFrom<IDisposable>(second).Dispose();
            Assert.Equal("second", RunMarker.ReadPrevious(folder).Version);
        }
        finally { (first as object as IDisposable)?.Dispose(); (second as object as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Startup_recovers_abandoned_evidence_but_leaves_the_running_instance_owned()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run-marker.abandoned.json"),
            System.Text.Json.JsonSerializer.Serialize(new PreviousRun(true, "abandoned", clock.GetUtcNow())));
        clock.Advance(TimeSpan.FromMinutes(1));
        var live = Start("live");
        Assert.Equal("abandoned", live.Previous.Version);
        RunMarker? next = null;
        try
        {
            next = Start("next");
            Assert.False(next.Previous.Unclean);
            next.Complete();
            Assert.IsAssignableFrom<IDisposable>(live).Dispose();
            Assert.Equal("live", RunMarker.ReadPrevious(folder).Version);
        }
        finally { (live as object as IDisposable)?.Dispose(); (next as object as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Competing_start_cannot_recover_the_same_record_while_the_first_start_saves()
    {
        Start("abandoned").Dispose();
        PreviousRun? competing = null;
        RunMarker.WriteFileOverride = (target, bytes) =>
        {
            RunMarker.WriteFileOverride = null;
            var other = Start("competing");
            competing = other.Previous;
            other.Complete();
            File.WriteAllBytes(target, bytes);
        };
        try
        {
            var recovered = Start("recovering");
            Assert.Equal("abandoned", recovered.Previous.Version);
            Assert.NotNull(competing);
            Assert.False(competing.Unclean);
            recovered.Complete();
            Assert.False(RunMarker.ReadPrevious(folder).Unclean);
        }
        finally { RunMarker.WriteFileOverride = null; }
    }

    [Fact]
    public void Read_only_recovered_record_does_not_disable_the_new_marker_or_release_its_lease()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Start("abandoned").Dispose();
        var old = Assert.Single(Directory.GetFiles(folder, "*.json"));
        File.SetAttributes(old, FileAttributes.ReadOnly);
        try
        {
            var recovered = Start("recovering");
            Assert.Equal("abandoned", recovered.Previous.Version);
            var file = Assert.Single(Directory.GetFiles(folder, "*.json").Where(file => file != old));
            using var competingOwner = UserLock.TryTake(file + ".lock");
            Assert.Null(competingOwner);
            recovered.Complete();
            Assert.False(File.Exists(file));
            recovered.RecordCrash(new InvalidOperationException());
            Assert.Contains(nameof(InvalidOperationException), File.ReadAllText(file));
        }
        finally { File.SetAttributes(old, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public void An_empty_or_corrupt_marker_is_unclean_without_throwing(string contents)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run-marker.json"), contents);
        Assert.True(RunMarker.ReadPrevious(folder).Unclean);
    }

    [Fact]
    public void Hang_evidence_uses_the_clock_and_redacts_every_free_text_field_before_writing()
    {
        var credential = "ghp_" + new string('x', 30);
        var marker = Start(credential);
        clock.Advance(TimeSpan.FromSeconds(15));
        marker.RecordHang(42, TimeSpan.FromSeconds(15), ["diagnostic " + credential]);
        var previous = ReadCurrent();
        var hang = Assert.IsType<HangRecord>(previous.Hang);
        Assert.Equal(clock.GetUtcNow(), hang.At);
        Assert.Equal(42, hang.ThreadId);
        Assert.Equal(15, hang.SecondsStalled);
        Assert.Contains(FeedbackRedactor.Mark, Assert.Single(hang.LastLogLines));
        Assert.DoesNotContain(credential, File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "*.json"))));
    }

    [Fact]
    public void A_crash_keeps_type_and_frames_but_never_the_exception_message_or_transcript()
    {
        var marker = Start("1");
        var error = Assert.Throws<InvalidOperationException>((Action)Crash);
        marker.RecordCrash(error);
        marker.Complete();
        marker.Dispose();
        var previous = RunMarker.ReadPrevious(folder);
        Assert.True(previous.Unclean);
        Assert.Contains(nameof(InvalidOperationException), previous.Crash);
        Assert.Contains("RunMarkerTests.Crash", previous.Crash);
        Assert.DoesNotContain("private conversation", File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "*.json"))));
    }

    static void Crash() => throw new InvalidOperationException("private conversation");

    [Fact]
    public void A_late_watchdog_callback_does_not_recreate_a_completed_marker()
    {
        var marker = Start("1");
        marker.Complete();
        marker.RecordHang(42, TimeSpan.FromSeconds(15), ["diagnostic"]);
        Assert.False(RunMarker.ReadPrevious(folder).Unclean);
    }

    [Fact]
    public void A_crash_during_window_teardown_restores_evidence_even_after_complete()
    {
        var marker = Start("1");
        marker.Complete();
        marker.RecordCrash(new InvalidOperationException("private conversation"));
        marker.Dispose();
        Assert.True(RunMarker.ReadPrevious(folder).Unclean);
        Assert.Contains(nameof(InvalidOperationException), RunMarker.ReadPrevious(folder).Crash);
    }

    [Fact]
    public void A_private_key_spanning_log_lines_is_removed_before_the_tail_is_bounded()
    {
        var marker = Start("1");
        var material = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("synthetic-material"));
        marker.RecordHang(42, TimeSpan.FromSeconds(15),
            ["-----BEGIN PRIVATE KEY-----", .. Enumerable.Repeat(material, 45), "-----END PRIVATE KEY-----"]);
        Assert.DoesNotContain(material, File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "*.json"))));
    }

    [Theory]
    [InlineData("start", 0)]
    [InlineData("start", 7)]
    [InlineData("crash", 0)]
    [InlineData("crash", 7)]
    [InlineData("hang", 0)]
    [InlineData("hang", 7)]
    public void A_failed_or_truncated_write_preserves_the_old_marker_and_removes_the_temporary_file(string operation, int written)
    {
        var marker = Start("1");
        var file = Assert.Single(Directory.GetFiles(folder, "*.json"));
        var before = File.ReadAllBytes(file);
        if (operation == "start") { marker.Dispose(); }
        RunMarker.WriteFileOverride = (target, bytes) =>
        {
            Assert.Equal(Path.GetDirectoryName(file), Path.GetDirectoryName(target));
            File.WriteAllBytes(target, bytes[..written]);
            throw new IOException("Simulated incomplete write");
        };
        try
        {
            Action update = operation switch
            {
                "start" => () => Start("2"),
                "crash" => () => marker.RecordCrash(new InvalidOperationException()),
                _ => () => marker.RecordHang(42, TimeSpan.FromSeconds(15), ["diagnostic"]),
            };
            Assert.Throws<IOException>(update);
            Assert.Equal(before, File.ReadAllBytes(file));
            Assert.Equal(file, Assert.Single(Directory.GetFiles(folder, "*.json")));
            Assert.Equal("1", ReadCurrent().Version);
        }
        finally { RunMarker.WriteFileOverride = null; }
    }
}
