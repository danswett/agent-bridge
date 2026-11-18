using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

public sealed class O302ParentProbeTests
{
    const string Id = "99999999-9999-4999-8999-999999999999";
    const string Empty = """{"errorType":"query","message":"No response was returned. Send your message again to retry."}""";

    [Theory]
    [InlineData("abort")]
    [InlineData("session.shutdown")]
    [InlineData("session.resume")]
    [InlineData("human")]
    public async Task Unprojected_withdrawals_at_the_real_final_effect_prevent_auto_retry(string change)
    {
        await using var h = new Harness();
        await h.Fail();
        await h.Watch.TickAsync();
        h.BeforeKeys = () => h.Unprojected(change);
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        Assert.Equal(0, h.Keys);
        Assert.Equal(1, h.Requests);
    }

    sealed class Harness : ISessionReplier, IConsoleInjector, IAgentHaBridgeProbe, ITargetGuard, IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly AutoRetryWatch Watch;
        readonly SessionSource source;
        readonly ReplyService replier;
        readonly string events;
        int at = 3598;
        public int Keys, Requests;
        public Action? BeforeKeys;

        public Harness()
        {
            events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            source = F.Source();
            replier = new ReplyService(new DeliveryGate(F.Time), source, F.Processes, this, this, this,
                new ReplyOptions { CopilotHome = F.CopilotHome, PlatformSupported = true, PreRead = false, Time = F.Time });
            Watch = new(source, this, () => true, F.Time, _ => { });
        }

        public async Task Fail()
        {
            Fixture.Append(events, Copilot.User("continue", at++), Copilot.TurnStart(at++),
                Copilot.Event("session.error", JsonSerializer.Deserialize<JsonElement>(Empty), at++));
            await source.RefreshAsync();
        }

        public void Unprojected(string change) => Fixture.Append(events, change == "human"
            ? Copilot.User("stop retrying", at++)
            : Copilot.Event(change, new { reason = "user_initiated" }, at++));

        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        {
            Requests++;
            return replier.ReplyAsync(request, peer, cancellationToken, watch);
        }

        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default)
        {
            BeforeKeys?.Invoke();
            if (plan.Proceed?.Invoke() is { } reason) return Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, reason));
            Keys++;
            Fixture.Append(events, Copilot.User(plan.Sends[0].Text, at++));
            return Task.FromResult(new InjectionResult(InjectionStatus.Typed, ""));
        }

        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default) => Task.FromResult(new ConsoleScreen(
            ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580",
             " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab"], null));
        public string? Active() => null;
        public string? Refuse(int processId) => null;
        public async ValueTask DisposeAsync() { await Watch.DisposeAsync(); await source.DisposeAsync(); F.Dispose(); }
    }
}
