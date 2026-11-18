namespace AgentBridge.App.Core;

public sealed class UiWatchdog : IDisposable
{
    readonly TimeProvider clock;
    readonly Func<long> heartbeat;
    readonly TimeSpan threshold;
    readonly int threadId;
    readonly RunMarker marker;
    readonly Func<IReadOnlyList<string>> lastLogLines;
    readonly Action<string> log;
    readonly ITimer timer;
    readonly object gate = new();
    long lastBeat;
    long lastCheck;
    long observedSince;
    bool reported;
    int disposed;

    public UiWatchdog(TimeProvider clock, Func<long> heartbeat, TimeSpan threshold, int threadId,
        RunMarker marker, Func<IReadOnlyList<string>> lastLogLines, Action<string> log)
    {
        if (threshold <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(threshold)); }
        this.clock = clock;
        this.heartbeat = heartbeat;
        this.threshold = threshold;
        this.threadId = threadId;
        this.marker = marker;
        this.lastLogLines = lastLogLines;
        this.log = log;
        lastBeat = heartbeat();
        lastCheck = observedSince = clock.GetTimestamp();
        timer = clock.CreateTimer(Check, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    void Check(object? state)
    {
        // A slow disk must not queue overlapping writes or make the UI wait on this lock.
        if (!Monitor.TryEnter(gate)) { return; }
        try
        {
            if (Volatile.Read(ref disposed) != 0) { return; }
            var now = clock.GetTimestamp();
            // A paused watchdog cannot distinguish a UI stall from suspend or a debugger break.
            // Restart observation when the background timer itself missed several beats.
            if (clock.GetElapsedTime(lastCheck, now) > TimeSpan.FromSeconds(5)) { observedSince = now; }
            lastCheck = now;
            var beat = heartbeat();
            if (beat != lastBeat) { lastBeat = beat; reported = false; }
            var stalled = clock.GetElapsedTime(Math.Max(beat, observedSince), now);
            if (reported || stalled < threshold) { return; }
            reported = true;
            try
            {
                var lines = lastLogLines().TakeLast(20).Select(LogStage).ToArray();
                marker.RecordHang(threadId, stalled, lines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log("UI hang report could not be written: " + e.GetType().Name);
            }
        }
        finally { Monitor.Exit(gate); }
    }

    // AppLog can contain exception messages. Keep fixed stages only, never their free text
    // (which may hold a transcript, path or credential that pattern redaction cannot prove safe).
    static readonly string[] Stages = ["settings not saved:", "toasts not registered:",
        "the host did not start:", "first run: start at sign-in not set up:",
        "other machines are off: this app's device key could not be loaded:",
        "the relay lock could not be opened, so it is not kept:"];
    static string LogStage(string line) => Stages.FirstOrDefault(stage => line.StartsWith(stage, StringComparison.Ordinal)) is { } stage
        ? stage + " [details omitted]" : "[message omitted]";

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        timer.Dispose();
    }
}
