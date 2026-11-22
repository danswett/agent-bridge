using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed class LaunchReplyActivityTests
{
    [Theory]
    [InlineData(false, "tool")]
    [InlineData(true, "tool")]
    [InlineData(false, "argument")]
    [InlineData(true, "argument")]
    [InlineData(false, "turn")]
    [InlineData(true, "turn")]
    [InlineData(false, "subagent")]
    [InlineData(true, "subagent")]
    public async Task Own_activity_after_process_start_releases_pending_and_finished_launch_holds_without_a_new_prompt(bool followed, string activity)
    {
        await using var r = new Rig();
        var argumentPrompt = activity == "argument";
        // Already inside From, but after the process started and before Pending.At.
        if (argumentPrompt) { Fixture.Append(r.Events, Copilot.User("launch argument", 3599)); }
        var from = File.Exists(r.Events) ? new FileInfo(r.Events).Length : 0;
        await r.BeginAsync(followed, argumentPrompt ? "launch argument" : null);
        if (!argumentPrompt)
        {
            Assert.NotNull(r.Launcher.HeldForReply(Rig.Id));
            Fixture.Append(r.Events, activity switch
            {
                "turn" => Copilot.TurnStart(3601),
                "subagent" => Copilot.SubagentStarted("task", "helper", 3601),
                _ => Copilot.ToolStart("work", "powershell", new { }, 3601),
            });
            if (activity == "tool") { Fixture.Append(r.Events, Copilot.ToolDone("work", 3602)); }
        }
        Assert.Empty(CopilotPrompts.Since(r.Events, from).Prompts);

        Assert.Null(r.Launcher.HeldForReply(Rig.Id));
        r.Rows = Rig.Ready;
        Assert.Equal(AnswerOutcome.Delivered, (await r.ReplyAsync()).Outcome);
        Assert.Single(r.Typed);
    }

    [Theory]
    [InlineData(false, "trust")]
    [InlineData(true, "trust")]
    [InlineData(false, "ask")]
    [InlineData(true, "ask")]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    public async Task Activity_never_lets_a_reply_answer_drawn_trust_or_an_open_question_or_permission(bool followed, string gate)
    {
        await using var r = new Rig();
        await r.BeginAsync(followed);
        Fixture.Append(r.Events, Copilot.ToolStart("work", "powershell", new { }, 3601), Copilot.ToolDone("work", 3602));
        r.Rows = gate == "trust" ? ["Confirm folder trust", "Do you trust the files in this folder?", "❯ 1. Yes"] : Rig.Ready;
        if (gate == "ask") { Fixture.Append(r.Events, Copilot.ToolStart("question", "ask_user", new { message = "Which?" }, 3603, agentId: "helper")); }
        if (gate == "permission") { Fixture.Append(r.Events, Copilot.Event("permission.requested", new { requestId = "permission", permissionRequest = new { kind = "shell" } }, 3603)); }

        Assert.Equal(AnswerOutcome.Refused, (await r.ReplyAsync()).Outcome);
        Assert.Empty(r.Typed);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "unreadable")]
    [InlineData(true, "unreadable")]
    [InlineData(false, "resume")]
    [InlineData(true, "resume")]
    [InlineData(false, "resume-only")]
    [InlineData(true, "resume-only")]
    public async Task Missing_or_unreadable_events_old_activity_and_resume_alone_do_not_release_a_launch(bool followed, string evidence)
    {
        await using var r = new Rig();
        if (evidence == "resume") { Fixture.Append(r.Events, Copilot.User("old prompt", 1), Copilot.TurnStart(2), Copilot.ToolStart("old", "powershell", new { }, 3), Copilot.ToolDone("old", 4)); }
        await r.BeginAsync(followed);
        if (evidence == "unreadable") { Directory.CreateDirectory(r.Events); }
        if (evidence.StartsWith("resume", StringComparison.Ordinal)) { Fixture.Append(r.Events, Copilot.Event("session.resume", new { }, 3601)); }

        Assert.NotNull(r.Launcher.HeldForReply(Rig.Id));
        Assert.Equal(AnswerOutcome.Refused, (await r.ReplyAsync()).Outcome);
        Assert.Empty(r.Typed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_first_reply_still_requires_a_readable_real_composer_with_or_without_activity(bool activity, bool unreadable)
    {
        await using var r = new Rig();
        await r.BeginAsync(followed: true);
        r.Replier.LaunchHeld = null;
        if (activity) { Fixture.Append(r.Events, Copilot.ToolStart("work", "powershell", new { }, 3601), Copilot.ToolDone("work", 3602)); }
        r.Rows = unreadable ? null : [];

        Assert.Equal(AnswerOutcome.Refused, (await r.ReplyAsync()).Outcome);
        Assert.Empty(r.Typed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activity_written_during_the_startup_screen_probe_releases_only_a_safe_first_reply(bool asking)
    {
        await using var r = new Rig();
        await r.BeginAsync(followed: true);
        r.Replier.LaunchHeld = null;
        var reads = 0;
        r.ReadRows = () =>
        {
            if (++reads != 2) { return Rig.Ready; }
            Assert.False(File.Exists(r.Events));
            Fixture.Append(r.Events, Copilot.ToolStart("work", "powershell", new { }, 3601));
            if (asking) { Fixture.Append(r.Events, Copilot.ToolStart("question", "ask_user", new { message = "Which?" }, 3602)); }
            return null;
        };

        Assert.Equal(asking ? AnswerOutcome.Refused : AnswerOutcome.Delivered, (await r.ReplyAsync()).Outcome);
        Assert.Equal(asking ? 0 : 1, r.Typed.Count);
    }

    // LaunchFlow's synthetic registration and Reply fixture's transcript confirmation, no relay or real console.
    sealed class Rig : IAsyncDisposable, IAgentStarter, IAgentLocator, ICommandRunner, IConsoleInjector, IAgentHaBridgeProbe, ITargetGuard, ILaunchAccount
    {
        public const string Id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        public static readonly string[] Ready = ["╻▄▄▄▄▄▄▄▄", "┃", "╹▀▀▀▀▀▀▀▀", " ← open sidebar · Interactive · Allow All · / commands · ? help · tab next tab"];
        readonly Fixture f = new();
        readonly SessionSource source;
        readonly TaskCompletionSource firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource followDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenSource stopping = new();
        readonly string workspace;
        int reads;
        public readonly LaunchService Launcher;
        public readonly ReplyService Replier;
        public readonly List<KeyPlan> Typed = [];
        public IReadOnlyList<string>? Rows = [];
        public Func<IReadOnlyList<string>?>? ReadRows;
        public string Events { get; }

        public Rig()
        {
            workspace = Directory.CreateDirectory(Path.Combine(f.Root, "workspace")).FullName;
            Events = f.CopilotSession(Id, null);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Events)!, "workspace.yaml"), $"id: {Id}\ncwd: '{workspace}'\nsummary: Launch activity\n");
            source = f.Source();
            var gate = new DeliveryGate();
            Launcher = new(gate, source, this, this, this, this, new AgencyCatalog(this), new LaunchServiceOptions
            {
                Time = f.Time, Config = LaunchConfig.Default with { Workspaces = [new("App", workspace, false)] },
                CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, CodexHome = f.CodexHome,
                PlatformSupported = true, IsSystemDirectory = _ => false, RegistrationWait = TimeSpan.FromSeconds(5),
                ReadyWait = TimeSpan.Zero, ScreenAfter = TimeSpan.Zero, ScreenEvery = TimeSpan.FromDays(1),
                PollInterval = TimeSpan.FromSeconds(1), FollowFor = TimeSpan.FromMinutes(1),
                ProvenanceFile = Path.Combine(f.Root, "provenance.json"),
            }, log: line => { if (line.Contains("replies to session", StringComparison.Ordinal) || line.Contains("registered as session", StringComparison.Ordinal)) { followDone.TrySetResult(); } }, account: this);
            Replier = new(gate, source, f.Processes, this, this, this, new ReplyOptions
            {
                CopilotHome = f.CopilotHome, PlatformSupported = true, PreRead = false,
                ConfirmTimeout = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(1),
            }) { LaunchHeld = Launcher.HeldForReply, Stopping = stopping.Token };
        }

        public async Task BeginAsync(bool followed, string? prompt = null)
        {
            var result = await Launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Resume = Id, Prompt = prompt }, "fixture").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(LaunchOutcome.Unconfirmed, result.Outcome);
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (followed) { await FinishFollowingAsync(); }
        }

        async Task FinishFollowingAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!followDone.Task.IsCompleted)
            {
                f.Time.Advance(TimeSpan.FromMinutes(1));
                await Task.WhenAny(followDone.Task, Task.Delay(1, timeout.Token)).WaitAsync(timeout.Token);
            }
        }

        public Task<ReplyResult> ReplyAsync() => Replier.ReplyAsync(new ReplyRequest { Id = Id, Request = Guid.NewGuid().ToString(), Text = "continue" }).WaitAsync(TimeSpan.FromSeconds(10));
        public string? Unavailable() => null;
        public string? Active() => null;
        public string? Refuse(int pid) => null;
        public string? Find(Launcher launcher, AgentConfig config) => launcher.Id == "copilot" ? @"C:\fake\copilot.exe" : null;
        public string? CommandLine(StartSpec spec, out string? problem) { problem = null; return "synthetic copilot"; }
        public bool Running(Started started) => true;
        public Started Start(StartSpec spec)
        {
            var began = f.Time.GetUtcNow().AddSeconds(-2);
            f.CopilotSession(Id, 4001, lockWrite: f.Time.GetUtcNow());
            f.Processes.Run(AgentKind.Copilot, 4001, $"copilot --session-id {Id}", began);
            return new(4001, began);
        }
        public Task<LaunchAccount> CheckAsync(string? login, CancellationToken ct) => Task.FromResult(LaunchAccount.Allowed("synthetic-test-token"));
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(new CommandResult(true, false, 0, """{"sessions":[]}"""));
        public Task<ConsoleScreen> ReadScreenAsync(int pid, CancellationToken ct = default)
        {
            var rows = ReadRows is { } read ? read() : Rows;
            if (Interlocked.Increment(ref reads) == 2) { firstRead.TrySetResult(); }
            return Task.FromResult(new ConsoleScreen(rows, rows is null ? "unreadable" : null));
        }
        public Task<InjectionResult> InjectAsync(int pid, KeyPlan plan, CancellationToken ct = default)
        {
            if (plan.Proceed?.Invoke() is { } why) { return Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, why)); }
            Typed.Add(plan);
            Fixture.Append(Events, Copilot.User(plan.Sends[0].Text, 3605));
            return Task.FromResult(new InjectionResult(InjectionStatus.Typed, "synthetic"));
        }
        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            await FinishFollowingAsync();
            await source.DisposeAsync();
            stopping.Dispose();
            f.Dispose();
        }
    }
}
