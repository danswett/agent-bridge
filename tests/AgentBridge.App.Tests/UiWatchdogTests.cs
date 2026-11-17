using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class UiWatchdogTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-watchdog-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new();
    readonly List<string> errors = [];
    long heartbeat;
    int snapshots;
    string Marker => Path.Combine(folder, "run-marker.json");

    public UiWatchdogTests() => heartbeat = clock.GetTimestamp();

    UiWatchdog Watch(params string[] lines) => new(clock, () => heartbeat, TimeSpan.FromSeconds(30),
        42, RunMarker.Start(folder, "1.0", clock), () => { snapshots++; return lines; }, errors.Add);

    bool HasHang()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Marker));
        return json.RootElement.GetProperty("Hang").ValueKind != JsonValueKind.Null;
    }

    void Tick(int seconds)
    {
        for (var i = 0; i < seconds; i++) { clock.Advance(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public void The_background_timer_records_the_stalled_ui_thread_at_the_threshold()
    {
        using var watch = Watch("settings not saved: disk unavailable");
        Tick(29);
        Assert.False(HasHang());
        clock.Advance(TimeSpan.FromSeconds(1));
        using var json = JsonDocument.Parse(File.ReadAllText(Marker));
        var hang = json.RootElement.GetProperty("Hang");
        Assert.Equal(42, hang.GetProperty("ThreadId").GetInt32());
        Assert.Equal(30, hang.GetProperty("SecondsStalled").GetDouble());
        Assert.Equal(clock.GetUtcNow(), hang.GetProperty("At").GetDateTimeOffset());
        Assert.Empty(errors);
    }

    [Fact]
    public void A_hang_is_written_once_until_a_new_heartbeat_rearms_detection()
    {
        using var watch = Watch();
        Tick(30);
        var first = File.ReadAllText(Marker);
        Tick(60);
        Assert.Equal(first, File.ReadAllText(Marker));
        Assert.Equal(1, snapshots);
        clock.Advance(TimeSpan.FromMinutes(2));
        Tick(30);
        Assert.Equal(1, snapshots);
        heartbeat = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(first, File.ReadAllText(Marker));
        Tick(29);
        Assert.Equal(2, snapshots);
        Assert.NotEqual(first, File.ReadAllText(Marker));
    }

    [Fact]
    public void Healthy_heartbeats_do_not_record_a_hang()
    {
        using var watch = Watch();
        Tick(20);
        heartbeat = clock.GetTimestamp();
        Tick(20);
        Assert.False(HasHang());
        Assert.Equal(0, snapshots);
    }

    [Fact]
    public void Log_context_keeps_only_known_stages_never_free_text_or_transcripts()
    {
        using var watch = Watch("settings not saved: password=sample-private-value",
            "transcript: private user conversation", "the host did not start: Bearer sample-private-value");
        Tick(30);
        using var json = JsonDocument.Parse(File.ReadAllText(Marker));
        var lines = json.RootElement.GetProperty("Hang").GetProperty("LastLogLines")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["settings not saved: [details omitted]", "[message omitted]",
            "the host did not start: [details omitted]"], lines);
    }

    [Fact]
    public void Disposing_stops_reporting_even_if_the_ui_timer_has_stopped()
    {
        var watch = Watch();
        watch.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(HasHang());
    }

    [Fact]
    public void A_marker_write_failure_is_logged_once_and_does_not_escape_the_timer()
    {
        using var watch = Watch();
        File.Delete(Marker);
        Directory.CreateDirectory(Marker);
        Tick(30);
        Tick(30);
        Assert.Single(errors);
        Assert.Contains("hang report", errors[0]);
        Assert.Equal(1, snapshots);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(120)]
    public void Missing_watchdog_ticks_restart_observation_after_resume_or_a_debugger_break(int pauseSeconds)
    {
        using var watch = Watch();
        Tick(25);
        clock.Advance(TimeSpan.FromSeconds(pauseSeconds));
        Assert.False(HasHang());
        Tick(29);
        Assert.False(HasHang());
        Tick(1);
        using var json = JsonDocument.Parse(File.ReadAllText(Marker));
        Assert.Equal(30, json.RootElement.GetProperty("Hang").GetProperty("SecondsStalled").GetDouble());
    }

    [Fact]
    public void A_healthy_beat_after_resume_does_not_permanently_suppress_later_hangs()
    {
        using var watch = Watch();
        clock.Advance(TimeSpan.FromMinutes(2));
        heartbeat = clock.GetTimestamp();
        Tick(29);
        Assert.False(HasHang());
        Tick(1);
        Assert.True(HasHang());
        Assert.Equal(1, snapshots);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }
}
