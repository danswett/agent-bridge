using AgentBridge.Host.Delivery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DeliveryQuietTests
{
    // Real time is only ever a failure bound: a starved CI pool must not decide the result.
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Planned_stop_waits_through_confirmation_and_resends_get_the_original_outcome()
    {
        var gate = new DeliveryGate();
        var memory = new OutcomeMemory<int>(TimeProvider.System, TimeSpan.FromMinutes(15), gate, () => -1);
        var key = Guid.NewGuid();
        var typed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = memory.Once(key, async () =>
        {
            await gate.WaitToTypeAsync();
            gate.DoneTyping();
            typed.SetResult();
            return await confirm.Task;
        }, _ => { });
        await typed.Task.WaitAsync(Bound);
        var stop = gate.PrepareStopAsync(Bound);
        Assert.False(stop.IsCompleted);
        var repeat = memory.Once(key, () => throw new Exception("must not type twice"), _ => { });
        Assert.False(repeat.IsCompleted);
        Assert.Equal(-1, await memory.Once(Guid.NewGuid(), () => Task.FromResult(9), _ => { }));
        confirm.SetResult(7);
        Assert.Equal(7, await first.WaitAsync(Bound));
        using var prepared = await stop.WaitAsync(Bound);
        Assert.NotNull(prepared);
        Assert.Equal(7, await repeat.WaitAsync(Bound));
        Assert.Equal(7, await memory.Once(key, () => throw new Exception("must not type twice"), _ => { }));
        Assert.Null(gate.TryEnterDelivery());
    }

    [Fact]
    public async Task A_recovery_stop_is_granted_after_a_journal_uncertainty_once_nothing_is_in_flight_and_the_failed_worker_is_proven_gone()
    {
        var gate = new DeliveryGate();
        var delivery = gate.TryEnterDelivery();
        Assert.NotNull(delivery);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injector = new GateTrackedInjector(new FailingInjector(new InjectorWorkerException(new IOException("pipe broke"), exited.Task)), gate);
        await Assert.ThrowsAsync<InjectorWorkerException>(() => injector.InjectAsync(100, null!));
        var stop = gate.PrepareStopAsync(Bound);
        Assert.False(stop.IsCompleted);
        delivery.Dispose();
        Assert.False(stop.IsCompleted);
        exited.SetResult();
        using var lease = await stop.WaitAsync(Bound);
        Assert.NotNull(lease);
        Assert.Null(gate.TryEnterDelivery());
    }

    [Fact]
    public async Task A_stop_is_refused_while_a_failed_injector_worker_is_still_running_and_granted_once_it_exits()
    {
        var gate = new DeliveryGate();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injector = new GateTrackedInjector(new FailingInjector(new InjectorWorkerException(new IOException("pipe broke"), exited.Task)), gate);
        await Assert.ThrowsAsync<InjectorWorkerException>(() => injector.InjectAsync(100, null!));

        Assert.Null(await gate.PrepareStopAsync(TimeSpan.FromMilliseconds(50)));
        // The refusal released the fence: deliveries go on while the host keeps running.
        using (var next = gate.TryEnterDelivery()) { Assert.NotNull(next); }
        exited.SetResult();
        using var lease = await gate.PrepareStopAsync(Bound);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task A_stop_is_refused_after_an_injector_failure_that_cannot_prove_its_worker_ended()
    {
        var gate = new DeliveryGate();
        var injector = new GateTrackedInjector(new FailingInjector(new InvalidOperationException("worker termination could not be confirmed")), gate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => injector.InjectAsync(100, null!));
        Assert.Null(await gate.PrepareStopAsync(TimeSpan.FromMilliseconds(50)));

        var other = new DeliveryGate();
        var failed = new GateTrackedInjector(new FailingInjector(new InjectorWorkerException(new IOException("pipe broke"),
            Task.FromException(new InvalidOperationException("no handle")))), other);
        await Assert.ThrowsAsync<InjectorWorkerException>(() => failed.InjectAsync(100, null!));
        Assert.Null(await other.PrepareStopAsync(Bound));
    }

    [Fact]
    public async Task A_request_refused_by_a_stop_that_is_then_abandoned_is_delivered_when_sent_again()
    {
        var gate = new DeliveryGate();
        var receipts = new Dictionary<Guid, int>();
        var memory = new OutcomeMemory<int>(TimeProvider.System, TimeSpan.FromMinutes(15), gate, () => -1,
            key => receipts.TryGetValue(key, out var known) ? (true, known) : (false, 0),
            (key, result, _) => receipts[key] = result);
        var key = Guid.NewGuid();
        var stop = await gate.PrepareStopAsync(Bound);
        Assert.NotNull(stop);
        Assert.Equal(-1, await memory.Once(key, () => Task.FromResult(8), _ => { }));
        Assert.Empty(receipts);

        stop.Dispose();
        Assert.Equal(8, await memory.Once(key, () => Task.FromResult(8), _ => { }).WaitAsync(Bound));
        Assert.Equal(8, receipts[key]);
    }

    sealed class FailingInjector(Exception failure) : IConsoleInjector
    {
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default) => Task.FromException<InjectionResult>(failure);
    }

    [Fact]
    public async Task A_stop_after_a_journal_uncertainty_is_still_refused_while_typing()
    {
        var gate = new DeliveryGate();
        await gate.WaitToTypeAsync();
        gate.MarkJournalUncertain();
        Assert.Null(await gate.PrepareStopAsync(TimeSpan.FromMilliseconds(50)));
        gate.DoneTyping();
    }

    [Fact]
    public async Task A_stop_that_times_out_leaves_the_host_and_delivery_running()
    {
        var gate = new DeliveryGate();
        using var delivery = gate.TryEnterDelivery();
        Assert.NotNull(delivery);
        Assert.Null(await gate.PrepareStopAsync(TimeSpan.FromMilliseconds(50)));
        using var next = gate.TryEnterDelivery();
        Assert.NotNull(next);
    }

    [Fact]
    public void A_committed_exclusion_cannot_be_released_even_through_IDisposable()
    {
        var gate = new DeliveryGate(new FakeTimeProvider());
        using var exclusion = gate.TryExclude(TimeSpan.FromSeconds(30), out _)!;
        Assert.True(exclusion.TryBeginCommit(out _));
        Assert.True(exclusion.TryCommit(new DepartureConnection(), 0, false, 0, out _));
        exclusion.Dispose();
        ((IDisposable)exclusion).Dispose();
        Assert.Null(gate.TryEnterDelivery());
    }

    [Fact]
    public async Task An_accepted_request_is_fenced_before_its_background_worker_claims_a_session()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new OutcomeMemory<int>(time, TimeSpan.FromMinutes(15), gate, () => -1);
        var key = Guid.NewGuid();
        var request = memory.Once(key, () => finish.Task, _ => { });
        Assert.Null(gate.TryExclude(TimeSpan.FromSeconds(30), out var why));
        Assert.Equal(DeliveryGate.Blocker.DeliveryInFlight, why);
        finish.SetResult(7);
        Assert.Equal(7, await request);
        using var quiet = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        Assert.NotNull(quiet);
        Assert.Equal(7, await memory.Once(key, () => throw new Exception("must not replay"), _ => { }));
        var next = Guid.NewGuid();
        Assert.Equal(-1, await memory.Once(next, () => Task.FromResult(8), _ => { }));
        quiet.Dispose();
        Assert.Equal(8, await memory.Once(next, () => Task.FromResult(8), _ => { }));
    }

    [Fact]
    public async Task An_accepted_outcome_is_complete_before_its_admission_is_released()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedAtRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new OutcomeMemory<int>(time, TimeSpan.FromMinutes(15), gate, () => -1);
        Task<int>? request = null;
        gate.AdmissionReleased = () => completedAtRelease.TrySetResult(request!.IsCompleted);
        request = memory.Once(Guid.NewGuid(), () => release.Task, _ => { });
        release.SetResult(7);
        Assert.True(await completedAtRelease.Task.WaitAsync(Bound));
        Assert.Equal(7, await request.WaitAsync(Bound));
        using var fence = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        Assert.NotNull(fence);
    }

    [Fact]
    public async Task A_typing_worker_cannot_be_interrupted_by_a_quiet_grant()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var delivery = gate.TryEnterDelivery();
        await gate.WaitToTypeAsync();
        try
        {
            Assert.Null(gate.TryExclude(TimeSpan.FromSeconds(30), out var why));
            Assert.Equal(DeliveryGate.Blocker.Typing, why);
        }
        finally { gate.DoneTyping(); }
    }

    [Fact]
    public void An_expired_provisional_fence_releases_but_its_stale_owner_cannot_release_a_new_one()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var first = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        Assert.NotNull(first);
        Assert.Null(gate.TryEnterDelivery());
        time.Advance(TimeSpan.FromSeconds(30));
        using (var admitted = gate.TryEnterDelivery()) { Assert.NotNull(admitted); }
        using var second = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        first.Dispose();
        Assert.Null(gate.TryEnterDelivery());
        second!.Dispose();
        using var next = gate.TryEnterDelivery();
        Assert.NotNull(next);
    }
}
