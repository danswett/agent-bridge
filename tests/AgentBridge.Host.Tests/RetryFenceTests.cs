using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

public sealed class RetryFenceTests
{
    const string Id = "99999999-9999-4999-8999-999999999999";
    const string Empty = """{"errorType":"query","message":"No response was returned. Send your message again to retry."}""";

    [Theory]
    [InlineData("episode")]
    [InlineData("activity")]
    [InlineData("process")]
    [InlineData("unobservedProcess")]
    [InlineData("cancel")]
    [InlineData("question")]
    [InlineData("permission")]
    public async Task A_retry_invalidated_behind_the_typing_queue_never_reaches_the_injector(string change)
    {
        await using var h = await Harness.Create();
        using var cancel = new CancellationTokenSource();
        await h.Gate.WaitToTypeAsync();
        h.Gate.TypingQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = h.Episode.TryAsync(1, Guid.NewGuid(), RetryReason.Owner, cancel.Token);
        try
        {
            await h.Gate.TypingQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(send.IsCompleted);
            await h.Change(change, cancel);
        }
        finally { h.Gate.DoneTyping(); }
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Keys);
        Assert.Equal(change is "episode" or "cancel" ? RetryStop.Cancelled : RetryStop.Changed, h.Episode.Stop);
        Assert.Equal(AnswerOutcome.Stale, Assert.Single(h.Episode.Attempts).Outcome);
    }

    [Theory]
    [InlineData("activity")]
    [InlineData("process")]
    [InlineData("unobservedProcess")]
    [InlineData("episode")]
    [InlineData("cancel")]
    [InlineData("expired")]
    [InlineData("question")]
    [InlineData("permission")]
    public async Task The_injector_queue_rechecks_freshness_after_the_last_screen_read(string change)
    {
        await using var h = await Harness.Create();
        using var cancel = new CancellationTokenSource();
        h.BeforeKeys = () => h.Change(change, cancel);
        Assert.True(await h.Episode.TryAsync(1, Guid.NewGuid(), RetryReason.Owner, cancel.Token));
        Assert.Equal(0, h.Keys);
        Assert.Equal(change switch
        {
            "episode" or "cancel" => RetryStop.Cancelled,
            "expired" => RetryStop.Expired,
            _ => RetryStop.Changed,
        }, h.Episode.Stop);
        Assert.Equal(AnswerOutcome.Stale, Assert.Single(h.Episode.Attempts).Outcome);
    }

    [Fact]
    public async Task Cancellation_during_keys_keeps_the_attempt_excluded_until_the_injector_finishes()
    {
        await using var h = await Harness.Create();
        using var cancel = new CancellationTokenSource();
        h.KeysDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = h.Episode.TryAsync(1, Guid.NewGuid(), RetryReason.Owner, cancel.Token);
        await h.KeysStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        Assert.False(send.IsCompleted);
        Assert.False(await h.Episode.TryAsync(2, Guid.NewGuid(), RetryReason.Owner));
        h.KeysDone.SetResult();
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, h.Keys);
        Assert.Equal(RetryStop.Cancelled, h.Episode.Stop);
    }

    sealed class Harness : IConsoleInjector, IAgentHaBridgeProbe, ITargetGuard, IAsyncDisposable
    {
        readonly Fixture f = new();
        readonly string events;
        readonly SessionSource source;
        readonly ReplyService replier;
        public readonly DeliveryGate Gate = new();
        public readonly TaskCompletionSource KeysStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? KeysDone;
        public Func<Task>? BeforeKeys;
        public RetryEpisode Episode = null!;
        public int Keys;
        Harness()
        {
            events = f.CopilotSession(Id, 100);
            Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2),
                Copilot.Event("session.error", JsonSerializer.Deserialize<JsonElement>(Empty), 3));
            f.Processes.Run(AgentKind.Copilot, 100);
            source = f.Source();
            replier = new ReplyService(Gate, source, f.Processes, this, this, this, new ReplyOptions
            {
                CopilotHome = f.CopilotHome, PlatformSupported = true, PreRead = false,
                ConfirmTimeout = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(10),
            });
        }
        public static async Task<Harness> Create()
        {
            var h = new Harness();
            await h.source.RefreshAsync();
            Assert.Equal(AttentionKind.Error, h.source.Get(Id)!.Attention!.Kind);
            h.Episode = new(Guid.Parse(Id), h.Observe, h.replier, h.f.Time, _ => { });
            return h;
        }
        RetryObservation? Observe()
        {
            var s = source.Get(Id);
            return s is null || source.ProcessOf(Id) is not { } process ? null
                : new(JsonSerializer.Deserialize<JsonElement>(Empty), f.Time.GetUtcNow(), process, source.Revision,
                    s.Status == SessionStatus.Idle && s.Question is null && s.Attention?.Kind == AttentionKind.Error);
        }
        public async Task Change(string change, CancellationTokenSource? cancel = null)
        {
            switch (change)
            {
                case "episode": Episode.Cancel(); return;
                case "cancel": cancel!.Cancel(); return;
                case "expired": f.Time.Advance(TimeSpan.FromMinutes(5)); return;
                case "unobservedProcess": f.Processes.Run(AgentKind.Copilot, 100, startedAt: Fixture.T0.AddSeconds(-1)); return;
                case "process": f.Processes.Run(AgentKind.Copilot, 100, startedAt: Fixture.T0.AddSeconds(-1)); break;
                case "question": Fixture.Append(events, Copilot.ToolStart("q", "ask_user",
                    new { message = "Which?", requestedSchema = new { properties = new { pick = new { type = "boolean" } } } }, 5)); break;
                case "permission": Fixture.Append(events, Copilot.Event("permission.requested",
                    new { requestId = "r", permissionRequest = new { kind = "shell" } }, 5)); break;
                default: Fixture.Append(events, Copilot.User("new turn", 5), Copilot.TurnStart(6), Copilot.TurnEnd(7)); break;
            }
            await source.RefreshAsync();
        }
        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default)
        {
            if (BeforeKeys is { } change) { await change(); }
            if (plan.Proceed?.Invoke() is { } reason) { return new(InjectionStatus.NothingTyped, reason); }
            Keys++;
            KeysStarted.TrySetResult();
            if (KeysDone is { } done) { await done.Task; }
            Fixture.Append(events, Copilot.User(plan.Sends[0].Text, 10));
            return new(InjectionStatus.Typed, "");
        }
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default)
        {
            return Task.FromResult(new ConsoleScreen(
                ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584",
                 "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580",
                 " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab"], null));
        }
        public string? Active() => null;
        public string? Refuse(int processId) => null;
        public async ValueTask DisposeAsync() { await source.DisposeAsync(); f.Dispose(); }
    }
}
