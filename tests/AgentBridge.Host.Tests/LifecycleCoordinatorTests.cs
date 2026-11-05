using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class LifecycleCoordinatorTests
{
    static DepartureConditions Conditions(params IDepartureCondition[] registered)
    {
        var conditions = new DepartureConditions();
        foreach (var purpose in Enum.GetValues<DeparturePurpose>()) { conditions.Register(purpose, registered); }
        return conditions;
    }

    [Fact]
    public async Task The_lease_blocks_while_a_delivery_is_in_flight_and_grants_when_quiet()
    {
        var gate = new DeliveryGate();
        var coordinator = new LifecycleCoordinator(gate, Conditions());
        using var delivery = gate.TryEnterDelivery();
        Assert.Equal(QuietBlockedReason.DeliveryInFlight,
            (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Reason);
        delivery!.Dispose();
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Value;
        Assert.NotNull(lease);
        Assert.Null(gate.TryEnterDelivery());
        lease.Dispose();
        using var next = gate.TryEnterDelivery();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task An_always_Working_session_does_not_block_a_quiet_grant()
    {
        using var f = new Fixture();
        const string id = "77777777-7777-4777-8777-777777777777";
        var events = f.CopilotSession(id, 100);
        Fixture.Append(events, Copilot.User("continue", 1), Copilot.TurnStart(2));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = new SessionSource(f.Options(), f.Processes);
        await source.RefreshAsync();
        Assert.Equal(SessionStatus.Working, Assert.Single(source.Snapshot()).Status);
        var coordinator = new LifecycleCoordinator(new DeliveryGate(), Conditions());
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value;
        Assert.NotNull(lease);
        Assert.Equal(SessionStatus.Working, source.Get(id)!.Status);
    }

    [Fact]
    public async Task Waiting_is_bounded_and_returns_the_actual_closed_blocker()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        var coordinator = new LifecycleCoordinator(gate, Conditions(), time: time);
        using var delivery = gate.TryEnterDelivery();
        var attempt = coordinator.TryAcquireQuiet(TimeSpan.FromSeconds(10), DeparturePurpose.RemoteRestart);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(QuietBlockedReason.DeliveryInFlight, (await attempt.WaitAsync(TimeSpan.FromSeconds(2))).Reason);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.TryAcquireQuiet(TimeSpan.FromSeconds(11), DeparturePurpose.RemoteRestart));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.TryAcquireQuiet(TimeSpan.Zero, (DeparturePurpose)int.MaxValue));
        Assert.Equal(["DeliveryInFlight", "Typing", "AnnounceUnacked", "JournalUncertain", "AppRunning",
            "OtherHostRunning", "ChannelChanged", "TransferInFlight", "ConditionFault"], Enum.GetNames<DepartureBlocker>());
    }

    [Fact]
    public async Task An_unacknowledged_announcement_cancels_and_releases_the_lease()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        var transport = new Transport { Send = m => m.Phase == "planned" ? new TaskCompletionSource<bool>().Task : Task.FromResult(true) };
        var coordinator = new LifecycleCoordinator(gate, Conditions(), transport, time);
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        var announcing = coordinator.AnnouncePlanned(lease);
        await transport.PlannedCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(QuietBlockedReason.AnnounceUnacked, (await announcing.WaitAsync(TimeSpan.FromSeconds(2))).Reason);
        Assert.Equal(CancellationOutcome.Sent, await lease.Cancellation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(["planned", "cancelled"], transport.Messages.Select(m => m.Phase));
        Assert.Equal(lease.Transition, transport.Messages[1].Transition);
        Assert.True(transport.Messages[1].Sequence > transport.Messages[0].Sequence);
        Assert.Equal(("update", 90), (transport.Messages[0].Reason, transport.Messages[0].ExpectedReturnSeconds));
        Assert.Null(transport.Messages[1].Reason);
        using var delivery = gate.TryEnterDelivery();
        Assert.NotNull(delivery);
        Assert.Equal(DepartureBlocker.ConditionFault, coordinator.CommitDeparture(lease).Kind);
    }

    [Fact]
    public async Task Only_a_persons_restart_or_stop_can_use_the_unnegotiated_Legacy_path()
    {
        var gate = new DeliveryGate();
        var coordinator = new LifecycleCoordinator(gate, Conditions());
        using var update = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        Assert.Equal(QuietBlockedReason.AnnounceUnavailable, (await coordinator.AnnouncePlanned(update)).Reason);
        using var restart = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Value!;
        Assert.True((await coordinator.AnnouncePlanned(restart)).Value!.Legacy);
        var guard = coordinator.CommitDeparture(restart).Guard;
        Assert.NotNull(guard);
        restart.Dispose();
        guard.Dispose();
        Assert.Null(gate.TryEnterDelivery());
    }

    [Fact]
    public async Task A_forced_recovery_is_refused_with_JournalUncertain_until_the_journal_exists()
    {
        var coordinator = new LifecycleCoordinator(new DeliveryGate(), Conditions());
        Assert.Equal(QuietBlockedReason.JournalUncertain,
            (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.WatchdogRecovery)).Reason);
        foreach (var mode in new[] { DepartureMode.ForcedRecovery, DepartureMode.ExternalRestart, DepartureMode.QueuedInputHandover })
        {
            Assert.Equal(QuietBlockedReason.JournalUncertain,
                (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart, mode: mode)).Reason);
        }
    }

    [Fact]
    public async Task After_commit_a_new_delivery_is_refused_until_the_process_exits_even_if_the_guard_is_disposed()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        var condition = new Condition(DepartureBlocker.AppRunning, () =>
        {
            Assert.Null(gate.TryEnterDelivery());
            return false;
        });
        var registration = Conditions(condition);
        var coordinator = new LifecycleCoordinator(gate, registration, new Transport(), time);
        Assert.Throws<InvalidOperationException>(() => registration.Register(DeparturePurpose.Update, condition));
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        Assert.True((await coordinator.AnnouncePlanned(lease)).Succeeded);
        var result = coordinator.CommitDeparture(lease);
        Assert.True(result.Committed);
        Assert.Equal(1, condition.Calls);
        result.Guard!.Dispose();
        time.Advance(TimeSpan.FromSeconds(31));
        lease.Dispose();
        Assert.Null(gate.TryEnterDelivery());
        Assert.False(coordinator.CommitDeparture(lease).Committed);
        Assert.Equal(1, condition.Calls);
        Assert.Null(gate.TryEnterDelivery());
    }

    [Fact]
    public async Task A_blocking_condition_aborts_releases_and_cancels_but_still_checks_every_condition_once()
    {
        var gate = new DeliveryGate();
        var transport = new Transport();
        var first = new Condition(DepartureBlocker.AppRunning, () => true);
        var second = new Condition(DepartureBlocker.OtherHostRunning, () => false);
        var coordinator = new LifecycleCoordinator(gate, Conditions(first, second), transport);
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        await coordinator.AnnouncePlanned(lease);
        Assert.Equal(DepartureBlocker.AppRunning, coordinator.CommitDeparture(lease).Kind);
        Assert.Equal((1, 1), (first.Calls, second.Calls));
        Assert.Equal(CancellationOutcome.Sent, await lease.Cancellation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("cancelled", transport.Messages[^1].Phase);
        using var admitted = gate.TryEnterDelivery();
        Assert.NotNull(admitted);
    }

    [Fact]
    public async Task A_throwing_condition_aborts_as_ConditionFault_and_releases()
    {
        var gate = new DeliveryGate();
        var condition = new Condition(DepartureBlocker.ChannelChanged, () => throw new IOException("not exported"));
        var coordinator = new LifecycleCoordinator(gate, Conditions(condition));
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Value!;
        await coordinator.AnnouncePlanned(lease);
        Assert.Equal(DepartureBlocker.ConditionFault, coordinator.CommitDeparture(lease).Kind);
        Assert.Equal(1, condition.Calls);
        using var admitted = gate.TryEnterDelivery();
        Assert.NotNull(admitted);
    }

    [Fact]
    public async Task A_reentrant_condition_is_refused_as_ConditionFault_without_deadlocking()
    {
        var gate = new DeliveryGate();
        LifecycleCoordinator coordinator = null!;
        QuietLease lease = null!;
        var condition = new Condition(DepartureBlocker.TransferInFlight, () =>
        {
            Assert.Equal(DepartureBlocker.ConditionFault, coordinator.CommitDeparture(lease).Kind);
            Assert.Equal(QuietBlockedReason.ConditionFault,
                coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart).GetAwaiter().GetResult().Reason);
            return false;
        });
        coordinator = new LifecycleCoordinator(gate, Conditions(condition));
        using var held = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Value!;
        lease = held;
        await coordinator.AnnouncePlanned(lease);
        Assert.Equal(DepartureBlocker.ConditionFault, coordinator.CommitDeparture(lease).Kind);
        Assert.Equal(1, condition.Calls);
        using var admitted = gate.TryEnterDelivery();
        Assert.NotNull(admitted);
    }

    [Fact]
    public void An_unknown_condition_kind_is_refused_at_composition()
    {
        var conditions = new DepartureConditions();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            conditions.Register(DeparturePurpose.Update, new Condition((DepartureBlocker)999, () => false)));
    }

    [Fact]
    public async Task A_changed_announcement_generation_or_missing_registration_aborts()
    {
        var gate = new DeliveryGate();
        var transport = new Transport();
        var coordinator = new LifecycleCoordinator(gate, Conditions(), transport);
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        await coordinator.AnnouncePlanned(lease);
        transport.Generation++;
        Assert.Equal(DepartureBlocker.AnnounceUnacked, coordinator.CommitDeparture(lease).Kind);
        using (var admitted = gate.TryEnterDelivery()) { Assert.NotNull(admitted); }
        var unregistered = new LifecycleCoordinator(gate, new DepartureConditions());
        using var second = (await unregistered.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Value!;
        await unregistered.AnnouncePlanned(second);
        Assert.Equal(DepartureBlocker.ConditionFault, unregistered.CommitDeparture(second).Kind);
    }

    [Fact]
    public async Task A_hung_relay_on_cancel_does_not_hold_the_fence_or_block_the_next_TryAcquireQuiet()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        var transport = new Transport { Send = m => m.Phase == "cancelled" ? new TaskCompletionSource<bool>().Task : Task.FromResult(true) };
        var coordinator = new LifecycleCoordinator(gate, Conditions(new Condition(DepartureBlocker.AppRunning, () => true)), transport, time);
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        await coordinator.AnnouncePlanned(lease);
        Assert.Equal(DepartureBlocker.AppRunning, coordinator.CommitDeparture(lease).Kind);
        await transport.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var next = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value;
        Assert.NotNull(next);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(CancellationOutcome.CancelUnsent, await lease.Cancellation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(CancellationOutcome.CancelUnsent, lease.CancellationStatus);
    }

    [Fact]
    public async Task An_unsent_cancel_is_reported_and_an_expired_announced_lease_cancels_outside_the_fence()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        LifecycleCoordinator coordinator = null!;
        var transport = new Transport
        {
            Send = m =>
            {
                if (m.Phase == "cancelled")
                {
                    // This would block or be refused if Commit/expiry called transport while
                    // retaining either the delivery fence or its coordinator's condition lock.
                    using var next = coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update).GetAwaiter().GetResult().Value;
                    Assert.NotNull(next);
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            },
        };
        coordinator = new LifecycleCoordinator(gate, Conditions(), transport, time);
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        await coordinator.AnnouncePlanned(lease);
        time.Advance(TimeSpan.FromSeconds(30));
        await transport.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CancellationOutcome.CancelUnsent, await lease.Cancellation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("cancelled", transport.Messages[^1].Phase);
    }

    [Fact]
    public async Task A_synchronously_blocking_announce_and_hung_cancel_cannot_extend_the_five_second_announcement_bound()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var release = new ManualResetEventSlim();
        var sendEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport
        {
            Send = m =>
            {
                if (m.Phase == "planned")
                {
                    try { release.Wait(TimeSpan.FromSeconds(3)); return Task.FromResult(true); }
                    finally { sendEnded.TrySetResult(); }
                }
                return new TaskCompletionSource<bool>().Task;
            },
        };
        var coordinator = new LifecycleCoordinator(gate, Conditions(), transport, time);
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        try
        {
            var announcing = coordinator.AnnouncePlanned(lease);
            await transport.PlannedCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(QuietBlockedReason.AnnounceUnacked, (await announcing.WaitAsync(TimeSpan.FromSeconds(2))).Reason);
            using var next = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value;
            Assert.NotNull(next);
            await transport.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(CancellationOutcome.CancelUnsent, await lease.Cancellation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(DepartureBlocker.ConditionFault, coordinator.CommitDeparture(lease).Kind);
        }
        finally
        {
            release.Set();
            await sendEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task An_acceptance_returned_after_its_deadline_never_produces_a_usable_receipt()
    {
        var time = new FakeTimeProvider();
        var transport = new Transport
        {
            Send = m =>
            {
                if (m.Phase == "planned") { time.Advance(TimeSpan.FromSeconds(6)); }
                return Task.FromResult(true);
            },
        };
        var coordinator = new LifecycleCoordinator(new DeliveryGate(time), Conditions(), transport, time);
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.Update)).Value!;
        Assert.Equal(QuietBlockedReason.AnnounceUnacked, (await coordinator.AnnouncePlanned(lease)).Reason);
        Assert.False(coordinator.CommitDeparture(lease).Committed);
        Assert.Equal(CancellationOutcome.Sent, await lease.Cancellation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    sealed class Condition(DepartureBlocker kind, Func<bool> blocking) : IDepartureCondition
    {
        public DepartureBlocker Kind => kind;
        public int Calls { get; private set; }
        public bool IsBlocking() { Calls++; return blocking(); }
    }

    sealed class Transport : IPlannedStateTransport
    {
        public bool Negotiated => true;
        public long Generation { get; set; } = 1;
        public List<PlannedStateMessage> Messages { get; } = [];
        public TaskCompletionSource CancelCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PlannedCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<PlannedStateMessage, Task<bool>> Send { get; init; } = _ => Task.FromResult(true);
        public Task<bool> SendAsync(PlannedStateMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            if (message.Phase == "cancelled") { CancelCalled.TrySetResult(); }
            else { PlannedCalled.TrySetResult(); }
            return Send(message);
        }
    }
}
