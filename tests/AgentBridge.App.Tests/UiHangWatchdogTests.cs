using System.Diagnostics;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

// The owner's app froze with the UI thread blocked and the list still scrolling, and no dump was taken. A background thread posts a no-op to the UI
// dispatcher every few seconds; unanswered for more than 15 s, the app is hung and ONE minidump is written for that freeze, from that thread; the UI
// answering again re-arms it for the next freeze. Tick() is one beat.
public sealed class UiHangWatchdogTests
{
    static readonly TimeSpan Beat = UiHangWatchdog.PingEvery;

    // A UI thread that answers (Run) or is blocked (the posted no-ops pile up unrun).
    sealed class FakeUi : IUiDispatcher
    {
        readonly object gate = new();
        readonly List<Action> pending = [];
        public bool Throws { get; set; }
        public bool Refuses { get; set; }
        public bool TryPost(Action action)
        {
            if (Refuses) { return false; }
            Post(action);
            return true;
        }
        public void Post(Action action)
        {
            if (Throws) { throw new InvalidOperationException("the queue is closed"); }
            lock (gate) { pending.Add(action); }
        }
        public void Run()
        {
            Action[] actions;
            lock (gate) { actions = [.. pending]; pending.Clear(); }
            foreach (var action in actions) { action(); }
        }
    }

    sealed class FakeDump(string? result = "C:\\dumps\\hang.dmp", bool throws = false) : IHangDumpWriter
    {
        public int Writes { get; private set; }
        public string? Write()
        {
            Writes++;
            return throws ? throw new IOException("disk full") : result;
        }
    }

    sealed class SkippingDump(string reason) : IHangDumpWriter
    {
        public int Writes { get; private set; }
        public string? Write() { Writes++; throw new HangDumpSkipped(reason); }
    }

    sealed class CallbackDump(Action action) : IHangDumpWriter
    {
        public string? Write() { action(); return @"C:\dumps\x.dmp"; }
    }

    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    readonly FakeUi ui = new();
    readonly FakeDump dump = new();
    readonly List<string> said = [];

    UiHangWatchdog Watchdog(IHangDumpWriter? writer = null, Func<bool>? debugger = null) =>
        new(ui, writer ?? dump, time, said.Add, debuggerAttached: debugger ?? (() => false));

    // Beats a hung UI for this long, one beat at a time.
    void Hang(UiHangWatchdog watchdog, TimeSpan total)
    {
        for (var elapsed = TimeSpan.Zero; elapsed <= total; elapsed += Beat) { watchdog.Tick(); time.Advance(Beat); }
    }

    [Fact]
    public void A_ui_that_answers_every_ping_never_dumps()
    {
        var watchdog = Watchdog();
        for (var i = 0; i < 200; i++) { watchdog.Tick(); ui.Run(); time.Advance(Beat); }

        Assert.Equal(0, dump.Writes);
    }

    [Fact]
    public void Fifteen_seconds_unanswered_is_not_yet_a_hang_but_more_than_fifteen_is()
    {
        var watchdog = Watchdog();
        for (var i = 0; i < 5; i++) { watchdog.Tick(); time.Advance(Beat); }  // t=0..12: the first ping is posted and never run
        time.Advance(TimeSpan.Zero);
        watchdog.Tick();                       // t=15
        Assert.Equal(0, dump.Writes);

        time.Advance(TimeSpan.FromMilliseconds(1));
        watchdog.Tick();
        Assert.Equal(1, dump.Writes);
    }

