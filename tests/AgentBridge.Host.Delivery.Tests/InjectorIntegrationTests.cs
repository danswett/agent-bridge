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

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
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
    // Each echo by the handle CreateProcess gave, kept open until the end: some tests let an
    // echo exit, and only a handle, never its id, is sure to reach the process started here.
    readonly List<nint> started = [];

    static string Worker => Environment.GetEnvironmentVariable("AGENT_BRIDGE_INJECTOR") is { Length: > 0 } set
        ? set : Path.Combine(AppContext.BaseDirectory, WorkerInjector.FileName);

    // Whether the tests run as administrator, as on GitHub's Windows runners; the echoes they
    // start are then elevated too.
    static bool Elevated => OperatingSystem.IsWindows() && TargetGuard.IntegrityLevel(Environment.ProcessId) >= 0x3000;

    public void Dispose()
    {
        foreach (var handle in started)
        {
            TerminateProcess(handle, 1);
            WaitForSingleObject(handle, 5000);
            CloseHandle(handle);
        }
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed record Echo(int Pid, nint Window, string Log);

    // Starts the echo program in a console of its own (CREATE_NEW_CONSOLE), hidden, and waits
    // for it to say it is reading. With exitOn, it exits once that line is typed and entered.
    Echo StartEcho(string? exitOn = null)
    {
        var log = Path.Combine(root, $"echo-{Guid.NewGuid():N}.log");
        var exe = Path.Combine(AppContext.BaseDirectory, "agent-bridge-console-echo.exe");
        var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1 /* STARTF_USESHOWWINDOW */, ShowWindow = 0 /* SW_HIDE */ };
        var command = new StringBuilder($"\"{exe}\" \"{log}\"{(exitOn is null ? "" : $" --exit-on \"{exitOn}\"")}");
        Assert.True(CreateProcessW(null, command, 0, 0, false, 0x10 /* CREATE_NEW_CONSOLE */, 0, null, ref info, out var process), $"CreateProcess {Marshal.GetLastWin32Error()}");
        CloseHandle(process.Thread);
        started.Add(process.Process);
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
    public async Task A_Claude_multi_select_and_a_Codex_approval_reach_the_console_as_their_digits_arrows_letters_and_Enters()
    {
        var echo = StartEcho();
        var claude = Claude.Ask(("Which ones?", true, ["a", "b", "c"]));
        using var input = System.Text.Json.JsonDocument.Parse(claude.ToJsonString());
        var (form, _) = AgentBridge.Host.Delivery.Claude.ClaudeForm.Read(input.RootElement);
        var (plan, _) = AgentBridge.Host.Delivery.Claude.ClaudePlanner.Plan(form!, CopilotPlannerTests.Answer(new Dictionary<string, object> { ["Which ones?"] = new[] { "a", "c" } }));
        var (approve, _, _) = AgentBridge.Host.Delivery.Codex.CodexApproval.Plan(CopilotPlannerTests.Answer(new { decision = "approve" }).Fields);
        var injector = new WorkerInjector(Worker);

        Assert.Equal(InjectionStatus.Typed, (await injector.InjectAsync(echo.Pid, plan!.Keys)).Status);
        Assert.Equal(InjectionStatus.Typed, (await injector.InjectAsync(echo.Pid, approve!)).Status);

        // 1, 3, Down x4, Enter; then y, Enter.
        var down = new[] { 27, 91, 66 };
        int[] expected = [49, 51, .. down, .. down, .. down, .. down, 13, 121, 13];
        var got = WaitFor(echo.Log, lines => Keys(lines) is { } k && k.Count >= expected.Length * 2 ? k : null);
        Assert.Equal(expected, got.Where(k => k.StartsWith("key 1", StringComparison.Ordinal)).Select(k => int.Parse(k.Split(' ')[3], CultureInfo.InvariantCulture)));
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

    // A reply through the real ReplyService and the real worker into a real console. The
    // echo program plays Copilot: once Enter arrives, what was typed before it is written to
    // the transcript as the person's prompt, which is how the reply is confirmed.
    [WindowsTheory]
    [InlineData("Looks good.\r\nNow open the PR – 世界 🚀 \"q\" \\", "Looks good.  Now open the PR – 世界 🚀 \"q\" \\")]
    [InlineData(null, null)]
    public async Task A_reply_is_typed_into_the_console_as_one_line_then_Enter_and_is_delivered_once_recorded(string? text, string? typed)
    {
        // The longest reply allowed: 16 KiB of UTF-8, every byte a key down and up.
        text ??= string.Concat(Enumerable.Repeat("abcdefgh", AgentBridge.Protocol.Limits.ReplyText / 8));
        typed ??= text;
        var echo = StartEcho();
        using var f = new Fixture();
        const string Id = "33333333-3333-4333-8333-333333333333";
        // The lock is newer than the process, as a live CLI's is; an older one is a dead CLI's.
        var events = f.CopilotSession(Id, echo.Pid, lockWrite: DateTimeOffset.UtcNow);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        f.Processes.Run(AgentBridge.Protocol.AgentKind.Copilot, echo.Pid);
        await using var source = new AgentBridge.Host.Core.SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        var replies = new ReplyService(source, f.Processes, new NoBridge(), new NoGuard(), new WorkerInjector(Worker),
            new ReplyOptions { CopilotHome = f.CopilotHome, PlatformSupported = true, ConfirmTimeout = TimeSpan.FromSeconds(60), PollInterval = TimeSpan.FromMilliseconds(50) });
        var keyDowns = text.Length + 1;
        var cli = Task.Run(() =>
        {
            var downs = WaitFor(echo.Log, lines => Keys(lines).Where(k => k.StartsWith("key 1 ", StringComparison.Ordinal)).ToList() is { } d && d.Count >= keyDowns ? d : null, seconds: 60);
            Assert.Equal("key 1 13 13", downs[^1]);
            var words = new string([.. downs.SkipLast(1).Select(k => (char)int.Parse(k.Split(' ')[3], CultureInfo.InvariantCulture))]);
            Fixture.Append(events, Copilot.Event("user.message", new { content = words, delivery = "idle" }, 5));
            return words;
        });

        var result = await replies.ReplyAsync(new AgentBridge.Protocol.ReplyRequest { Id = Id, Request = Guid.NewGuid().ToString(), Text = text });

        Assert.Equal(typed, await cli);
        Assert.Equal((AgentBridge.Protocol.AnswerOutcome.Delivered, (string?)null), (result.Outcome, result.Reason));
    }

    // A reply with a file: staged privately, its path typed as an @ mention ahead of the words
    // by the real worker, and delivered once the "CLI" records the prompt with it attached.
    [WindowsFact]
    public async Task A_reply_with_an_attachment_types_its_staged_path_as_an_at_mention_and_is_delivered_once_attached()
    {
        var echo = StartEcho();
        using var f = new Fixture();
        const string Id = "44444444-4444-4444-8444-444444444444";
        var events = f.CopilotSession(Id, echo.Pid);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        f.Processes.Run(AgentBridge.Protocol.AgentKind.Copilot, echo.Pid);
        await using var source = new AgentBridge.Host.Core.SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        using var store = new AgentBridge.Host.Delivery.Staging.AttachmentStore(new() { StagingRoot = Path.Combine(f.Root, "attachments") }, sweep: false);
        var replies = new ReplyService(source, f.Processes, new NoBridge(), new NoGuard(), new WorkerInjector(Worker),
            new ReplyOptions { CopilotHome = f.CopilotHome, PlatformSupported = true, ConfirmTimeout = TimeSpan.FromSeconds(60), PollInterval = TimeSpan.FromMilliseconds(50) },
            attachments: store);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var file = Guid.NewGuid().ToString();
        store.Start("phone", new() { Attachment = file, Name = "shot.png", Size = png.Length, Sha256 = AgentBridge.Protocol.Attachments.Base64Url(System.Security.Cryptography.SHA256.HashData(png)) });
        store.Chunk("phone", new() { Attachment = file, Offset = 0, Data = Convert.ToBase64String(png) });
        var cli = Task.Run(() =>
        {
            var downs = WaitFor(echo.Log, lines => Keys(lines).Where(k => k.StartsWith("key 1 ", StringComparison.Ordinal)).ToList() is { Count: > 0 } d && d[^1] == "key 1 13 13" ? d : null, seconds: 60);
            var words = new string([.. downs.SkipLast(1).Select(k => (char)int.Parse(k.Split(' ')[3], CultureInfo.InvariantCulture))]);
            var path = words.Split(' ')[0][1..];
            Fixture.Append(events, Copilot.Event("user.message", new { content = words, attachments = new[] { new { type = "file", path, displayName = "@" + path } }, delivery = "idle" }, 5));
            return (words, path);
        });

        var result = await replies.ReplyAsync(new AgentBridge.Protocol.ReplyRequest { Id = Id, Request = Guid.NewGuid().ToString(), Text = "what is this?", Attachments = [new() { Id = file }] }, "phone");

        var (typedWords, staged) = await cli;
        Assert.Equal($"@{staged} what is this?", typedWords);
        Assert.Equal(png, File.ReadAllBytes(staged));
        Assert.Equal((AgentBridge.Protocol.AnswerOutcome.Delivered, (string?)null), (result.Outcome, result.Reason));
    }

    sealed class NoBridge : IAgentHaBridgeProbe
    {
        public string? Active() => null;
    }

    // Ending a session through the real StopService, the real worker and the real process
    // control, against an echo program of the tests' own playing the agent. Only processes
    // started here are ever opened, typed into or terminated.
    [WindowsTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ending_an_idle_session_types_exit_and_terminates_it_through_its_handle_only_if_it_does_not_exit(bool exits)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var echo = StartEcho(exits ? "/exit" : null);
        using var f = new Fixture();
        const string Id = "44444444-4444-4444-8444-444444444444";
        // The lock is newer than the process, as a live CLI's is; an older one is a dead CLI's.
        var events = f.CopilotSession(Id, echo.Pid, lockWrite: DateTimeOffset.UtcNow);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        using (var p = Process.GetProcessById(echo.Pid))
        {
            f.Processes.Run(AgentBridge.Protocol.AgentKind.Copilot, echo.Pid, startedAt: new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        await using var source = new AgentBridge.Host.Core.SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        var stops = new StopService(source, f.Processes, new NoBridge(), new WorkerInjector(Worker), new WindowsProcessControl(),
            new StopOptions { CopilotHome = f.CopilotHome, PlatformSupported = true, Grace = TimeSpan.FromSeconds(exits ? 15 : 1) });

        var result = await stops.StopAsync(new AgentBridge.Protocol.StopRequest { Id = Id, Request = Guid.NewGuid().ToString() });

        output.WriteLine($"{result.Outcome} {result.Reason} forced={result.Forced}");
        // A test run elevated (GitHub's Windows runners are) starts an elevated echo, which
        // the host must refuse before typing or terminating anything: the real check, for real.
        if (Elevated)
        {
            Assert.Equal(AgentBridge.Protocol.StopOutcome.Refused, result.Outcome);
            Assert.Contains("runs as administrator", result.Reason);
            Thread.Sleep(500);
            Assert.Empty(Keys(WaitFor(echo.Log, lines => lines)));
            using var p = Process.GetProcessById(echo.Pid);
            Assert.False(p.HasExited);
            return;
        }
        Assert.Equal((AgentBridge.Protocol.StopOutcome.Ended, exits ? (bool?)null : true), (result.Outcome, result.Forced));
        var typed = WaitFor(echo.Log, lines => Keys(lines).Where(k => k.StartsWith("key 1 ", StringComparison.Ordinal)).ToList() is { Count: >= 6 } d ? d : null);
        Assert.Equal("/exit\r", new string([.. typed.Take(6).Select(k => (char)int.Parse(k.Split(' ')[3], CultureInfo.InvariantCulture))]));
        Assert.Equal(exits, WaitFor(echo.Log, lines => lines.Length > 0 ? lines : null).Contains("exiting"));
        Assert.True(Exited(echo.Pid));
    }

    [WindowsFact]
    public void The_process_control_holds_only_the_process_that_started_when_the_session_said_and_never_the_host_itself()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var echo = StartEcho();
        DateTimeOffset started;
        using (var p = Process.GetProcessById(echo.Pid)) { started = new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero); }
        var control = new WindowsProcessControl();

        var opened = control.Open(echo.Pid, started);
        using (var held = opened.Process)
        {
            if (Elevated) { Assert.Contains("runs as administrator", opened.Refusal); }
            else
            {
                Assert.NotNull(held);
                Assert.Equal(echo.Pid, held.Id);
            }
        }
        // Another start time is another process that has taken the id: as good as gone.
        Assert.Equal((null, true, null), Shape(control.Open(echo.Pid, started.AddSeconds(-5))));
        Assert.Contains("this host", control.Open(Environment.ProcessId, DateTimeOffset.UtcNow).Refusal);
        // Nothing here was ended.
        using (var p = Process.GetProcessById(echo.Pid)) { Assert.False(p.HasExited); }

        static (IHeldProcess?, bool, string?) Shape(HeldOpen o) => (o.Process, o.Gone, o.Refusal);
    }

    sealed class NoGuard : ITargetGuard
    {
        public string? Refuse(int processId) => null;
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

    [DllImport("kernel32.dll")]
    static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(nint handle, uint milliseconds);

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
