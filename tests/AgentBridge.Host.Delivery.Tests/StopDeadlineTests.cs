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
    public async Task Stop_enforces_its_deadline_even_when_exit_injection_does_not_finish(bool agentError)
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
        var control = new Control(f);
        var stopper = new StopService(new DeliveryGate(), source, f.Processes, new NoBridge(), injector, control,
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
            await injector.Entered.Task.WaitAsync(Bound);
            var result = await stopping.WaitAsync(TimeSpan.FromSeconds(2));
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

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new InjectionResult(InjectionStatus.NothingTyped, "fixture released without typing");
        }
    }

    sealed class Control(Fixture f) : IProcessControl
    {
        public int Terminations;

        public HeldOpen Open(int processId, DateTimeOffset startedAt)
        {
            Assert.Equal(Pid, processId);
            Assert.Equal(Started, startedAt);
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

            public void Dispose() { }
        }
    }
}
