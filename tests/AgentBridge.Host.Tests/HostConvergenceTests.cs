using System.Text;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #819's consumer against #805's real LifecycleCoordinator and DeliveryGate (#874). Only owners'
// public interfaces are test doubles: the relay transport, halts source, release and apply ports and
// the three owners' condition snapshots, none of which exists at runtime yet.
public sealed class HostConvergenceTests
{
    const string Source = "github@danswett/agent-bridge";
    const string Installed = "0.1.2151";
    const string Newer = "0.1.2349";
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_quiet_host_announces_commits_and_applies_once_under_a_guard_held_until_exit()
    {
        var h = new Harness();
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Applying, "none", Newer), (decision.Outcome, decision.Word, decision.TargetVersion));
        Assert.Equal(["planned"], h.Transport.Phases);
        var planned = Assert.Single(h.Transport.Messages);
        Assert.Equal(("update", 90), (planned.Reason, planned.ExpectedReturnSeconds));
        Assert.Equal(planned.Transition, Assert.Single(h.Apply.Guards).Transition);
        Assert.Null(h.Gate.TryEnterDelivery());

        var again = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Blocked, "departure-committed"), (again.Outcome, again.Word));
        Assert.Single(h.Apply.Guards);
        Assert.Single(h.Transport.Messages);
        Assert.Same(again, h.Convergence.Last);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_apply_keeps_the_guard_and_never_applies_again(bool cancelled)
    {
        var h = new Harness();
        h.Apply.Throw = cancelled ? new OperationCanceledException("C:\\secret\\path token=abc") : new IOException("C:\\secret\\path token=abc");
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Blocked, "apply-failed"), (decision.Outcome, decision.Word));
        Assert.DoesNotContain("secret", decision.ToString(), StringComparison.Ordinal);
        Assert.Null(h.Gate.TryEnterDelivery());
        Assert.Equal(["planned"], h.Transport.Phases);

        Assert.Equal("departure-committed", (await h.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        Assert.Single(h.Apply.Guards);
        Assert.Null(h.Gate.TryEnterDelivery());
    }

    [Fact]
    public async Task Each_missing_seam_is_its_own_block_and_never_takes_a_lease()
    {
        Assert.Equal("not-installed", (await new Harness { InstalledVersion = null }.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        Assert.Equal("channel-unavailable", (await new Harness { ChannelSource = null }.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        Assert.Equal("eligibility-unavailable", (await new Harness { NoRelease = true }.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        Assert.Equal("eligibility-unavailable", (await new Harness { NoEligibility = true }.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        Assert.Equal("apply-unavailable", (await new Harness { NoApply = true }.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        Assert.Equal("lifecycle-unavailable", (await new Harness { NoLifecycle = true }.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        var throwing = new Harness();
        throwing.Channel.Throw = true;
        Assert.Equal("channel-unavailable", (await throwing.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        Assert.Empty(throwing.Transport.Messages);
        using var open = throwing.Gate.TryEnterDelivery();
        Assert.NotNull(open);
    }

    [Fact]
    public async Task An_incomplete_condition_set_fails_closed_and_is_refused_at_registration()
    {
        var app = new Flag(DepartureBlocker.AppRunning);
        var other = new Flag(DepartureBlocker.OtherHostRunning);
        var transfer = new Flag(DepartureBlocker.TransferInFlight);
        var channel = new ChannelChangedCondition();
        IDepartureCondition[][] incomplete =
        [
            [],
            [app, other, channel],
            [app, other, channel, transfer, new Flag(DepartureBlocker.AppRunning)],
            [app, app, channel, transfer],
            [app, other, new Flag(DepartureBlocker.ChannelChanged), transfer],
        ];
        foreach (var set in incomplete)
        {
            var h = new Harness { Conditions = set, Register = false };
            var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
            Assert.Equal((ConvergenceOutcome.Blocked, "conditions-incomplete"), (decision.Outcome, decision.Word));
            Assert.Empty(h.Transport.Messages);
            Assert.Throws<ArgumentException>(() => HostConvergence.RegisterUpdateConditions(new DepartureConditions(), set));
        }
        var none = new Harness { Conditions = null, Register = false };
        Assert.Equal("conditions-incomplete", (await none.Convergence.RunOnceAsync(CancellationToken.None)).Word);
    }

    [Fact]
    public async Task Without_the_composition_roots_registration_the_commit_faults_cancels_and_reopens_delivery()
    {
        var h = new Harness { Register = false };
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Blocked, "condition-fault", "sent"), (decision.Outcome, decision.Word, decision.CancelWord));
        Assert.Equal(["planned", "cancelled"], h.Transport.Phases);
        Assert.Empty(h.Apply.Guards);
        using var open = h.Gate.TryEnterDelivery();
        Assert.NotNull(open);
    }

    [Theory]
    [InlineData(DepartureBlocker.AppRunning, "app-running")]
    [InlineData(DepartureBlocker.OtherHostRunning, "other-host-running")]
    [InlineData(DepartureBlocker.TransferInFlight, "transfer-in-flight")]
    public async Task A_condition_already_blocking_is_reported_before_any_lease_or_plan(DepartureBlocker kind, string word)
    {
        var h = new Harness();
        h.Flag(kind).Blocking = true;
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Blocked, word, (string?)null), (decision.Outcome, decision.Word, decision.CancelWord));
        Assert.Empty(h.Transport.Messages);
        Assert.Empty(h.Apply.Guards);
        using var open = h.Gate.TryEnterDelivery();
        Assert.NotNull(open);
    }

    [Fact]
    public async Task A_relay_reconnect_while_announcing_is_announce_unacked_with_the_cancel_unsent()
    {
        var h = new Harness();
        h.Transport.OnPlanned = () => h.Transport.Connection.Publish(2, true);
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        // #805 never sends the cancel on a connection other than the one that heard the plan.
        Assert.Equal((ConvergenceOutcome.Blocked, "announce-unacked", "cancel-unsent"), (decision.Outcome, decision.Word, decision.CancelWord));
        Assert.Equal(["planned"], h.Transport.Phases);
        Assert.Empty(h.Apply.Guards);
        using var open = h.Gate.TryEnterDelivery();
        Assert.NotNull(open);
    }

    [Theory]
    [InlineData(DepartureBlocker.AppRunning, "app-running")]
    [InlineData(DepartureBlocker.OtherHostRunning, "other-host-running")]
    [InlineData(DepartureBlocker.TransferInFlight, "transfer-in-flight")]
    [InlineData(DepartureBlocker.ChannelChanged, "channel-changed")]
    public async Task A_condition_that_turns_blocking_under_the_fence_aborts_cancels_and_never_applies(DepartureBlocker kind, string word)
    {
        var h = new Harness();
        h.Transport.OnPlanned = () =>
        {
            if (kind == DepartureBlocker.ChannelChanged) { h.Channel.Value = "github@someone/else"; }
            else { h.Flag(kind).Blocking = true; }
        };
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Blocked, word, "sent"), (decision.Outcome, decision.Word, decision.CancelWord));
        Assert.Equal(["planned", "cancelled"], h.Transport.Phases);
        Assert.Empty(h.Apply.Guards);
        using var open = h.Gate.TryEnterDelivery();
        Assert.NotNull(open);
    }

    [Fact]
    public async Task A_throwing_or_reentrant_condition_is_a_fault_that_aborts()
    {
        var early = new Harness();
        early.Flag(DepartureBlocker.AppRunning).ThrowOn = 1;
        Assert.Equal("condition-fault", (await early.Convergence.RunOnceAsync(CancellationToken.None)).Word);
        Assert.Empty(early.Transport.Messages);

        var underFence = new Harness();
        underFence.Flag(DepartureBlocker.TransferInFlight).ThrowOn = 2;
        var thrown = await underFence.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal(("condition-fault", "sent"), (thrown.Word, thrown.CancelWord));
        Assert.Empty(underFence.Apply.Guards);

        var reentrant = new Harness();
        var flag = reentrant.Flag(DepartureBlocker.OtherHostRunning);
        flag.OnCall = n => { if (n == 2) { _ = reentrant.Lifecycle.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Sleep); } };
        var reentered = await reentrant.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal(("condition-fault", "sent"), (reentered.Word, reentered.CancelWord));
        Assert.Empty(reentrant.Apply.Guards);
        using var open = reentrant.Gate.TryEnterDelivery();
        Assert.NotNull(open);
    }

    [Fact]
    public async Task Quiet_is_805s_answer_a_delivery_waits_and_an_uncertain_journal_blocks()
    {
        var busy = new Harness { MaxWait = TimeSpan.Zero };
        using (busy.Gate.TryEnterDelivery())
        {
            var decision = await busy.Convergence.RunOnceAsync(CancellationToken.None);
            Assert.Equal((ConvergenceOutcome.Waiting, "delivery-in-flight"), (decision.Outcome, decision.Word));
        }
        Assert.Empty(busy.Transport.Messages);

        var uncertain = new Harness { MaxWait = TimeSpan.Zero };
        uncertain.Gate.MarkJournalUncertain();
        var blocked = await uncertain.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Blocked, "journal-uncertain"), (blocked.Outcome, blocked.Word));
        Assert.Empty(uncertain.Apply.Guards);
    }

    [Fact]
    public async Task An_unnegotiated_relay_is_announce_unavailable_with_nothing_to_cancel()
    {
        var h = new Harness { Unnegotiated = true };
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Blocked, "announce-unavailable", "not-required"), (decision.Outcome, decision.Word, decision.CancelWord));
        Assert.Empty(h.Apply.Guards);
        using var open = h.Gate.TryEnterDelivery();
        Assert.NotNull(open);
    }

    [Fact]
    public async Task An_unacknowledged_plan_is_cancelled_by_805_and_reported()
    {
        var time = new FakeTimeProvider();
        var h = new Harness { Time = time };
        h.Transport.Send = m => m.Phase == "planned" ? new TaskCompletionSource<bool>().Task : Task.FromResult(true);
        var pass = h.Convergence.RunOnceAsync(CancellationToken.None);
        await h.Transport.PlannedCalled.Task.WaitAsync(Wait);
        time.Advance(LifecycleCoordinator.AnnounceTimeout);
        var decision = await pass.WaitAsync(Wait);
        Assert.Equal((ConvergenceOutcome.Blocked, "announce-unacked", "sent"), (decision.Outcome, decision.Word, decision.CancelWord));
        Assert.Equal(["planned", "cancelled"], h.Transport.Phases);
        Assert.Empty(h.Apply.Guards);
    }

    [Fact]
    public async Task A_hung_cancel_is_reported_unsent_and_never_holds_the_next_pass()
    {
        var time = new FakeTimeProvider();
        var h = new Harness { Time = time };
        h.Transport.Send = _ => new TaskCompletionSource<bool>().Task;
        var pass = h.Convergence.RunOnceAsync(CancellationToken.None);
        await h.Transport.PlannedCalled.Task.WaitAsync(Wait);
        time.Advance(LifecycleCoordinator.AnnounceTimeout);
        await h.Transport.CancelCalled.Task.WaitAsync(Wait);
        time.Advance(LifecycleCoordinator.AnnounceTimeout);
        var decision = await pass.WaitAsync(Wait);
        Assert.Equal(("announce-unacked", "cancel-unsent"), (decision.Word, decision.CancelWord));
        using (var open = h.Gate.TryEnterDelivery()) { Assert.NotNull(open); }

        h.Transport.Send = _ => Task.FromResult(true);
        Assert.NotEqual("pass-running", (await h.Convergence.RunOnceAsync(CancellationToken.None).WaitAsync(Wait)).Word);
    }

    [Fact]
    public async Task A_cancelled_caller_is_cancelled_and_only_its_own_cancellation_counts()
    {
        var h = new Harness();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var decision = await h.Convergence.RunOnceAsync(cancelled.Token);
        Assert.Equal((ConvergenceOutcome.Blocked, "cancelled"), (decision.Outcome, decision.Word));
        Assert.Same(decision, h.Convergence.Last);
        Assert.Equal(0, h.Release.Calls);

        var unrelated = new Harness();
        unrelated.Release.Throw = new OperationCanceledException();
        Assert.Equal("eligibility-unavailable", (await unrelated.Convergence.RunOnceAsync(CancellationToken.None)).Word);

        var afterAck = new Harness();
        using var later = new CancellationTokenSource();
        afterAck.Transport.OnPlanned = later.Cancel;
        var stopped = await afterAck.Convergence.RunOnceAsync(later.Token);
        Assert.Equal(("cancelled", "sent"), (stopped.Word, stopped.CancelWord));
        Assert.Equal(["planned", "cancelled"], afterAck.Transport.Phases);
        Assert.Empty(afterAck.Apply.Guards);
    }

    [Fact]
    public async Task A_caller_cancelling_as_the_fence_is_granted_frees_it_at_once()
    {
        // The caller's token is cancelled while #805 grants the fence (as it creates the lease's
        // timer), so AnnouncePlanned refuses before sending; the fence must be free at once.
        using var caller = new CancellationTokenSource();
        var time = new CancelAtGrant(caller);
        var h = new Harness { Time = time };
        var pass = h.Convergence.RunOnceAsync(caller.Token);
        var settled = await Task.WhenAny(pass, Task.Delay(Wait)) == pass;
        Assert.True(time.Granted);
        Assert.True(settled, "the refused announcement kept the fence, waiting on a cancel nobody dispatched");
        var decision = await pass;
        Assert.Equal((ConvergenceOutcome.Blocked, "cancelled", "not-required"), (decision.Outcome, decision.Word, decision.CancelWord));
        Assert.Same(decision, h.Convergence.Last);
        Assert.Empty(h.Transport.Messages);
        Assert.Empty(h.Apply.Guards);
        using var open = h.Gate.TryEnterDelivery();
        Assert.NotNull(open);
    }

    sealed class CancelAtGrant(CancellationTokenSource caller) : FakeTimeProvider
    {
        public bool Granted { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!Granted && dueTime == LifecycleCoordinator.LeaseLifetime)
            {
                Granted = true;
                caller.Cancel();
            }
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Fact]
    public async Task An_overlapping_pass_says_so_while_the_first_applies()
    {
        var h = new Harness();
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Apply.Pending = applying.Task;
        var first = h.Convergence.RunOnceAsync(CancellationToken.None);
        await h.Apply.Called.Task.WaitAsync(Wait);
        var overlapping = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((ConvergenceOutcome.Waiting, "pass-running"), (overlapping.Outcome, overlapping.Word));
        applying.SetResult();
        Assert.Equal("none", (await first.WaitAsync(Wait)).Word);
    }

    [Theory]
    [InlineData("{\"v\":1,\"halted\":[\"0.1.2349\"]}", ConvergenceOutcome.Blocked, "halted")]
    [InlineData("{\"v\":1}", ConvergenceOutcome.Blocked, "halt-list-unavailable")]
    [InlineData(null, ConvergenceOutcome.Blocked, "halt-list-unavailable")]
    [InlineData("{\"v\":1,\"halted\":[]}", ConvergenceOutcome.Applying, "none")]
    public async Task Halts_are_580s_answer_and_an_unreadable_list_fails_closed(string? halts, ConvergenceOutcome outcome, string word)
    {
        var h = new Harness();
        h.Halts.Body = halts;
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((outcome, word), (decision.Outcome, decision.Word));
        Assert.Equal(outcome == ConvergenceOutcome.Applying ? 1 : 0, h.Apply.Guards.Count);
    }

    [Theory]
    [InlineData("0.1.2151", ConvergenceOutcome.Current, "none")]
    [InlineData("0.1.2110", ConvergenceOutcome.Current, "none")]
    [InlineData("0.1.2349-beta", ConvergenceOutcome.Blocked, "version-unknown")]
    [InlineData("1.0", ConvergenceOutcome.Blocked, "version-unknown")]
    public async Task No_downgrade_is_581s_comparison_alone(string target, ConvergenceOutcome outcome, string word)
    {
        var h = new Harness();
        h.Release.Answer = new(CandidateKind.Target, new(target, Source, true, true));
        var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
        Assert.Equal((outcome, word), (decision.Outcome, decision.Word));
        Assert.Empty(h.Transport.Messages);
    }

    [Fact]
    public async Task The_candidate_must_be_this_channels_architecture_and_verified()
    {
        async Task<string> Word(ReleaseCandidate answer)
        {
            var h = new Harness();
            h.Release.Answer = answer;
            var decision = await h.Convergence.RunOnceAsync(CancellationToken.None);
            Assert.Empty(h.Transport.Messages);
            return decision.Word;
        }
        Assert.Equal("channel-changed", await Word(new(CandidateKind.Target, new(Newer, "github@someone/else", true, true))));
        Assert.Equal("wrong-architecture", await Word(new(CandidateKind.Target, new(Newer, Source, false, true))));
        Assert.Equal("not-verified", await Word(new(CandidateKind.Target, new(Newer, Source, true, false))));
        Assert.Equal("none", await Word(new(CandidateKind.None)));
        Assert.Equal("eligibility-unavailable", await Word(new(CandidateKind.Unknown)));
        Assert.Equal("eligibility-unavailable", await Word(new(CandidateKind.Target)));
        var throwing = new Harness();
        throwing.Release.Throw = new HttpRequestException("https://secret");
        Assert.Equal("eligibility-unavailable", (await throwing.Convergence.RunOnceAsync(CancellationToken.None)).Word);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task An_invalid_quiet_bound_is_refused_before_anything_is_asked(int seconds)
    {
        var h = new Harness { MaxWait = TimeSpan.FromSeconds(seconds) };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Convergence.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, h.Release.Calls);
    }

    sealed class Harness
    {
        HostConvergence? convergence;
        public string? InstalledVersion { get; init; } = Installed;
        public string? ChannelSource { init => Channel.Value = value; }
        public bool NoRelease { get; init; }
        public bool NoEligibility { get; init; }
        public bool NoApply { get; init; }
        public bool NoLifecycle { get; init; }
        public bool Register { get; init; } = true;
        public bool Unnegotiated { get; init; }
        public TimeSpan? MaxWait { get; init; }
        public FakeTimeProvider? Time { get; init; }
        public IDepartureCondition[]? Conditions { get; set; }
        public TestChannel Channel { get; } = new();
        public TestRelease Release { get; } = new();
        public TestHalts Halts { get; } = new();
        public TestApply Apply { get; } = new();
        public Transport Transport { get; } = new();
        public DeliveryGate Gate { get { _ = Convergence; return gate!; } }
        public LifecycleCoordinator Lifecycle { get { _ = Convergence; return lifecycle!; } }
        DeliveryGate? gate;
        LifecycleCoordinator? lifecycle;

        public Harness() => Conditions =
            [new Flag(DepartureBlocker.AppRunning), new Flag(DepartureBlocker.OtherHostRunning), new ChannelChangedCondition(), new Flag(DepartureBlocker.TransferInFlight)];

        public Flag Flag(DepartureBlocker kind) => Conditions!.OfType<Flag>().Single(f => f.Kind == kind);

        public HostConvergence Convergence => convergence ??= Build();

        HostConvergence Build()
        {
            gate = new DeliveryGate(Time);
            var registry = new DepartureConditions();
            if (Register) { HostConvergence.RegisterUpdateConditions(registry, Conditions!); }
            lifecycle = new LifecycleCoordinator(gate, registry, Unnegotiated ? null : Transport, Time);
            return new HostConvergence(Channel, NoRelease ? null : Release, NoEligibility ? null : new ReleaseEligibility(Halts),
                NoApply ? null : Apply, Conditions, NoLifecycle ? null : lifecycle, () => InstalledVersion, MaxWait, Time);
        }
    }

    sealed class Flag(DepartureBlocker kind) : IDepartureCondition
    {
        int calls;
        public DepartureBlocker Kind => kind;
        public volatile bool Blocking;
        public int ThrowOn { get; set; }
        public Action<int>? OnCall { get; set; }
        public bool IsBlocking()
        {
            var n = Interlocked.Increment(ref calls);
            OnCall?.Invoke(n);
            if (n == ThrowOn) { throw new InvalidOperationException("C:\\secret"); }
            return Blocking;
        }
    }

    sealed class TestChannel : IConvergenceChannel
    {
        public string? Value { get; set; } = HostConvergenceTests.Source;
        public bool Throw { get; set; }
        public string? Source() => Throw ? throw new IOException("C:\\secret") : Value;
    }

    sealed class TestRelease : IConvergenceRelease
    {
        public int Calls;
        public ReleaseCandidate Answer { get; set; } = new(CandidateKind.Target, new(Newer, HostConvergenceTests.Source, true, true));
        public Exception? Throw { get; set; }
        public Task<ReleaseCandidate> CandidateAsync(string channelSource, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Throw is { } e ? Task.FromException<ReleaseCandidate>(e) : Task.FromResult(Answer);
        }
    }

    sealed class TestHalts : IHaltListSource
    {
        public string? Body { get; set; } = "{\"v\":1,\"halted\":[]}";
        public Task<byte[]> ReadAsync(CancellationToken cancellationToken) =>
            Body is { } body ? Task.FromResult(Encoding.UTF8.GetBytes(body)) : Task.FromException<byte[]>(new IOException("C:\\secret"));
    }

    sealed class TestApply : IHostUnattendedApply
    {
        public List<DepartureGuard> Guards { get; } = [];
        public Exception? Throw { get; set; }
        public Task Pending { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ApplyUnattendedAsync(EligibleRelease release, DepartureGuard guard, CancellationToken cancellationToken)
        {
            lock (Guards) { Guards.Add(guard); }
            Called.TrySetResult();
            await Pending;
            if (Throw is { } e) { throw e; }
        }
    }

    sealed class Transport : IPlannedStateTransport
    {
        readonly List<PlannedStateMessage> messages = [];
        public Transport() => Connection.Publish(1, true);
        public DepartureConnection Connection { get; } = new();
        public bool Negotiated => Connection.Snapshot.Negotiated;
        public long Generation => Connection.Snapshot.Generation;
        public IReadOnlyList<PlannedStateMessage> Messages { get { lock (messages) { return [.. messages]; } } }
        public string[] Phases => [.. Messages.Select(m => m.Phase)];
        public TaskCompletionSource CancelCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PlannedCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnPlanned { get; set; }
        public Func<PlannedStateMessage, Task<bool>> Send { get; set; } = _ => Task.FromResult(true);
        public Task<bool> SendAsync(PlannedStateMessage message, CancellationToken cancellationToken)
        {
            lock (messages) { messages.Add(message); }
            if (message.Phase == "cancelled") { CancelCalled.TrySetResult(); }
            else { OnPlanned?.Invoke(); PlannedCalled.TrySetResult(); }
            return Send(message);
        }
    }
}
