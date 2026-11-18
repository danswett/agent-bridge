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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stop_waiting_for_another_delivery_forces_termination_without_typing(bool unknownWorker) =>
        await CheckDeadlineAsync(false, typingBusy: true, unknownWorker: unknownWorker);

    async Task CheckDeadlineAsync(bool agentError, bool? faultedProof = null, bool typingBusy = false, bool unknownWorker = false)
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

        var injector = new StalledInjector();
        var proof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (faultedProof is not null) { injector.WorkerExited = proof.Task; }
        if (faultedProof == true) { proof.SetException(new IOException("fixture cannot observe worker exit")); }
        var control = new Control(f);
        var gate = new DeliveryGate();
        if (typingBusy) { await gate.WaitToTypeAsync(); }
        if (unknownWorker) { gate.MarkWorkerUncertain(null); }
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
            if (!typingBusy) { await injector.Entered.Task.WaitAsync(Bound); }
            var result = await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            if (typingBusy)
            {
                Assert.Equal(StopOutcome.Ended, result.Outcome);
                Assert.True(result.Forced);
                Assert.False(injector.Entered.Task.IsCompleted);
                Assert.Equal(1, control.Terminations);
                Assert.Equal(0, control.Handles);
                using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitToTypeAsync(wait.Token));
                return;
            }
            if (faultedProof is not null)
            {
                Assert.Equal(StopOutcome.Failed, result.Outcome);
                Assert.Equal(0, control.Terminations);
                Assert.Equal(1, control.Handles);
                using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitToTypeAsync(wait.Token));
                if (!faultedProof.Value)
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
        }
        finally
        {
            output.WriteLine($"Before cleanup: stop completed={stopping.IsCompleted}; "
                + $"terminations={control.Terminations}; status={source.Get(Id)?.Status}");
            injector.Release.TrySetResult();
            proof.TrySetResult();
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

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException e) { throw new InjectorWorkerException(e, WorkerExited); }
            return new InjectionResult(InjectionStatus.NothingTyped, "fixture released without typing");
        }
    }

    sealed class Control(Fixture f) : IProcessControl
    {
        public int Terminations;
        public int Handles;

        public HeldOpen Open(int processId, DateTimeOffset startedAt)
        {
            Assert.Equal(Pid, processId);
            Assert.Equal(Started, startedAt);
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
            }
        }
    }
}