    [Fact]
    public void A_hang_writes_exactly_one_dump_however_long_it_lasts()
    {
        var watchdog = Watchdog();

        Hang(watchdog, TimeSpan.FromMinutes(10));

        Assert.Equal(1, dump.Writes);
        Assert.Contains(said, line => line.Contains("hang.dmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Recovery_re_arms_so_a_second_freeze_writes_a_second_dump_and_each_freeze_exactly_one()
    {
        var watchdog = Watchdog();
        Hang(watchdog, TimeSpan.FromSeconds(60));
        Assert.Equal(1, dump.Writes);              // the first freeze, however long it lasted

        ui.Run();                                  // it answers at last
        watchdog.Tick();
        ui.Run();
        watchdog.Tick();
        Assert.Contains(said, line => line.Contains("answered again", StringComparison.Ordinal));
        Assert.Equal(1, dump.Writes);

        Hang(watchdog, TimeSpan.FromSeconds(120)); // and it freezes a second time

        Assert.Equal(2, dump.Writes);
    }

    [Fact]
    public void A_ui_that_answers_late_but_within_the_limit_is_not_a_freeze_and_does_not_arm_anything()
    {
        var watchdog = Watchdog();
        for (var i = 0; i < 4; i++) { watchdog.Tick(); time.Advance(Beat); }  // 12 s unanswered
        ui.Run();
        for (var i = 0; i < 20; i++) { watchdog.Tick(); ui.Run(); time.Advance(Beat); }

        Assert.Equal(0, dump.Writes);
        Assert.DoesNotContain(said, line => line.Contains("answered again", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gap_between_beats_is_a_sleep_or_a_pause_not_a_hang()
    {
        var watchdog = Watchdog();
        watchdog.Tick();                              // posted, unanswered
        time.Advance(TimeSpan.FromHours(8));          // the machine slept: the watchdog thread was suspended too
        watchdog.Tick();
        Assert.Equal(0, dump.Writes);

        time.Advance(Beat);                           // and the next beat starts from a fresh ping
        watchdog.Tick();
        Assert.Equal(0, dump.Writes);
    }

    // Resume from sleep: the system says so (power mode), and what the ping waited through proves nothing, even when the clock did not count the sleep.
    [Fact]
    public void A_resume_discards_the_ping_and_gives_the_ui_a_full_window_again()
    {
        var watchdog = Watchdog();
        for (var i = 0; i < 4; i++) { watchdog.Tick(); time.Advance(Beat); }   // 12 s unanswered
        watchdog.NoteResume();
        for (var i = 0; i < 4; i++) { watchdog.Tick(); time.Advance(Beat); }   // 24 s since the first ping, 12 s since the resume
        Assert.Equal(0, dump.Writes);

        Hang(watchdog, TimeSpan.FromSeconds(30));                              // a real freeze after the resume still counts

        Assert.Equal(1, dump.Writes);
    }

    [Fact]
    public void A_dump_the_writer_skips_is_said_with_its_reason_never_thrown()
    {
        var skipping = new SkippingDump("only 412 MB of disk is free, a dump needs 1 GB");
        var watchdog = Watchdog(skipping);

        Hang(watchdog, TimeSpan.FromMinutes(1));

        Assert.Equal(1, skipping.Writes);
        Assert.Contains(said, line => line.Contains("only 412 MB of disk is free", StringComparison.Ordinal) && line.Contains("not written", StringComparison.Ordinal));
    }

    [Fact]
    public void The_dump_is_written_on_the_watchdogs_own_thread_never_the_ui_thread()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var wrote = new ManualResetEventSlim();
        var threads = new List<(int Id, string? Name)>();
        var writer = new CallbackDump(() => { threads.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.Name)); wrote.Set(); });
        using var watchdog = new UiHangWatchdog(ui, writer, TimeProvider.System, said.Add, pingEvery: TimeSpan.FromMilliseconds(20),
            hangAfter: TimeSpan.FromMilliseconds(250), debuggerAttached: () => false);

        watchdog.Start();                         // the posted no-ops are never run: this thread is the "blocked" UI thread
        Assert.True(wrote.Wait(TimeSpan.FromSeconds(10)));
        watchdog.Dispose();

        var (id, name) = Assert.Single(threads);
        Assert.NotEqual(uiThread, id);
        Assert.Equal("UI hang watchdog", name);
    }

    // A logger that throws (a full disk, a closed file) must never escape a beat: on the background thread that is an unhandled exception, which ends the app.
    [Fact]
    public void A_logger_that_throws_never_escapes_a_beat_and_the_dump_is_still_taken()
    {
        var watchdog = new UiHangWatchdog(ui, dump, time, _ => throw new InvalidOperationException("the log is gone"), debuggerAttached: () => false);

        Hang(watchdog, TimeSpan.FromMinutes(1));
        ui.Run();                                   // it answers again: the recovery line throws too
        watchdog.Tick();
        ui.Run();
        watchdog.Tick();

        Assert.Equal(1, dump.Writes);
        Assert.False(watchdog.Stopped);
    }

    [Fact]
    public void A_logger_that_throws_does_not_end_the_watchdog_thread_or_crash_the_process()
    {
        var wrote = new ManualResetEventSlim();
        var writer = new CallbackDump(() => wrote.Set());
        using var watchdog = new UiHangWatchdog(ui, writer, TimeProvider.System, _ => throw new IOException("the log is gone"),
            pingEvery: TimeSpan.FromMilliseconds(20), hangAfter: TimeSpan.FromMilliseconds(250), debuggerAttached: () => false);

        watchdog.Start();
        Assert.True(wrote.Wait(TimeSpan.FromSeconds(10)));
        Thread.Sleep(200);                           // beats after the throwing line: still running, nothing unhandled

        Assert.False(watchdog.Stopped);
        watchdog.Dispose();
        Assert.True(watchdog.Stopped);
    }

    // QueueDispatcher.Post ignored TryEnqueue's result: a queue that refuses the ping (the app is closing) looked like a hung UI thread.
    [Fact]
    public void A_dispatcher_that_refuses_the_ping_stops_the_watchdog_and_is_never_a_hang()
    {
        ui.Refuses = true;
        var watchdog = Watchdog();

        Hang(watchdog, TimeSpan.FromMinutes(5));

        Assert.Equal(0, dump.Writes);
        Assert.True(watchdog.Stopped);
        Assert.Contains(said, line => line.Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dispatcher_that_starts_refusing_later_stops_it_the_same_way()
    {
        var watchdog = Watchdog();
        watchdog.Tick();
        ui.Run();                                    // the first ping is answered
        ui.Refuses = true;
        watchdog.Tick();

        Hang(watchdog, TimeSpan.FromMinutes(2));

        Assert.Equal(0, dump.Writes);
        Assert.True(watchdog.Stopped);
    }

    [Fact]
    public void A_debugger_attached_is_never_a_hang()
    {
        var watchdog = Watchdog(debugger: () => true);

        Hang(watchdog, TimeSpan.FromMinutes(2));

        Assert.Equal(0, dump.Writes);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("C:\\dumps\\x.dmp", true)]
    public void A_dump_that_fails_or_throws_is_said_never_thrown_and_not_retried(string? result, bool throws)
    {
        var failing = new FakeDump(result, throws);
        var watchdog = Watchdog(failing);

        Hang(watchdog, TimeSpan.FromMinutes(2));

        Assert.Equal(1, failing.Writes);
        Assert.Contains(said, line => line.Contains("dump", StringComparison.OrdinalIgnoreCase) && line.Contains("not", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_dispatcher_that_has_shut_down_stops_the_watchdog_quietly()
    {
        var watchdog = Watchdog();
        watchdog.Tick();
        ui.Throws = true;
        ui.Run();
        time.Advance(Beat);

        watchdog.Tick();                  // posting throws: the app is closing
        Hang(watchdog, TimeSpan.FromMinutes(1));

        Assert.Equal(0, dump.Writes);
    }

    [Fact]
    public void Start_runs_a_background_thread_that_stops_on_dispose_and_never_dumps_a_live_ui()
    {
        using var watchdog = new UiHangWatchdog(ui, dump, TimeProvider.System, said.Add, pingEvery: TimeSpan.FromMilliseconds(10), debuggerAttached: () => false);

        watchdog.Start();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 200) { ui.Run(); Thread.Sleep(5); }
        watchdog.Dispose();

        Assert.Equal(0, dump.Writes);
        Assert.True(watchdog.Stopped);
    }
}
