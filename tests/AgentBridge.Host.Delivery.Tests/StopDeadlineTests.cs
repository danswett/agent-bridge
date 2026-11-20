using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

public sealed class StopDeadlineTests(ITestOutputHelper output)
{
    const string Id = "77777777-7777-4777-8777-777777777777";
    const int Pid = 700;
    static readonly DateTimeOffset Started = Fixture.T0.AddHours(-1);
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_enforces_its_deadline_even_when_exit_injection_does_not_finish(bool agentError) =>
        await CheckDeadlineAsync(agentError);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unproven_worker_keeps_the_PID_handle_and_typing_fence_until_it_is_proven_gone(bool faultedProof) =>
        await CheckDeadlineAsync(false, faultedProof);

    [Fact]
    public async Task A_stop_waiting_for_another_delivery_forces_termination_without_typing() =>
        await CheckDeadlineAsync(false, typingBusy: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stop_never_terminates_while_another_failed_worker_is_unproven(bool tracked) =>
        await CheckDeadlineAsync(false, typingBusy: true, otherWorker: tracked ? "tracked" : "unknown");

    [Fact]
    public async Task Stop_keeps_the_held_process_after_an_injector_exception_with_no_exit_proof() =>
        await CheckDeadlineAsync(false, unknownException: true);

    [Fact]
    public async Task Stop_during_a_departure_exclusion_ends_the_process_without_typing() =>
        await CheckDeadlineAsync(false, departureBlocked: true);

    [Fact]
    public async Task Stop_during_a_committed_departure_is_busy_and_opens_nothing() =>
        await CheckDeadlineAsync(false, departureBlocked: true, committed: true);

    async Task CheckDeadlineAsync(bool agentError, bool? faultedProof = null, bool typingBusy = false, string? otherWorker = null,
        bool unknownException = false, bool departureBlocked = false, bool committed = false)
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, Pid);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        if (agentError)
        {
            Fixture.Append(events, Copilot.Event("session.error",
                new { errorType = "session", statusCode = 402, message = "Synthetic quota failure." }, 3));
        }
        else { Fixture.Append(events, Copilot.Assistant("Done.", 3)); }
        Fixture.Append(events, Copilot.TurnEnd(4));
        f.Processes.Run(AgentKind.Copilot, Pid, startedAt: Started);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Equal(SessionStatus.Idle, source.Get(Id)!.Status);
        if (agentError) { Assert.Equal(AttentionKind.Error, source.Get(Id)!.Attention?.Kind); }

