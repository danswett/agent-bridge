using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Windows;
using Xunit;
using Xunit.Abstractions;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) { Skip = "needs Windows consoles"; }
    }
}

// The real injector worker typing into a real console: a console program of the tests' own
// (agent-bridge-console-echo), started in a new hidden console, which writes down every key
// event it reads. Nothing here touches any console the tests did not create.
// AGENT_BRIDGE_INJECTOR points at a published (NativeAOT) worker; otherwise the build's own.
public sealed class InjectorIntegrationTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("abinj-").FullName;
    readonly List<int> started = [];

    static string Worker => Environment.GetEnvironmentVariable("AGENT_BRIDGE_INJECTOR") is { Length: > 0 } set
        ? set : Path.Combine(AppContext.BaseDirectory, WorkerInjector.FileName);

    public void Dispose()
    {
        foreach (var pid in started)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); p.WaitForExit(5000); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed record Echo(int Pid, nint Window, string Log);

    // Starts the echo program in a console of its own (CREATE_NEW_CONSOLE), hidden, and waits
    // for it to say it is reading.
    Echo StartEcho()
    {
        var log = Path.Combine(root, $"echo-{Guid.NewGuid():N}.log");
        var exe = Path.Combine(AppContext.BaseDirectory, "agent-bridge-console-echo.exe");
        var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1 /* STARTF_USESHOWWINDOW */, ShowWindow = 0 /* SW_HIDE */ };
        var command = new StringBuilder($"\"{exe}\" \"{log}\"");
        Assert.True(CreateProcessW(null, command, 0, 0, false, 0x10 /* CREATE_NEW_CONSOLE */, 0, null, ref info, out var process), $"CreateProcess {Marshal.GetLastWin32Error()}");
        CloseHandle(process.Thread);
        CloseHandle(process.Process);
        started.Add(process.ProcessId);
        var ready = WaitFor(log, lines => lines.FirstOrDefault(l => l.StartsWith("ready ", StringComparison.Ordinal)));
        var parts = ready.Split(' ');
        return new Echo(process.ProcessId, nint.Parse(parts[2], CultureInfo.InvariantCulture), log);
    }

    static T WaitFor<T>(string log, Func<string[], T?> found, int seconds = 15)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            string[] lines = [];
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                lines = new StreamReader(stream).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (FileNotFoundException) { }
            if (found(lines) is { } result) { return result; }
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(seconds), $"timed out; the echo log held: {string.Join(" / ", lines)}");
            Thread.Sleep(50);
        }
    }

    static List<string> Keys(string[] lines) => [.. lines.Where(l => l.StartsWith("key ", StringComparison.Ordinal))];

    static bool Exited(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.WaitForExit(10_000); }
        catch (ArgumentException) { return true; }
    }

    [WindowsFact]
    public async Task The_worker_writes_every_character_as_an_exact_Unicode_key_down_and_up_and_Enter_as_a_key_of_its_own()
    {
        var echo = StartEcho();
        var text = "héllo – 世界 ✓ 🚀 \"q\" \\" + KeyPlan.Down;

        var result = await new WorkerInjector(Worker).InjectAsync(echo.Pid, new KeyPlan([new KeySend(text, true, 100, 0)]));

        Assert.Equal(InjectionStatus.Typed, result.Status);
        var expected = text.SelectMany(c => new[] { $"key 1 0 {(int)c}", $"key 0 0 {(int)c}" }).Concat(["key 1 13 13", "key 0 13 13"]).ToList();
        var got = WaitFor(echo.Log, lines => Keys(lines) is { } k && k.Count >= expected.Count ? k : null);
        Assert.Equal(expected, got);
        // The emoji is its surrogate pair, each unit its own key down and up.
        Assert.Contains($"key 1 0 {0xD83D}", got);
        Assert.Contains($"key 1 0 {0xDE80}", got);
    }

    [WindowsFact]
    public async Task Several_sends_reach_the_console_in_order_each_in_its_own_attach()
    {
        var echo = StartEcho();
        var plan = new KeyPlan([new KeySend(KeyPlan.Up, false, 0, 50), new KeySend("", true, 50, 50), new KeySend("ok", true, 50, 0)]);

        var result = await new WorkerInjector(Worker).InjectAsync(echo.Pid, plan);

        Assert.Equal(InjectionStatus.Typed, result.Status);
        var got = WaitFor(echo.Log, lines => Keys(lines) is { Count: >= 14 } k ? k : null);
        Assert.Equal(new[] { 27, 91, 65, 13, 111, 107, 13 }, got.Where(k => k.StartsWith("key 1", StringComparison.Ordinal)).Select(k => int.Parse(k.Split(' ')[3], CultureInfo.InvariantCulture)));
    }

    [WindowsFact]
    public async Task The_host_survives_the_session_console_closing_mid_answer_reports_it_unconfirmed_and_answers_the_next()
    {
        var echo = StartEcho();
        Assert.NotEqual(0, echo.Window);
        var injector = new WorkerInjector(Worker);
        // Long enough between the words and the Enter for the window to close while attached.
        var typing = injector.InjectAsync(echo.Pid, new KeyPlan([new KeySend("first", true, 10_000, 0)]));
        WaitFor(echo.Log, lines => Keys(lines).Count >= 10 ? "typed" : null);

        Assert.True(PostMessageW(echo.Window, 0x0010 /* WM_CLOSE */, 0, 0));
        var interrupted = await typing.WaitAsync(TimeSpan.FromSeconds(20));
        output.WriteLine($"after close: {interrupted.Status} {interrupted.Detail}");

        Assert.Equal(InjectionStatus.Interrupted, interrupted.Status);
        Assert.True(Exited(echo.Pid));

        var next = StartEcho();
        var again = await injector.InjectAsync(next.Pid, new KeyPlan([new KeySend("again", true, 50, 0)]));
        Assert.Equal(InjectionStatus.Typed, again.Status);
        WaitFor(next.Log, lines => Keys(lines).Count >= 12 ? "typed" : null);
    }

    [WindowsFact]
    public async Task A_process_that_cannot_be_attached_to_is_reported_as_nothing_typed()
    {
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();

        var result = await new WorkerInjector(Worker).InjectAsync(gone.Id, new KeyPlan([new KeySend("x", true, 10, 0)]));

        Assert.Equal(InjectionStatus.NothingTyped, result.Status);
        output.WriteLine(result.Detail);
    }

    [WindowsFact]
    public async Task A_missing_worker_types_nothing()
    {
        var result = await new WorkerInjector(Path.Combine(root, "absent.exe")).InjectAsync(1, new KeyPlan([new KeySend("x", true, 10, 0)]));

        Assert.Equal((InjectionStatus.NothingTyped, true), (result.Status, result.Detail.Contains(WorkerInjector.FileName)));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    static extern bool PostMessageW(nint window, uint message, nint w, nint l);
}

