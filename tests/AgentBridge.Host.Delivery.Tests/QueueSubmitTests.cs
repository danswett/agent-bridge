using System.Reflection;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Protocol;
using Xunit;

public sealed class QueueSubmitTests : IAsyncLifetime
{
    const string Id = "22222222-2222-4222-8222-222222222222";
    readonly Fixture f = new();
    readonly QualificationControllerTests.Run run = new(Fixture.T0.ToFileTime());
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
        controller = run.Controller();
        Assert.Equal(QueueCapability.Verified, (await run.Start(controller)).Capability);
        service = new(new DeliveryGate(), source, f.Processes, new NoBridge(), new NoGuard(), console,
            new ReplyOptions { CopilotHome = f.CopilotHome, PlatformSupported = true, PreRead = false,
                ConfirmTimeout = TimeSpan.Zero });
        Wire(controller);
        console.Record = text => Fixture.Append(events, Copilot.Event("user.message", new { content = text, delivery = "queued" }, 3));
    }

    public async Task DisposeAsync() { await source.DisposeAsync(); f.Dispose(); }
    void Wire(QualificationController? value) => service.QueueController = value;
    Task<ReplyResult> Send(string text = "first", string? rid = null) =>
        service.ReplyAsync(new ReplyRequest { Id = Id, Request = rid ?? Guid.NewGuid().ToString(), Text = text, IntentSource = () => intent });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Busy_qualified_deliveries_submit_once_per_rid_in_order_and_leave_the_composer_empty(bool plain)
    {
        console.Plain = plain; Assert.Equal(SessionStatus.Working, source.Get(Id)!.Status);
        var rid = Guid.NewGuid().ToString();
        Assert.Equal(AnswerOutcome.Delivered, (await Send(rid: rid)).Outcome);
        Assert.Equal(AnswerOutcome.Delivered, (await Send(rid: rid)).Outcome);
        Assert.Equal(1, console.TextWrites); Assert.Equal(new[] { "q" }, console.Actions); Assert.Equal(new[] { "first" }, console.Submitted);
        Assert.Equal(AnswerOutcome.Delivered, (await Send("second")).Outcome);
        Assert.Equal(new[] { "first", "second" }, console.Submitted);
        Assert.Equal(new[] { "q", "q" }, console.Actions);
        Assert.Equal("", console.Draft); Assert.Equal(2, console.TextWrites);
    }

    [Theory]
    [InlineData("unknown")][InlineData("unsupported")][InlineData("expired")][InlineData("identity")][InlineData("missing")][InlineData("target")]
    public async Task Unqualified_or_changed_capability_refuses_before_text_or_keys(string change)
    {
        if (change == "unknown") { await controller.EndAsync(); }
        else if (change == "unsupported")
        {
            var other = new QualificationControllerTests.Run(); other.Probe.Submission = QualificationSubmission.Unsupported;
            controller = other.Controller(); await other.Start(controller); Wire(controller);
        }
        else if (change == "expired") { run.Authority.Clock.Advance(TimeSpan.FromMinutes(1)); }
        else if (change == "missing") { Wire(null); }
        else if (change == "target") { run.Probe.Identity = run.Probe.Identity!.Replace("identity 42 ", "identity 43 "); }
        else { run.Probe.Identity = null; }
        Assert.Equal(AnswerOutcome.Refused, (await Send()).Outcome);
        Assert.Empty(console.Actions); Assert.Equal("", console.Draft); Assert.Equal(0, console.TextWrites);
    }

    [Theory]
    [InlineData(false, "one two", false)][InlineData(false, "one two", true)]
    [InlineData(true, "one two", false)][InlineData(true, "one two", true)]
    [InlineData(false, " one two ", false)][InlineData(false, " one two ", true)]
    public async Task A_failed_queue_key_cleans_exact_owned_text_but_preserves_internal_space_edits(bool edited, string words, bool plain)
    {
        console.AcceptQueue = false; console.Plain = plain;
        console.AfterQueue = () => { if (edited) { console.Draft = "one  two"; } };
        var rid = Guid.NewGuid().ToString();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Send(words, rid)).Outcome);
        Assert.Equal(edited ? "one  two" : "", console.Draft);
        Assert.Equal(edited ? new[] { "q" } : ["q", "u"], console.Actions);
        await Send(words, rid);
        Assert.Equal(edited ? new[] { "q" } : ["q", "u"], console.Actions);
        Assert.Equal(edited ? "one  two" : "", console.Draft); Assert.Empty(console.Submitted); Assert.Equal(1, console.TextWrites);
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
        Assert.Equal(ReplyRefusal.Occupied, (await Send()).Refusal);
        Assert.Equal("owner draft", console.Draft); Assert.Equal(0, console.TextWrites);
    }

    [Fact]
    public async Task Revocation_during_the_injectors_own_wait_is_rechecked_before_any_key()
    {
        console.BeforeKeys = () => run.Authority.Grants().Revoke(run.Authority.Id);
        Assert.Equal(AnswerOutcome.Refused, (await Send()).Outcome);
        Assert.Equal(0, console.TextWrites); Assert.Empty(console.Actions);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task An_owner_edit_after_the_queue_key_is_never_cleared(bool accepted)
    {
        console.AcceptQueue = accepted; console.AfterQueue = () => console.Draft += " owner edit";
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Send()).Outcome);
        Assert.Equal(new[] { "q" }, console.Actions); Assert.EndsWith(" owner edit", console.Draft);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Conflicting_text_or_Enter_with_a_queue_action_is_rejected_before_serialization(bool enter)
    {
        var plan = new KeyPlan([new KeySend(enter ? "" : "text", enter, 0, 0) { Submit = SubmitKey.Queue }]);
        Assert.Throws<InvalidOperationException>(() => plan.ToWorkerScript());
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

    [Theory]
    [InlineData("\u0011", true, 81, 8u)][InlineData("\u0015", true, 85, 8u)][InlineData("\u000b", true, 75, 8u)][InlineData("\r", false, 13, 0u)]
    public void The_real_worker_encodes_a_chord_as_distinct_modified_key_down_and_up(string text, bool control, int vk, uint modifiers)
    {
        var program = Assembly.Load("agent-bridge-injector").GetType("AgentBridge.Injector.Program")!;
        var records = (Array)program.GetMethod("Records", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [text, control])!;
        Assert.Equal(2, records.Length);
        for (var i = 0; i < 2; i++)
        {
            var record = records.GetValue(i)!; var type = record.GetType();
            Assert.Equal((ushort)vk, type.GetField("VirtualKeyCode")!.GetValue(record));
            Assert.Equal(modifiers, type.GetField("ControlKeyState")!.GetValue(record));
            Assert.Equal(1 - i, type.GetField("KeyDown")!.GetValue(record));
        }
    }

    [Theory]
    [InlineData("", null)][InlineData("fail partial 0", false)][InlineData("fail partial 1", true)][InlineData("wrote 1", true)]
    public void The_workers_Enter_announcement_preserves_uncertainty_and_partial_write_evidence(string after, bool? entered)
    {
        var program = Assembly.Load("agent-bridge-injector").GetType("AgentBridge.Injector.Program")!;
        var announcement = (string)program.GetMethod("SubmissionAnnouncement", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [1, '1'])!;
        var evidence = new WorkerInjector.ReplyWriteEvidence(new KeyPlan([new("report", false, 0, 0), new("", true, 0, 0)]));
        foreach (var line in new[] { "writing 0", "wrote 0", announcement, after }) { evidence.Read(line); }
        Assert.Equal(entered, evidence.Apply(new(InjectionStatus.Interrupted, "worker stopped")).EnterWritten);
    }

    sealed class NoBridge : IAgentHaBridgeProbe { public string? Active() => null; }
    sealed class NoGuard : ITargetGuard { public string? Refuse(int processId) => null; }
    sealed class ConsoleFake : IConsoleInjector
    {
        public string Draft = "";
        public bool AcceptQueue = true, Plain;
        public int TextWrites;
        public Action? AfterText;
        public Action? BeforeKeys;
        public Action? AfterQueue;
        public Action<string>? Record;
        public readonly List<string> Submitted = [], Actions = [];
        IReadOnlyList<string> Rows() => Plain
            ? [new string('\u2500', 100), "\u276F " + Draft, new string('\u2500', 100), " Working esc interrupt \u00B7 ctrl+q enqueue"]
            : ["\u257B" + new string('\u2584', 99), "\u2503 " + Draft, "\u2579" + new string('\u2580', 99), " Working esc interrupt \u00B7 ctrl+q enqueue"];
        public Task<ConsoleScreen> ReadScreenAsync(int pid, CancellationToken ct = default) => Task.FromResult(new ConsoleScreen(Rows(), null));
        public async Task<InjectionResult> InjectAsync(int pid, KeyPlan plan, CancellationToken ct = default)
        {
            BeforeKeys?.Invoke();
            if (plan.Proceed?.Invoke() is { } stop) { return new(InjectionStatus.NothingTyped, stop); }
            if (plan.ProceedAsync is { } fence && await fence() is { } stopped) { return new(InjectionStatus.NothingTyped, stopped); }
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
                if (action == "q") { AfterQueue?.Invoke(); }
            }
            return new(InjectionStatus.Typed, "ok");
        }
    }
}
