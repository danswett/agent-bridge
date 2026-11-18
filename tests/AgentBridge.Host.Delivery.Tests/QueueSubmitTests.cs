using System.Reflection;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;
using Xunit;

public sealed class QueueSubmitTests : IAsyncLifetime
{
    const string Id = "22222222-2222-4222-8222-222222222222";
    readonly Fixture f = new();
    readonly QualificationControllerTests.Run run = new();
    readonly ConsoleFake console = new();
    SessionSource source = null!;
    ReplyService service = null!;
    QualificationController controller = null!;
    string events = "";
    ReplyIntent intent = ReplyIntent.Queueable;

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, 42);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        f.Processes.Run(AgentKind.Copilot, 42, startedAt: Fixture.T0);
        source = f.Source(); await source.RefreshAsync();
        run.Probe.Identity = run.Probe.Identity!.Replace("identity 42 100 ", $"identity 42 {Fixture.T0.ToFileTime()} ");
        controller = run.Controller();
        Assert.Equal(QueueCapability.Verified, (await controller.StartAsync(run.Authority.Id)).Capability);
        service = new(new DeliveryGate(), source, f.Processes, new NoBridge(), new NoGuard(), console,
            new ReplyOptions { CopilotHome = f.CopilotHome, PlatformSupported = true, PreRead = false,
                ConfirmTimeout = TimeSpan.Zero });
        // Optional on the parent so this RED compiles and exercises its existing delivery path.
        Wire(controller);
        console.Record = text => Fixture.Append(events, Copilot.Event("user.message", new { content = text, delivery = "queued" }, 3));
    }

    public async Task DisposeAsync() { await source.DisposeAsync(); f.Dispose(); }
    void Wire(QualificationController? value) =>
        typeof(ReplyService).GetProperty("QueueController", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(service, value);
    Task<ReplyResult> Send(string text = "first", string? rid = null) =>
        service.ReplyAsync(new ReplyRequest { Id = Id, Request = rid ?? Guid.NewGuid().ToString(), Text = text, IntentSource = () => intent });

    [Fact]
    public async Task Busy_qualified_deliveries_submit_once_per_rid_in_order_and_leave_the_composer_empty()
    {
        Assert.Equal(SessionStatus.Working, source.Get(Id)!.Status);
        var rid = Guid.NewGuid().ToString();
        Assert.Equal(AnswerOutcome.Delivered, (await Send(rid: rid)).Outcome);
        Assert.Equal(AnswerOutcome.Delivered, (await Send(rid: rid)).Outcome);
        Assert.Equal(AnswerOutcome.Delivered, (await Send("second")).Outcome);
        Assert.Equal(new[] { "first", "second" }, console.Submitted);
        Assert.Equal(new[] { "q", "q" }, console.Actions);
        Assert.Equal("", console.Draft);
    }

    [Theory]
    [InlineData("unknown")][InlineData("unsupported")][InlineData("expired")][InlineData("identity")][InlineData("missing")][InlineData("target")]
    public async Task Unqualified_or_changed_capability_refuses_before_text_or_keys(string change)
    {
        if (change == "unknown") { await controller.EndAsync(); }
        else if (change == "unsupported")
        {
            var other = new QualificationControllerTests.Run(); other.Probe.Submission = QualificationSubmission.Unsupported;
            controller = other.Controller(); await controller.StartAsync(other.Authority.Id); Wire(controller);
        }
        else if (change == "expired") { run.Authority.Clock.Advance(TimeSpan.FromMinutes(1)); }
        else if (change == "missing") { Wire(null); }
        else if (change == "target") { run.Probe.Identity = run.Probe.Identity!.Replace("identity 42 ", "identity 43 "); }
        else { run.Probe.Identity = null; }
        Assert.Equal(AnswerOutcome.Refused, (await Send()).Outcome);
        Assert.Empty(console.Actions); Assert.Equal("", console.Draft); Assert.Equal(0, console.TextWrites);
    }

    [Fact]
    public async Task A_queue_key_not_consumed_clears_only_the_owned_text_and_reports_failure_without_resubmitting()
    {
        console.AcceptQueue = false;
        var rid = Guid.NewGuid().ToString();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Send(rid: rid)).Outcome);
        await Send(rid: rid);
        Assert.Equal(new[] { "q", "u" }, console.Actions);
        Assert.Equal("", console.Draft); Assert.Empty(console.Submitted);
    }

    [Theory]
    [InlineData("owner")][InlineData("intent")][InlineData("revoke")]
    public async Task A_change_after_text_never_submits_or_clears_an_owner_edit(string change)
    {
        console.AfterText = () =>
        {
            if (change == "owner") { console.Draft += " owner edit"; }
            else if (change == "intent") { intent = ReplyIntent.Unknown; }
            else { run.Authority.Grants().Revoke(run.Authority.Id); }
        };
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Send()).Outcome);
        Assert.Empty(console.Actions); Assert.Empty(console.Submitted);
        if (change == "owner") { Assert.Equal("first owner edit", console.Draft); }
    }

    [Fact]
    public async Task An_owner_draft_is_occupied_and_is_never_overwritten()
    {
        console.Draft = "owner draft";
        Assert.Equal(ReplyRefusal.ComposerUnverified, (await Send()).Refusal);
        Assert.Equal("owner draft", console.Draft); Assert.Equal(0, console.TextWrites);
    }

    [Theory]
    [InlineData("q")][InlineData("u")][InlineData("invalid")]
    public void The_real_worker_decoder_distinguishes_queue_cleanup_and_invalid_actions(string action)
    {
        var program = Assembly.Load("agent-bridge-injector").GetType("AgentBridge.Injector.Program")!;
        var read = program.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"agent-bridge-keys 2\nsend  {action} 0 0\n"));
        var sends = read.Invoke(null, [stream]) as System.Collections.IList;
        if (action == "invalid") { Assert.Null(sends); }
        else { Assert.NotNull(sends); Assert.Equal(action, sends[0]!.GetType().GetProperty("Submit")!.GetValue(sends[0])!.ToString()); }
    }

    sealed class NoBridge : IAgentHaBridgeProbe { public string? Active() => null; }
    sealed class NoGuard : ITargetGuard { public string? Refuse(int processId) => null; }
    sealed class ConsoleFake : IConsoleInjector
    {
        public string Draft = "";
        public bool AcceptQueue = true;
        public int TextWrites;
        public Action? AfterText;
        public Action<string>? Record;
        public readonly List<string> Submitted = [], Actions = [];
        IReadOnlyList<string> Rows() => ["\u257B" + new string('\u2584', 99), "\u2503 " + Draft,
            "\u2579" + new string('\u2580', 99), " Working esc interrupt \u00B7 ctrl+q enqueue"];
        public Task<ConsoleScreen> ReadScreenAsync(int pid, CancellationToken ct = default) => Task.FromResult(new ConsoleScreen(Rows(), null));
        public async Task<InjectionResult> InjectAsync(int pid, KeyPlan plan, CancellationToken ct = default)
        {
            if (plan.Proceed?.Invoke() is { } stop) { return new(InjectionStatus.NothingTyped, stop); }
            if (typeof(KeyPlan).GetProperty("ProceedAsync", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plan)
                is Func<Task<string?>> fence && await fence() is { } stopped) { return new(InjectionStatus.NothingTyped, stopped); }
            var tokens = plan.ToWorkerScript().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            for (var i = 0; i < plan.Sends.Count; i++)
            {
                var send = plan.Sends[i]; var rows = Rows();
                if ((send.Expect ?? []).Any(r => r.Row >= rows.Count || rows[r.Row].TrimEnd(' ') != r.Text.TrimEnd(' '))
                    || (send.Checks ?? []).Any(c => !c.Passes(rows))) { return new(InjectionStatus.NothingTyped, "screen changed"); }
                if (send.Text.Length > 0) { Draft += send.Text; TextWrites++; AfterText?.Invoke(); }
                var action = tokens[i].Split(' ')[2];
                if (action == "0") { continue; }
                Actions.Add(action);
                if (action == "u") { Draft = ""; }
                else if (action != "q" || AcceptQueue) { Submitted.Add(Draft); Record?.Invoke(Draft); Draft = ""; }
            }
            return new(InjectionStatus.Typed, "ok");
        }
    }
}