// G5 and the elevation guard against this machine's real state, read-only.
public class WindowsGuardTests
{
    [WindowsFact]
    public void The_probe_lists_this_sessions_mutexes_and_finds_one_named_like_agent_ha_bridges_daemon()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // An id no installation has, so only listing the namespace can find it.
        var name = "AgentBridgeDaemon_" + Guid.NewGuid().ToString("N");
        var probe = new AgentHaBridgeProbe();
        Assert.DoesNotContain(name, probe.Active() ?? "");

        using (new Mutex(false, @"Local\" + name))
        {
            var found = new HashSet<string>();
            Assert.True(AgentHaBridgeProbe.NamedMutexes(found));
            Assert.Contains(name, found);
            Assert.Contains(name, probe.Active());
        }

        Assert.DoesNotContain(name, probe.Active() ?? "");
    }

    [WindowsFact]
    public void The_elevation_guard_reads_a_processes_integrity_level_and_refuses_only_an_elevated_one()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var level = TargetGuard.IntegrityLevel(Environment.ProcessId);

        Assert.True(level >= 0x2000, $"level 0x{level:X}");
        Assert.Equal(level >= 0x3000, new TargetGuard().Refuse(Environment.ProcessId) is not null);
        Assert.NotNull(new TargetGuard().Refuse(0));
    }
}