        var injector = new StalledInjector { UnknownException = unknownException };
        var proof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (faultedProof is not null) { injector.WorkerExited = proof.Task; }
        if (faultedProof == true) { proof.SetException(new IOException("fixture cannot observe worker exit")); }
        var control = new Control(f);
        var gate = new DeliveryGate();
        if (typingBusy) { await gate.WaitToTypeAsync(); }
        if (otherWorker is not null) { gate.MarkWorkerUncertain(otherWorker == "tracked" ? other.Task : null); }
        using var exclusion = departureBlocked ? gate.TryExclude(TimeSpan.FromSeconds(30), out _) : null;
        if (departureBlocked) { Assert.NotNull(exclusion); }
        if (committed)
        {
            Assert.True(exclusion!.TryBeginCommit(out _));
            Assert.True(exclusion.TryCommit(new DepartureConnection(), 0, false, 0, out _));
        }
        var stopper = new StopService(gate, source, f.Processes, new NoBridge(), injector, control,
            new StopOptions
            {
                CopilotHome = f.CopilotHome,
                PlatformSupported = true,
                Grace = TimeSpan.FromMilliseconds(20),
                TerminateWait = TimeSpan.FromMilliseconds(20),
            });
        var stopping = stopper.StopAsync(new StopRequest { Id = Id, Request = Guid.NewGuid().ToString("D") });
        try
        {
            if (!typingBusy && !departureBlocked) { await injector.Entered.Task.WaitAsync(Bound); }
            var result = await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            if (committed)
            {
                Assert.Equal(StopOutcome.Busy, result.Outcome);
                Assert.Equal(0, control.Opened);
                Assert.Equal(0, control.Terminations);
                Assert.False(injector.Entered.Task.IsCompleted);
                return;
            }
            if (faultedProof is not null || unknownException || otherWorker is not null)
            {
                // Unknown worker proof never permits a kill or an ended result (#2910).
                Assert.Equal(StopOutcome.Failed, result.Outcome);
                Assert.Equal(0, control.Terminations);
                Assert.Equal(1, control.Handles);
                Assert.NotNull(source.Get(Id));
                if (otherWorker == "tracked")
                {
                    other.SetResult();
                    while (Volatile.Read(ref control.Handles) != 0) { Assert.True(await control.Disposals.WaitAsync(Bound)); }
                    return;
                }
                if (otherWorker is not null) { return; }
                using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitToTypeAsync(wait.Token));
                if (faultedProof == false)
                {
                    proof.SetResult();
                    await gate.WaitToTypeAsync().WaitAsync(Bound);
                    gate.DoneTyping();
                    Assert.Equal(0, control.Handles);
                    using var quiet = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
                    Assert.NotNull(quiet);
                }
                return;
            }
            Assert.Equal(StopOutcome.Ended, result.Outcome);
            Assert.True(result.Forced);
            Assert.Equal(1, control.Terminations);
            Assert.Null(source.Get(Id));
            if (typingBusy || departureBlocked)
            {
                Assert.False(injector.Entered.Task.IsCompleted);
                Assert.Equal(0, control.Handles);
                if (typingBusy)
                {
                    using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitToTypeAsync(wait.Token));
                }
                else
                {
                    Assert.Null(gate.TryEnterDelivery());
                    exclusion!.Dispose();
                    await gate.WaitToTypeAsync().WaitAsync(Bound);
                    gate.DoneTyping();
                }
            }
        }
        finally
        {
            output.WriteLine($"Before cleanup: stop completed={stopping.IsCompleted}; "
                + $"terminations={control.Terminations}; status={source.Get(Id)?.Status}");
            injector.Release.TrySetResult();
            proof.TrySetResult();
            other.TrySetResult();
            if (typingBusy) { gate.DoneTyping(); }
            await stopping.WaitAsync(Bound);
        }
    }

    sealed class NoBridge : IAgentHaBridgeProbe
    {
        public string? Active() => null;
    }

    sealed class StalledInjector : IConsoleInjector
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WorkerExited { get; set; } = Task.CompletedTask;
        public bool UnknownException { get; init; }

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException e)
            {
                if (UnknownException) { throw new IOException("fixture cannot identify its worker"); }
                throw new InjectorWorkerException(e, WorkerExited);
            }
            return new InjectionResult(InjectionStatus.NothingTyped, "fixture released without typing");
        }
    }

    sealed class Control(Fixture f) : IProcessControl
    {
        public int Terminations;
        public int Handles;
        public int Opened;
        public SemaphoreSlim Disposals { get; } = new(0);

        public HeldOpen Open(int processId, DateTimeOffset startedAt)
        {
            Assert.Equal(Pid, processId);
            Assert.Equal(Started, startedAt);
            Interlocked.Increment(ref Opened);
            Interlocked.Increment(ref Handles);
            return HeldOpen.Of(new Held(this, f));
        }

        sealed class Held(Control owner, Fixture fixture) : IHeldProcess
        {
            public int Id => Pid;
            public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(owner.Terminations > 0);

            public string? Terminate()
            {
                Interlocked.Increment(ref owner.Terminations);
                fixture.Processes.Exit(AgentKind.Copilot, Pid);
                return null;
            }

            public void Dispose()
            {
                Interlocked.Decrement(ref owner.Handles);
                owner.Disposals.Release();
            }
        }
    }
}
