using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
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
    public async Task The_final_effect_fence_rechecks_the_switch_and_questions(string change)
    {
        await using var h = new Harness();
        await h.Error(Empty);
        await h.Watch.TickAsync();
        h.BeforeKeys = async () => { if (change == "off") h.Enabled = false; else await h.Error(change); };
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

    sealed class Harness : ISessionReplier, IAsyncDisposable
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
        readonly string events;
        int at = 3598;
        public Harness()
        {
            events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source();
            Watch = new(Source, this, () => Enabled, F.Time, Log.Add, _ => Held);
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
        public async Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        {
            Requests.Add(request);
            if (BeforeKeys is { } before) await before();
            if (request.StillCurrent?.Invoke() != true) return new(AnswerOutcome.Stale);
            Keys++;
            return new(Outcome);
        }
        public async ValueTask DisposeAsync() { await Watch.DisposeAsync(); await Source.DisposeAsync(); F.Dispose(); }
    }
}
