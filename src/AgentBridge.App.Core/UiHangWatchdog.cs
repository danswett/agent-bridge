using System.Diagnostics;

namespace AgentBridge.App.Core;

/// <summary>Takes the dump when the UI thread is hung: the platform's job (Windows: <see cref="WindowsMiniDump"/>). Null when it could not.</summary>
public interface IHangDumpWriter
{
    string? Write();
}

/// <summary>A dump left unwritten on purpose, with the reason for the log (words of ours, never an exception's or a path's).</summary>
public sealed class HangDumpSkipped(string reason) : Exception(reason);

/// <summary>
/// A hung UI thread leaves nothing behind: the owner's app froze with the UI thread blocked (the list still scrolled) and no dump was taken. A
/// background thread posts a no-op to the UI dispatcher every <see cref="PingEvery"/>; one that goes unanswered for more than
/// <see cref="HangAfter"/> means the UI thread is blocked, and ONE minidump is written for that freeze, from this thread (the UI thread cannot do
/// it). The UI thread answering again ends the freeze, says so in the log and re-arms the watchdog, so the next freeze is dumped too; a freeze
/// that goes on is dumped once. The dump writer keeps only the newest few files, so the disk stays bounded.
/// </summary>
/// <remarks>
/// It is not a hang when the watchdog itself was paused: a gap between beats of more than three times <see cref="PingEvery"/> (the machine
/// slept, a debugger broke in) discards the ping and starts again; <see cref="NoteResume"/> (the system's resume notice) does the same, whether or not
/// the clock counted the sleep; and a debugger attached never dumps. Everything here, the beats and the dump, runs on the watchdog's own thread: the
/// UI thread only ever runs the no-op. A dispatcher that throws on Post is an app shutting down: the watchdog stops without a word.
/// </remarks>
public sealed class UiHangWatchdog : IDisposable
{
    /// <summary>How often a no-op is posted to the UI thread.</summary>
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(3);

    /// <summary>A ping unanswered for more than this is a hang.</summary>
    public static readonly TimeSpan HangAfter = TimeSpan.FromSeconds(15);

    readonly IUiDispatcher ui;
    readonly IHangDumpWriter dump;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly TimeSpan pingEvery;
    readonly TimeSpan hangAfter;
    // Three beats late (at least a second, over the timer's own granularity): the watchdog thread was paused, not the UI.
    TimeSpan PauseAfter => pingEvery * 3 > TimeSpan.FromSeconds(1) ? pingEvery * 3 : TimeSpan.FromSeconds(1);
    readonly Func<bool> debuggerAttached;
    readonly Func<string>? uiState;
    readonly ManualResetEventSlim stop = new(false);
    Thread? thread;

    // Touched by the watchdog thread only (or by a test calling Tick); the UI thread's answer is the one volatile field.
    sealed record StateAt(string Text, long Stamp);

    StateAt? lastState;   // written by the UI thread's ping, read by this thread
    long answeredId;
    long pingId;
    long pingAt;
    long lastTick;
    bool pinged;
    bool hung;
    volatile bool closed;
    volatile bool resumed;

    public UiHangWatchdog(IUiDispatcher ui, IHangDumpWriter dump, TimeProvider time, Action<string> log, TimeSpan? pingEvery = null,
        TimeSpan? hangAfter = null, Func<bool>? debuggerAttached = null, Func<string>? uiState = null)
    {
        this.uiState = uiState;
        (this.ui, this.dump, this.time, this.log) = (ui, dump, time, log);
        this.pingEvery = pingEvery ?? PingEvery;
        this.hangAfter = hangAfter ?? HangAfter;
        this.debuggerAttached = debuggerAttached ?? (() => Debugger.IsAttached);
    }

    /// <summary>Whether the watchdog has stopped, by Dispose or because the dispatcher shut down.</summary>
    public bool Stopped => closed;

