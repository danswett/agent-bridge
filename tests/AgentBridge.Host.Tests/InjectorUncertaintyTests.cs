using System.ComponentModel;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

public sealed class InjectorUncertaintyTests
{
    [Fact]
    public async Task An_injector_exception_converted_to_a_reply_result_still_blocks_departure_before_admission_is_released()
    {
        using var fixture = new Fixture();
        const string id = "77777777-7777-4777-8777-777777777777";
        var events = fixture.CopilotSession(id, 100);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes);
        await source.RefreshAsync();
        var gate = new DeliveryGate();
        var injector = new UncertainInjector();
        var replies = new ReplyService(gate, source, fixture.Processes, new NoBridge(), new NoGuard(), injector,
            new ReplyOptions { CopilotHome = fixture.CopilotHome, PlatformSupported = true, PreRead = false });
        var result = await replies.ReplyAsync(new ReplyRequest { Id = id, Request = Guid.NewGuid().ToString(), Text = "continue" });
        Assert.True(injector.Attempted);
        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        var coordinator = new LifecycleCoordinator(gate, new DepartureConditions());
        Assert.Equal(QuietBlockedReason.JournalUncertain,
            (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Reason);
    }

    sealed class NoBridge : IAgentHaBridgeProbe { public string? Active() => null; }
    sealed class NoGuard : ITargetGuard { public string? Refuse(int processId) => null; }
    sealed class UncertainInjector : IConsoleInjector
    {
        public bool Attempted { get; private set; }
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            Attempted = true;
            throw new Win32Exception("worker termination could not be confirmed");
        }
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConsoleScreen(["● Done.", "", "╻▄▄▄▄▄▄▄▄", "┃", "╹▀▀▀▀▀▀▀▀",
                " ← open sidebar · Interactive · Allow All · / commands · ? help · tab next tab"], null));
    }
}
