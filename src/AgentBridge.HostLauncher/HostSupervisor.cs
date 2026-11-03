namespace AgentBridge.HostLauncher;

// Starts the host again after it crashed (#106), a few times, sooner rather than later, then
// gives up and says so. Task Scheduler's restart-on-failure was meant to do this, but it acts only
// when the task fails to launch, never when its process exits with an error: on 2026-10-30 a
// shadow host crashed (#103's cause) and its task sat Ready, 0xE0434352, for hours, the machine
// dark on every phone.
//
// The exit-code contract: everything the host does on purpose ends with a small code (0 stopped,
// whether by the app, host.stop, an update or an uninstall; 2 usage; 3 another host has the
// profile; 4 refused; HostLauncherContract.Restart for host.restart, which is started again at
// once), and so does whatever ended it on purpose (-1 from the app's Process.Kill when a stop is
// not obeyed, 1 from Task Manager, Ctrl+C or log-off's 0xC000013A, SIGTERM or SIGKILL). None of
// those is restarted as a crash. Only a crash is: an unhandled .NET exception (0xE0434352, or
// SIGABRT off Windows) or another fatal fault, including one on the way out of a restart, before
// the host could say so (#239's review).
public sealed class HostSupervisor
{
    // Each well under the app's 20 s start timeout, so a Start pressed during one gets the host it
    // brings up rather than a refusal, and one launcher never outlasts the app's wait for it.
    public static readonly IReadOnlyList<TimeSpan> DefaultBackoff = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12)];

    // Runs the host once, to its end: its exit code.
    public required Func<CancellationToken, Task<int>> RunHost { get; init; }
    public Action<string> Log { get; init; } = _ => { };
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public IReadOnlyList<TimeSpan> Backoff { get; init; } = DefaultBackoff;
    // A host that ran this long before crashing has its attempts back: a crash a day is not a loop.
    public TimeSpan Healthy { get; init; } = TimeSpan.FromMinutes(10);
    public bool Windows { get; init; } = OperatingSystem.IsWindows();

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var crashes = 0;
        while (true)
        {
            var started = Time.GetTimestamp();
            var code = await RunHost(ct);
            // host.restart, the host fully stopped: its replacement, at once and still watched.
            if (code == AgentBridge.Local.HostLauncherContract.Restart)
            {
                crashes = 0;
                continue;
            }
            if (!IsCrash(code, Windows)) { return code; }
            if (Time.GetElapsedTime(started) >= Healthy) { crashes = 0; }
            if (crashes == Backoff.Count)
            {
                Log($"the host crashed (exit code {Describe(code)}) {crashes + 1} times in a row; not starting it again: start it from the Agent Bridge app once the cause is fixed");
                return code;
            }
            var wait = Backoff[crashes++];
            Log($"the host crashed (exit code {Describe(code)}); starting it again in {wait.TotalSeconds:0} s (attempt {crashes} of {Backoff.Count})");
            await (Delay ?? ((d, c) => Task.Delay(d, Time, c)))(wait, ct);
        }
    }

    public static bool IsCrash(int code, bool windows)
    {
        if (!windows)
        {
            // 128 + the fatal signal: SIGILL, SIGTRAP, SIGABRT (an unhandled .NET exception), SIGBUS,
            // SIGFPE, SIGSEGV, SIGSYS. Not SIGINT, SIGKILL or SIGTERM, which someone sent.
            return code is 132 or 133 or 134 or 135 or 136 or 139 or 159;
        }
        // An NTSTATUS error or warning, or an exception code: 0xE0434352 for an unhandled .NET
        // exception, 0xC0000005, 0xC00000FD, 0xC0000409 and the like. Not -1 (TerminateProcess from
        // Process.Kill) or 0xC000013A (ended by Ctrl+C, closing or logging off).
        return unchecked((uint)code) >= 0x80000000u && code != -1 && unchecked((uint)code) != 0xC000013Au;
    }

    static string Describe(int code) => code < 0 ? $"0x{unchecked((uint)code):X8}" : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
