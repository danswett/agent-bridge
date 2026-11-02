using System.Net;
using AgentBridge.HomeAssistant;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Home Assistant bans an address after login_attempts_threshold (10) failed logins. On 2026-11-02 a
// just-migrated machine's connector repeated a rejected token every 30 s until it was banned (#227).
// These hold the guard to what agent-ha-bridge's test-auth-backoff.ps1 holds its daemon to, and to
// the stricter rule that a rejected token stops rather than being repeated on a timer for ever.
public sealed class AuthBackoffTests : IDisposable
{
    const string Token = "test-token-not-a-secret";
    static readonly Uri Url = new("http://ha.test:8123");
    readonly string folder = Path.Combine(Path.GetTempPath(), "abau-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time = new(FakeSessions.T0);

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    string Record => Path.Combine(folder, HaAuthGuard.FileName);

    // Home Assistant answering whatever Status says, and when each request reached it.
    sealed class Answering(FakeTimeProvider time) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Unauthorized;
        public Exception? Fail { get; set; }
        public List<DateTimeOffset> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Seen) { Seen.Add(time.GetUtcNow()); }
            if (Fail is { } e) { throw e; }
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("[]") });
        }
    }

    HomeAssistantClient Client(HttpMessageHandler handler, HaAuthGuard guard, string token = Token) => new(Url, token, new HttpClient(handler), auth: guard);

    static async Task<Exception?> Publish(HomeAssistantClient client)
    {
        try
        {
            await client.PublishAsync("copilot/cli/agent_bridge_x/status/state", "idle", true);
            return null;
        }
        catch (HttpRequestException e) { return e; }
    }

    // A caller retrying every 30 s for as long as it takes, as the connector's startup did.
    async Task Hammer(HomeAssistantClient client, TimeSpan howLong)
    {
        for (var t = TimeSpan.Zero; t < howLong; t += TimeSpan.FromSeconds(30))
        {
            await Publish(client);
            time.Advance(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task A_rejected_token_is_sent_at_most_three_times_minutes_apart_and_never_again_until_it_changes()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);

        await Hammer(Client(ha, guard), TimeSpan.FromDays(2));

        Assert.Equal(HaAuthGuard.MaxRejections, ha.Seen.Count);
        Assert.All(ha.Seen.Zip(ha.Seen.Skip(1)), p => Assert.True(p.Second - p.First >= TimeSpan.FromMinutes(5), $"{p.First:t} then {p.Second:t}"));
        var said = guard.Describe(guard.Credential(Url, Token))!;
        Assert.StartsWith("Home Assistant rejected the token: run `agent-bridge-host ha token set`", said, StringComparison.Ordinal);
        Assert.Contains("Stopped after 3", said, StringComparison.Ordinal);

        // The host restarted: the record is read again, and the token is still not sent.
        var restarted = new HaAuthGuard(Record, time);
        var held = Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, restarted)));
        Assert.False(held.Sent);
        Assert.Equal(HaAuthGuard.MaxRejections, ha.Seen.Count);
    }

    [Fact]
    public async Task A_changed_token_gets_exactly_one_fresh_attempt_and_a_good_one_then_goes_through()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        await Hammer(Client(ha, guard), TimeSpan.FromHours(1));
        Assert.Equal(3, ha.Seen.Count);

        // ha token set with another token Home Assistant also refuses: one attempt, then nothing.
        await Hammer(Client(ha, guard, "second-token-not-a-secret"), TimeSpan.FromDays(1));
        Assert.Equal(4, ha.Seen.Count);

        // The same one given again is a change too, and earns one more.
        guard.Forget(guard.Credential(Url, "second-token-not-a-secret"));
        await Hammer(Client(ha, guard, "second-token-not-a-secret"), TimeSpan.FromDays(1));
        Assert.Equal(5, ha.Seen.Count);

        // A token Home Assistant accepts is tried once and, accepted, used as freely as ever.
        ha.Status = HttpStatusCode.OK;
        var good = Client(ha, guard, "third-token-not-a-secret");
        for (var i = 0; i < 10; i++) { Assert.Null(await Publish(good)); }
        Assert.Equal(15, ha.Seen.Count);
        Assert.Null(guard.Describe(guard.Credential(Url, "third-token-not-a-secret")));
    }

    // Home Assistant counts by this machine's address, not by the URL it was reached at.
    [Fact]
    public async Task Another_spelling_of_the_address_is_a_change_that_earns_one_attempt_not_three_more()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        await Hammer(Client(ha, guard), TimeSpan.FromHours(1));
        Assert.Equal(3, ha.Seen.Count);

        foreach (var url in new[] { "https://ha.test:8123", "http://homeassistant.test:8123" })
        {
            await Hammer(new HomeAssistantClient(new Uri(url), Token, new HttpClient(ha), auth: guard), TimeSpan.FromHours(2));
        }
        Assert.Equal(5, ha.Seen.Count);
    }

    // The record keeps a few addresses, dropping the oldest; never the one a refusal is being
    // recorded on.
    [Fact]
    public async Task A_full_record_still_keeps_the_refusals_of_a_new_address()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        for (var i = 0; i < 8; i++)
        {
            Assert.Null(await Publish(new HomeAssistantClient(new Uri($"http://ha{i}.test:8123"), Token, new HttpClient(ha), auth: guard)));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        ha.Status = HttpStatusCode.Unauthorized;
        await Hammer(Client(ha, guard), TimeSpan.FromDays(1));

        Assert.Equal(8 + HaAuthGuard.MaxRejections, ha.Seen.Count);
    }

    // A record that cannot be written must not forget a refusal when it is read again, or the token
    // would be tried every five minutes for ever.
    [Fact]
    public async Task A_record_that_cannot_be_written_still_stops_a_rejected_token_after_three_attempts()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        Assert.Null(await Publish(Client(ha, guard, "other-token-not-a-secret")));
        Assert.True(File.Exists(Record));
        Directory.CreateDirectory(Record + ".tmp");

        ha.Status = HttpStatusCode.Unauthorized;
        await Hammer(Client(ha, guard), TimeSpan.FromDays(1));

        Assert.Equal(1 + HaAuthGuard.MaxRejections, ha.Seen.Count);
    }

    // Another process holding the record's lock for good: nothing is written over its record, and
    // this one still counts for itself.
    [Fact]
    public async Task A_record_that_cannot_be_locked_is_left_alone_and_a_rejected_token_still_stops_after_three_attempts()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        Assert.Null(await Publish(Client(ha, guard, "other-token-not-a-secret")));
        var before = File.ReadAllText(Record);
        using (new FileStream(Record + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            ha.Status = HttpStatusCode.Unauthorized;
            await Hammer(Client(ha, guard), TimeSpan.FromDays(1));
        }

        Assert.Equal(1 + HaAuthGuard.MaxRejections, ha.Seen.Count);
        Assert.Equal(before, File.ReadAllText(Record));
    }

    [Fact]
    public async Task A_403_once_the_token_was_accepted_is_reported_as_a_ban_and_only_looked_at_now_and_then()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        var client = Client(ha, guard);
        Assert.Null(await Publish(client));

        ha.Status = HttpStatusCode.Forbidden;
        var banned = Assert.IsType<HomeAssistantAuthException>(await Publish(client));
        Assert.Equal((HaAuthKind.Banned, true), (banned.Kind, banned.Sent));
        Assert.StartsWith("Home Assistant has banned this machine's address", banned.Message, StringComparison.Ordinal);
        Assert.Contains("see docs/ha-connector.md", banned.Message, StringComparison.Ordinal);
        Assert.Contains("/config/ip_bans.yaml", guard.Describe(guard.Credential(Url, Token)), StringComparison.Ordinal);

        // A banned address is refused before its token is read, so a look costs nothing towards a
        // ban; but nor does it achieve anything, so it is rare.
        var before = ha.Seen.Count;
        await Hammer(client, TimeSpan.FromDays(1));
        var looks = ha.Seen.Skip(before).ToList();
        Assert.InRange(looks.Count, 1, 8);
        Assert.All(looks.Prepend(ha.Seen[before - 1]).Zip(looks), p => Assert.True(p.Second - p.First >= TimeSpan.FromMinutes(15)));

        // Cleared (the entry removed and Home Assistant restarted): the next look finds it, and
        // everything goes through again.
        ha.Status = HttpStatusCode.OK;
        await Hammer(client, TimeSpan.FromHours(5));
        Assert.Null(guard.Describe(guard.Credential(Url, Token)));
        Assert.Null(await Publish(client));
    }

    // The exact sequel to the incident: the machine banned after a rejected token's attempts, and
    // the replacement token given then is answered 403. It must read as the ban it is, and be
    // looked at again, not be stopped for good as one more rejected token.
    [Fact]
    public async Task A_403_is_a_ban_even_for_a_new_token_after_the_last_one_used_up_its_attempts()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        await Hammer(Client(ha, guard), TimeSpan.FromHours(1));
        Assert.Equal(3, ha.Seen.Count);

        ha.Status = HttpStatusCode.Forbidden;
        var replacement = Client(ha, guard, "second-token-not-a-secret");
        var banned = Assert.IsType<HomeAssistantAuthException>(await Publish(replacement));
        Assert.Equal((HaAuthKind.Banned, true), (banned.Kind, banned.Sent));

        ha.Status = HttpStatusCode.OK;
        await Hammer(replacement, TimeSpan.FromMinutes(20));
        Assert.Null(guard.Describe(guard.Credential(Url, "second-token-not-a-secret")));
        Assert.Null(await Publish(replacement));
    }

    // serve has the token accepted and sends it freely; shadow run, in its own process, has since
    // seen it refused three times. serve must not send it again on what it knew before.
    [Fact]
    public async Task A_token_accepted_in_one_process_is_not_sent_again_once_another_has_seen_it_refused()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var serve = Client(ha, new HaAuthGuard(Record, time));
        Assert.Null(await Publish(serve));

        ha.Status = HttpStatusCode.Unauthorized;
        var shadow = Client(ha, new HaAuthGuard(Record, time));
        foreach (var wait in new[] { 5, 15, 0 })
        {
            await Publish(shadow);
            time.Advance(TimeSpan.FromMinutes(wait));
        }
        Assert.Equal(4, ha.Seen.Count);

        Assert.False(Assert.IsType<HomeAssistantAuthException>(await Publish(serve)).Sent);
        Assert.Equal(4, ha.Seen.Count);
    }

    [Fact]
    public async Task A_server_error_or_a_timeout_is_no_refusal_and_is_retried_as_often_as_the_caller_likes()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.InternalServerError };
        var guard = new HaAuthGuard(Record, time);
        var client = Client(ha, guard);

        for (var i = 0; i < 12; i++) { Assert.IsNotType<HomeAssistantAuthException>(await Publish(client)); }
        ha.Fail = new TaskCanceledException("timed out");
        for (var i = 0; i < 12; i++)
        {
            try { await client.PublishAsync("t", "p", false); } catch (TaskCanceledException) { }
        }

        Assert.Equal(24, ha.Seen.Count);
        Assert.Null(guard.Describe(guard.Credential(Url, Token)));
        Assert.Null(guard.Problem);
    }

    [Fact]
    public async Task Callers_with_a_token_not_yet_accepted_go_one_at_a_time_so_a_burst_spends_one_attempt()
    {
        var gate = new TaskCompletionSource();
        var seen = 0;
        var handler = new Waiting(async () =>
        {
            Interlocked.Increment(ref seen);
            await gate.Task;
            return HttpStatusCode.Unauthorized;
        });
        var guard = new HaAuthGuard(Record, time);
        var client = new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: guard);

        var burst = Enumerable.Range(0, 6).Select(_ => Publish(client)).ToList();
        await Task.Delay(200);
        gate.SetResult();
        var outcomes = await Task.WhenAll(burst);

        Assert.Equal(1, seen);
        Assert.All(outcomes, e => Assert.IsType<HomeAssistantAuthException>(e));
        Assert.Single(outcomes, e => ((HomeAssistantAuthException)e!).Sent);
    }

    // The token revoked while a request with it was on its way: that request's success, answered
    // last, must not wipe out the refusal of the one Home Assistant answered after it.
    [Fact]
    public async Task A_success_already_on_its_way_does_not_erase_a_newer_refusal()
    {
        var slow = new TaskCompletionSource();
        var calls = 0;
        var handler = new Waiting(async () =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1) { return HttpStatusCode.OK; }
            if (n == 2) { await slow.Task; return HttpStatusCode.OK; }
            return HttpStatusCode.Unauthorized;
        });
        var guard = new HaAuthGuard(Record, time);
        var client = new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: guard);
        Assert.Null(await Publish(client));

        var onItsWay = Publish(client);
        while (Volatile.Read(ref calls) < 2) { await Task.Delay(10); }
        Assert.IsType<HomeAssistantAuthException>(await Publish(client));
        slow.SetResult();
        Assert.Null(await onItsWay);

        Assert.NotNull(client.Refused);
        Assert.False(Assert.IsType<HomeAssistantAuthException>(await Publish(client)).Sent);
        Assert.Equal(3, calls);
    }

    // The record keeps a bounded number of addresses and tokens, but never by forgetting a refusal.
    [Fact]
    public async Task A_stopped_token_stays_stopped_however_many_other_tokens_and_addresses_come_after_it()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        await Hammer(Client(ha, guard), TimeSpan.FromHours(1));
        Assert.Equal(3, ha.Seen.Count);

        ha.Status = HttpStatusCode.OK;
        for (var i = 0; i < 20; i++)
        {
            Assert.Null(await Publish(Client(ha, guard, $"token-{i}-not-a-secret")));
            Assert.Null(await Publish(new HomeAssistantClient(new Uri($"http://ha{i}.test:8123"), Token + i, new HttpClient(ha), auth: guard)));
            time.Advance(TimeSpan.FromMinutes(1));
        }
        var before = ha.Seen.Count;

        ha.Status = HttpStatusCode.Unauthorized;
        await Hammer(Client(ha, new HaAuthGuard(Record, time)), TimeSpan.FromDays(1));
        Assert.Equal(before, ha.Seen.Count);
    }

    // serve's app shows what its guard says; a refusal shadow run recorded must reach it too, not
    // only stop the connector.
    [Fact]
    public async Task The_app_hears_of_a_refusal_another_process_recorded()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var serveGuard = new HaAuthGuard(Record, time);
        var serve = Client(ha, serveGuard);
        Assert.Null(await Publish(serve));
        var told = 0;
        serveGuard.Changed += () => Interlocked.Increment(ref told);

        ha.Status = HttpStatusCode.Unauthorized;
        await Publish(Client(ha, new HaAuthGuard(Record, time)));
        Assert.NotNull(serve.Refused);

        Assert.True(told > 0);
        Assert.StartsWith("Home Assistant rejected the token", serveGuard.Problem, StringComparison.Ordinal);
    }

    // serve, ha shadow and shadow run are separate processes with a guard each, on one record.
    [Fact]
    public async Task Two_processes_with_a_token_not_yet_accepted_send_one_attempt_between_them()
    {
        var gate = new TaskCompletionSource();
        var seen = 0;
        var handler = new Waiting(async () =>
        {
            Interlocked.Increment(ref seen);
            await gate.Task;
            return HttpStatusCode.Unauthorized;
        });
        var serve = new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: new HaAuthGuard(Record, time));
        var shadow = new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: new HaAuthGuard(Record, time));

        var first = Publish(serve);
        while (Volatile.Read(ref seen) == 0) { await Task.Delay(10); }
        var second = Publish(shadow);
        await Task.Delay(500);
        gate.SetResult();

        Assert.True(Assert.IsType<HomeAssistantAuthException>(await first).Sent);
        Assert.False(Assert.IsType<HomeAssistantAuthException>(await second).Sent);
        Assert.Equal(1, seen);
    }

    sealed class Waiting(Func<Task<HttpStatusCode>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(await answer());
    }

    // The incident itself: the connector's startup, with a token Home Assistant rejects, retried
    // every RetryDelay (30 s) as it always has been for a Home Assistant that is down.
    [Fact]
    public async Task A_connector_started_with_a_rejected_token_tries_three_times_then_stops_and_says_to_run_ha_token_set()
    {
        var ha = new Answering(time);
        var log = new List<string>();
        void Log(string m) { lock (log) { log.Add(m); } }
        var guard = new HaAuthGuard(Record, time, Log);
        await using var c = new HaConnector(new HaConnectorOptions { MachineName = "DSWETT-ARM8", Version = "0.1.914", Dev = true },
            Client(ha, guard), new FakeSessions(), () => null, log: Log, time: time);

        for (var t = TimeSpan.Zero; t < TimeSpan.FromHours(6); t += TimeSpan.FromSeconds(30))
        {
            await c.TickAsync();
            time.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(3, ha.Seen.Count);
        Assert.False(c.Started);
        Assert.Contains(log, l => l.Contains("Home Assistant rejected the token: run `agent-bridge-host ha token set`", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("trying again in 30s", StringComparison.Ordinal));
        // Said when something changes, not on every pass.
        Assert.InRange(log.Count, 3, 10);
        Assert.Contains("Stopped after 3", guard.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connector_whose_Home_Assistant_answers_500_keeps_trying_every_30_seconds()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.InternalServerError };
        var log = new List<string>();
        var guard = new HaAuthGuard(Record, time);
        await using var c = new HaConnector(new HaConnectorOptions { MachineName = "DSWETT-ARM8", Version = "0.1.914", Dev = true },
            Client(ha, guard), new FakeSessions(), () => null, log: m => { lock (log) { log.Add(m); } }, time: time);

        for (var t = TimeSpan.Zero; t < TimeSpan.FromMinutes(10); t += TimeSpan.FromSeconds(30))
        {
            await c.TickAsync();
            time.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(20, ha.Seen.Count);
        Assert.Contains(log, l => l.Contains("trying again in 30s", StringComparison.Ordinal));
        Assert.Null(guard.Problem);
    }
}
