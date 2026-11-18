using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

public sealed class AutoRetryTests
{
    const string Id = "99999999-9999-4999-8999-999999999999";
    const string Empty = """{"errorType":"query","message":"No response was returned. Send your message again to retry."}""";

    [Fact]
    public async Task Repeated_transient_errors_share_three_backed_off_audited_attempts()
    {
        await using var h = new Harness();
        await h.Error(Empty);
        await h.Watch.TickAsync();
        foreach (var seconds in new[] { 30, 60, 90 })
        {
            h.F.Time.Advance(TimeSpan.FromSeconds(seconds - 1));
            await h.Watch.TickAsync();
            var before = h.Requests.Count;
            h.F.Time.Advance(TimeSpan.FromSeconds(1));
            await h.Watch.TickAsync();
            Assert.Equal(before + 1, h.Requests.Count);
            await h.Error(Empty);
        }
        h.F.Time.Advance(TimeSpan.FromMinutes(10));
        await h.Watch.TickAsync();
        Assert.Equal(3, h.Requests.Count);
        Assert.All(h.Requests, r => { Assert.Equal("continue", r.Text); Assert.True(r.RequireIdle); });
        Assert.Equal(new AutoRetryStatus("needs-person", "no-response", 3), h.Source.Get(Id)!.AutoRetry);
        Assert.Equal(3, h.Log.Count(l => l.Contains("Auto: started", StringComparison.Ordinal)));
        var attributes = AgentBridge.HomeAssistant.HaPayloads.StatusAttributes(h.Source.Get(Id)!, "test", "test", h.F.Time.GetUtcNow());
        Assert.Equal(3, attributes["auto_retry"]!["attempts"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"errorType":"query","statusCode":402}""", "quota")]
    [InlineData("""{"errorType":"authorization","statusCode":401,"remediation":"sign_in"}""", "auth")]
    [InlineData("""{"errorType":"query","statusCode":403}""", "auth")]
    [InlineData("""{"errorType":"query","remediation":"sign_in","message":"No response was returned. Send your message again to retry."}""", "auth")]
    [InlineData("""{"errorType":"query","message":"unknown"}""", "other")]
    [InlineData("form", "question")]
    [InlineData("permission", "permission")]
    [InlineData("trust", "permission")]
    public async Task Excluded_errors_and_questions_publish_needs_person_without_typing(string error, string reason)
    {
        await using var h = new Harness();
        await h.Error(error);
        await h.Watch.TickAsync();
        h.F.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Watch.TickAsync();
        Assert.Empty(h.Requests);
        Assert.Equal(new AutoRetryStatus("needs-person", reason, 0), h.Source.Get(Id)!.AutoRetry);
        Assert.True(AgentBridge.HomeAssistant.HaPayloads.NeedsPerson(h.Source.Get(Id)!));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("form")]
    [InlineData("permission")]
    [InlineData("trust")]
    [InlineData("abort")]
    public async Task The_final_effect_fence_rechecks_the_switch_and_questions(string change)
    {
        await using var h = new Harness();
        await h.Error(Empty);
        await h.Watch.TickAsync();
        h.BeforeKeys = async () => { if (change == "off") h.Enabled = false; else if (change == "abort") await h.Abort(); else await h.Error(change); };
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        Assert.Single(h.Requests);
        Assert.Equal(0, h.Keys);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 300)]
    public async Task Disabled_or_expired_errors_never_send(bool enabled, int seconds)
    {
        await using var h = new Harness { Enabled = enabled };
        await h.Error(Empty);
        h.F.Time.Advance(TimeSpan.FromSeconds(seconds));
        await h.Watch.TickAsync();
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Uncertain_delivery_spends_the_budget_and_never_resends()
    {
        await using var h = new Harness { Outcome = AnswerOutcome.Unconfirmed };
        await h.Error(Empty);
        await h.Watch.TickAsync();
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        await h.Error(Empty);
        h.F.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Watch.TickAsync();
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task A_completed_real_response_clears_the_episode_before_a_new_failure()
    {
        await using var h = new Harness();
        await h.Error(Empty);
        await h.Watch.TickAsync();
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        await h.Heal();
        await h.Watch.TickAsync();
        Assert.Null(h.Source.Get(Id)!.AutoRetry);
        await h.Error(Empty);
        await h.Watch.TickAsync();
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(1, h.Source.Get(Id)!.AutoRetry!.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_acknowledged_continue_requires_a_new_failure_before_another_attempt(bool realReply)
    {
        await using var h = new Harness(realReply);
        await h.Error(Empty);
        await h.Watch.TickAsync();
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        Assert.Equal(1, h.Keys);
        h.F.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Watch.TickAsync();
        Assert.Equal(1, h.Keys);
        Assert.Single(h.Requests);
        await h.Error(Empty);
        await h.Watch.TickAsync();
        Assert.Equal(2, h.Keys);
        h.F.Time.Advance(TimeSpan.FromSeconds(210));
        await h.Watch.TickAsync();
        Assert.Equal(new AutoRetryStatus("needs-person", "no-response", 2), h.Source.Get(Id)!.AutoRetry);
    }

    [Fact]
    public async Task A_delegate_response_cannot_refill_the_exhausted_main_budget()
    {
        await using var h = new Harness();
        await h.Error(Empty);
        await h.Watch.TickAsync();
        foreach (var seconds in new[] { 30, 60, 90 })
        {
            h.F.Time.Advance(TimeSpan.FromSeconds(seconds));
            await h.Watch.TickAsync();
            await h.Error(Empty);
        }
        await h.DelegateHeal();
        await h.Watch.TickAsync();
        await h.Error(Empty);
        await h.Watch.TickAsync();
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        Assert.Equal(3, h.Keys);
    }

    [Fact]
    public async Task A_main_abort_withdraws_a_scheduled_continue()
    {
        await using var h = new Harness();
        await h.Error(Empty);
        await h.Watch.TickAsync();
        await h.Abort();
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("session.shutdown")]
    [InlineData("session.resume")]
    [InlineData("human")]
    public async Task Unprojected_withdrawals_at_the_real_final_effect_prevent_auto_retry(string change)
    {
        await using var h = new Harness(realReply: true);
        await h.Error(Empty);
        await h.Watch.TickAsync();
        h.BeforeKeys = () => h.Unprojected(change);
        h.F.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Watch.TickAsync();
        Assert.Equal(0, h.Keys);
        Assert.Single(h.Requests);
    }

    [Fact]
    public void Auto_retry_starts_only_after_the_second_startup_Stop_check_and_Serving()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AgentBridge.slnx")))
            root = root.Parent ?? throw new IOException("repository root not found");
        var program = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.Host", "Program.cs"));
        var start = program.IndexOf("autoRetry.Start(stop.Token)", StringComparison.Ordinal);
        Assert.True(start > program.LastIndexOf("handover.StoppedFromTheApp(identity.Folder)", StringComparison.Ordinal));
        Assert.True(start > program.IndexOf("startGate.Serving()", StringComparison.Ordinal));
        Assert.Contains("() => startGate.IsServing", program);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_in_the_startup_window_prevents_a_continue(bool stopped)
    {
        HostStartGate? gate = null;
        await using var h = new Harness(serving: () => gate?.IsServing == true);
        await HostHandover.RunAsync(async handover =>
        {
            gate = new HostStartGate(handover, h.F.Root);
            Assert.False(handover.StoppedFromTheApp(h.F.Root));
            await h.Error(Empty);
            await h.Watch.TickAsync();
            if (stopped) File.WriteAllText(Path.Combine(h.F.Root, HostLauncherContract.StopMarker), "");
            h.F.Time.Advance(TimeSpan.FromSeconds(30));
            await h.Watch.TickAsync();
            Assert.Equal(stopped, handover.StoppedFromTheApp(h.F.Root));
            Assert.Empty(h.Requests);
            if (!stopped)
            {
                gate.Serving();
                await h.Watch.TickAsync();
                h.F.Time.Advance(TimeSpan.FromSeconds(30));
                await h.Watch.TickAsync();
                Assert.Single(h.Requests);
            }
            return 0;
        }, _ => { }, supervised: true, checkStop: false);
    }

    [Theory]
    [InlineData("{}", true, false)]
    [InlineData("""{"sessions":{"autoRetry":false}}""", false, false)]
    [InlineData("""{"sessions":{"autoRetry":null}}""", false, true)]
    [InlineData("""{"sessions":{"autoRetry":"yes"}}""", false, true)]
    public void The_config_switch_defaults_on_and_malformed_settings_fail_closed(string json, bool enabled, bool problem)
    {
        var parsed = HostSettings.Parse(json, "test");
        Assert.Equal(enabled, parsed.Settings.AutoRetry);
        Assert.Equal(problem, parsed.Problem is not null);
    }

    sealed class Harness : ISessionReplier, IConsoleInjector, IAgentHaBridgeProbe, ITargetGuard, IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly SessionSource Source;
        public readonly AutoRetryWatch Watch;
        public readonly List<ReplyRequest> Requests = [];
        public readonly List<string> Log = [];
        public bool Enabled = true;
        public bool Held;
        public AnswerOutcome Outcome = AnswerOutcome.Delivered;
        public int Keys;
        public Func<Task>? BeforeKeys;
        readonly ReplyService? real;
        readonly string events;
        int at = 3598;
        public Harness(bool realReply = false, Func<bool>? serving = null)
        {
            events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source();
            if (realReply) real = new ReplyService(new DeliveryGate(F.Time), Source, F.Processes, this, this, this,
                new ReplyOptions { CopilotHome = F.CopilotHome, PlatformSupported = true, PreRead = false, Time = F.Time });
            Watch = new(Source, this, () => Enabled, F.Time, Log.Add, _ => Held, serving);
        }
        public async Task Error(string error)
        {
            if (error == "trust") { Held = true; error = Empty; }
            Fixture.Append(events, Copilot.User("continue", at++), Copilot.TurnStart(at++));
            Fixture.Append(events, error switch
            {
                "form" => Copilot.ToolStart("q", "ask_user", new { message = "Which?", requestedSchema = new { properties = new { pick = new { type = "boolean" } } } }, at++),
                "permission" => Copilot.Event("permission.requested", new { requestId = "r", permissionRequest = new { kind = "shell" } }, at++),
                _ => Copilot.Event("session.error", JsonSerializer.Deserialize<JsonElement>(error), at++),
            });
            await Source.RefreshAsync();
        }
        public bool Available => true;
        public async Task Heal()
        {
            Fixture.Append(events, Copilot.User("go", at++), Copilot.TurnStart(at++), Copilot.Assistant("done", at++), Copilot.TurnEnd(at++));
            await Source.RefreshAsync();
        }
        public async Task DelegateHeal()
        {
            Fixture.Append(events, Copilot.User("continue", at++), Copilot.TurnStart(at++),
                Copilot.Event("assistant.message", new { content = "delegate reply" }, at++, agentId: "helper"), Copilot.TurnEnd(at++));
            await Source.RefreshAsync();
        }
        public async Task Abort()
        {
            Fixture.Append(events, Copilot.Event("abort", new { reason = "user_initiated" }, at++));
            await Source.RefreshAsync();
        }
        public Task Unprojected(string change)
        {
            Fixture.Append(events, change == "human" ? Copilot.User("stop retrying", at++)
                : Copilot.Event(change, new { reason = "user_initiated" }, at++));
            return Task.CompletedTask;
        }
        public async Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        {
            Requests.Add(request);
            if (real is not null) return await real.ReplyAsync(request, peer, cancellationToken, watch);
            if (BeforeKeys is { } before) await before();
            if (request.StillCurrent?.Invoke() != true) return new(AnswerOutcome.Stale);
            Keys++;
            return new(Outcome);
        }
        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default)
        {
            if (BeforeKeys is { } before) await before();
            if (plan.Proceed?.Invoke() is { } reason) return new(InjectionStatus.NothingTyped, reason);
            Keys++;
            Fixture.Append(events, Copilot.User(plan.Sends[0].Text, at++));
            return new(InjectionStatus.Typed, "");
        }
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default) => Task.FromResult(new ConsoleScreen(
            ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580",
             " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab"], null));
        public string? Active() => null;
        public string? Refuse(int processId) => null;
        public async ValueTask DisposeAsync() { await Watch.DisposeAsync(); await Source.DisposeAsync(); F.Dispose(); }
    }
}