    /// <summary>The machine resumed from sleep: the ping in flight waited through it, so it counts for nothing and the UI gets a full window again.</summary>
    public void NoteResume() => resumed = true;

    public void Start()
    {
        if (thread is not null || closed) { return; }
        thread = new Thread(Loop) { IsBackground = true, Name = "UI hang watchdog" };
        thread.Start();
    }

    void Loop()
    {
        try { while (!stop.Wait(pingEvery)) { Tick(); } }
        catch (Exception e) { closed = true; Say($"UI hang watchdog stopped: {e.GetType().Name}"); }
    }

    // The log is the app's, and a full disk or a closed file must not become an unhandled exception on this background thread, which ends the app.
    void Say(string line)
    {
        try { log(line); }
        catch (Exception) { /* nothing left to say it to */ }
    }

    /// <summary>One beat: ask the UI thread to answer, or judge the ask it has not answered.</summary>
    internal void Tick()
    {
        if (closed) { return; }
        var now = time.GetTimestamp();
        var gap = lastTick == 0 ? TimeSpan.Zero : time.GetElapsedTime(lastTick, now);
        lastTick = now;
        // The watchdog itself was not running (sleep, a debugger): what the ping waited through proves nothing.
        if (gap > PauseAfter || resumed) { pinged = false; resumed = false; }
        if (pinged && Volatile.Read(ref answeredId) == pingId)
        {
            if (hung) { hung = false; Say($"UI hang: the UI thread answered again after {Seconds(time.GetElapsedTime(pingAt, now))}"); }
            pinged = false;
        }
        if (!pinged)
        {
            var id = ++pingId;
            // A queue that refuses the ping has shut down: an app that is closing, never a hung UI thread.
            try
            {
                if (!ui.TryPost(() => Answer(id)))
                {
                    closed = true;
                    Say("UI hang watchdog stopped: the UI dispatcher refused the ping (the app is closing)");
                    return;
                }
            }
            catch (Exception) { closed = true; return; }
            (pinged, pingAt) = (true, now);
            return;
        }
        var waited = time.GetElapsedTime(pingAt, now);
        if (waited <= hangAfter || debuggerAttached()) { return; }
        if (hung) { return; }  // the same freeze: its dump is taken
        hung = true;
        try
        {
            var path = dump.Write();
            Say(path is null
                ? $"UI hang: the UI thread has not answered for {Seconds(waited)}; the minidump was not written"
                : $"UI hang: the UI thread has not answered for {Seconds(waited)}; minidump written to {path}");
            SayLastState(now);
        }
        catch (HangDumpSkipped skipped)
        {
            Say($"UI hang: the UI thread has not answered for {Seconds(waited)}; the minidump was not written: {skipped.Message}");
        }
        catch (Exception e)
        {
            Say($"UI hang: the UI thread has not answered for {Seconds(waited)}; the minidump was not written ({e.GetType().Name})");
        }
    }

    // Runs ON the UI thread, as the ping: it answers, and first records what the UI looks like (names only; the reader may throw and must not stop the answer).
    void Answer(long id)
    {
        if (uiState is not null)
        {
            string text;
            try { text = uiState(); }
            catch (Exception) { text = "unavailable (the UI state could not be read)"; }
            Volatile.Write(ref lastState, new StateAt(text, time.GetTimestamp()));
        }
        Volatile.Write(ref answeredId, id);
    }

    void SayLastState(long now)
    {
        if (uiState is null) { return; }
        var state = Volatile.Read(ref lastState);
        Say(state is null
            ? "UI hang: the last UI state: none was recorded (no ping was ever answered)"
            : $"UI hang: the last UI state, {Seconds(time.GetElapsedTime(state.Stamp, now))} before the freeze was seen: {state.Text}");
    }

    static string Seconds(TimeSpan span) => $"{(int)span.TotalSeconds} s";

    public void Dispose()
    {
        stop.Set();
        thread?.Join(TimeSpan.FromSeconds(2));
        closed = true;
    }
}
