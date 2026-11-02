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

    // A record that cannot be written holds every token not already accepted, rather than letting
    // one try with nothing to remember its refusals by; one already accepted carries on. Once the
    // record can be written again, the rules apply as ever.
    [Fact]
    public async Task A_record_that_cannot_be_written_holds_a_token_not_yet_accepted_until_it_can_be()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        var other = Client(ha, guard, "other-token-not-a-secret");
        Assert.Null(await Publish(other));
        Directory.CreateDirectory(Record + ".tmp");

        ha.Status = HttpStatusCode.Unauthorized;
        var held = Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, guard)));
        Assert.Equal(HaAuthKind.Unrecorded, held.Kind);
        Assert.False(held.Sent);
        ha.Status = HttpStatusCode.OK;
        Assert.Null(await Publish(other));
        Assert.Equal(2, ha.Seen.Count);

        Directory.Delete(Record + ".tmp");
        ha.Status = HttpStatusCode.Unauthorized;
        await Hammer(Client(ha, guard), TimeSpan.FromDays(1));
        Assert.Equal(2 + HaAuthGuard.MaxRejections, ha.Seen.Count);
    }

    // A read that succeeds while writes still fail is no recovery: the record stays unavailable,
    // and a token not yet accepted stays held, until a write does succeed.
    [Fact]
    public async Task A_record_that_can_be_read_but_not_written_stays_unavailable_until_a_write_succeeds()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var said = new List<string>();
        var guard = new HaAuthGuard(Record, time, m => { lock (said) { said.Add(m); } });
        Assert.Null(await Publish(Client(ha, guard, "other-token-not-a-secret")));
        ha.Status = HttpStatusCode.Unauthorized;
        Directory.CreateDirectory(Record + ".tmp");
        var serve = Client(ha, guard);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HaAuthKind.Unrecorded, Assert.IsType<HomeAssistantAuthException>(await Publish(serve)).Kind);
            Assert.NotNull(serve.Refused);
        }

        Assert.DoesNotContain(said, m => m.EndsWith(" again", StringComparison.Ordinal));
        Directory.Delete(Record + ".tmp");
        Assert.Null(serve.Refused);
    }

    // Another process holding the record's lock for good: nothing is written over its record, and
    // nothing is sent that only the record could have stopped.
    [Fact]
    public async Task A_record_that_cannot_be_locked_is_left_alone_and_a_token_not_yet_accepted_is_held()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        Assert.Null(await Publish(Client(ha, guard, "other-token-not-a-secret")));
        var before = File.ReadAllText(Record);
        using (new FileStream(Record + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            ha.Status = HttpStatusCode.Unauthorized;
            await Hammer(Client(ha, guard), TimeSpan.FromMinutes(30));
            Assert.Equal(before, File.ReadAllText(Record));
        }

        Assert.Single(ha.Seen);
    }

    // On Windows a reader's open handle makes replacing the record fail. That once stopped the
    // process recording for good, and a restart then found the refusal gone and the token clear.
    // (Elsewhere the replace succeeds at once; the refusal must survive the restart either way.)
    [Fact]
    public async Task A_refusal_recorded_while_a_reader_has_the_record_open_survives_a_restart()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var serve = Client(ha, new HaAuthGuard(Record, time));
        Assert.Null(await Publish(serve));

        ha.Status = HttpStatusCode.Unauthorized;
        using (new FileStream(Record, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.True(Assert.IsType<HomeAssistantAuthException>(await Publish(serve)).Sent);
        }
        Assert.NotNull(serve.Refused);

        var restarted = new HaAuthGuard(Record, time);
        Assert.StartsWith("Home Assistant rejected the token", restarted.Describe(restarted.Credential(Url, Token)), StringComparison.Ordinal);
    }

    // A refusal that could not be written is kept, other tokens are held meanwhile, and the next
    // request that can write it does, so a restart still finds it.
    [Fact]
    public async Task A_refusal_that_could_not_be_written_is_written_once_the_record_can_be_and_survives_a_restart()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var serve = Client(ha, new HaAuthGuard(Record, time));
        Assert.Null(await Publish(serve));
        Directory.CreateDirectory(Record + ".tmp");

        ha.Status = HttpStatusCode.Unauthorized;
        Assert.True(Assert.IsType<HomeAssistantAuthException>(await Publish(serve)).Sent);
        Assert.Equal(HaAuthKind.Unrecorded, Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, new HaAuthGuard(Record, time), "second-token-not-a-secret"))).Kind);
        Directory.Delete(Record + ".tmp");
        Assert.NotNull(serve.Refused);

        var restarted = new HaAuthGuard(Record, time);
        Assert.StartsWith("Home Assistant rejected the token", restarted.Describe(restarted.Credential(Url, Token)), StringComparison.Ordinal);
        Assert.Equal(2, ha.Seen.Count);
    }

    // The corrupt record stays in place until the record of its loss replaces it: a process that
    // could not write that, and one started after it has gone, still hold every token.
    [Fact]
    public async Task A_corrupt_record_that_cannot_be_replaced_still_holds_tokens_after_a_restart()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        await Hammer(Client(ha, guard), TimeSpan.FromHours(1));
        Assert.Equal(3, ha.Seen.Count);
        File.WriteAllBytes(Record, new byte[64]);
        Directory.CreateDirectory(Record + ".tmp");

        Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, new HaAuthGuard(Record, time))));
        Directory.Delete(Record + ".tmp");
        await Hammer(Client(ha, new HaAuthGuard(Record, time)), TimeSpan.FromHours(1));

        Assert.Equal(3, ha.Seen.Count);
    }

    // A process that still knew a token as accepted when the record was lost does not bring that
    // back: another process may have stopped it in the record that was lost.
    [Fact]
    public async Task A_lost_record_does_not_bring_back_a_token_another_process_stopped()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var serve = Client(ha, new HaAuthGuard(Record, time));
        Assert.Null(await Publish(serve));
        ha.Status = HttpStatusCode.Unauthorized;
        await Hammer(Client(ha, new HaAuthGuard(Record, time)), TimeSpan.FromHours(1));
        var before = ha.Seen.Count;
        File.WriteAllBytes(Record, new byte[64]);

        await Hammer(serve, TimeSpan.FromHours(1));

        Assert.Equal(before, ha.Seen.Count);
        Assert.Equal(HaAuthKind.RecordLost, Assert.IsType<HomeAssistantAuthException>(await Publish(serve)).Kind);
    }

    // An acceptance this process could not write yet when the record was lost is from before the
    // loss: written after it, it must not bring the token back past the hold, here or on restart.
    [Fact]
    public async Task An_acceptance_not_yet_written_when_the_record_is_lost_does_not_survive_the_loss()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        using (var l = await guard.EnterAsync(guard.Credential(Url, "other-token-not-a-secret"))) { l.Answered(HttpStatusCode.OK); }
        var token = guard.Credential(Url, Token);
        var lease = await guard.EnterAsync(token);
        Directory.CreateDirectory(Record + ".tmp");
        lease.Answered(HttpStatusCode.OK);
        lease.Dispose();

        File.WriteAllBytes(Record, new byte[64]);
        Directory.Delete(Record + ".tmp");
        Assert.NotNull(guard.Refused(token));

        Assert.Equal(HaAuthKind.RecordLost, Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, new HaAuthGuard(Record, time)))).Kind);
    }

    // A second loss is a loss of its own: an answer to a request let go between the two does not
    // make the token accepted.
    [Fact]
    public async Task An_answer_to_a_request_let_go_between_two_losses_does_not_accept_the_token()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        var token = guard.Credential(Url, Token);
        using (var l = await guard.EnterAsync(guard.Credential(Url, "other-token-not-a-secret"))) { l.Answered(HttpStatusCode.OK); }
        File.WriteAllBytes(Record, new byte[64]);
        Assert.NotNull(guard.Refused(token));
        Assert.True(guard.Forget(token));

        var between = await guard.EnterAsync(token);
        time.Advance(TimeSpan.FromMinutes(1));
        File.WriteAllBytes(Record, new byte[64]);
        Assert.NotNull(guard.Refused(guard.Credential(Url, "third-token-not-a-secret")));
        between.Answered(HttpStatusCode.OK);
        between.Dispose();

        Assert.Equal(HaAuthKind.RecordLost, Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, new HaAuthGuard(Record, time)))).Kind);
    }

    // Nor when the corrupt record cannot even be set aside: what this process knew before is gone
    // with it, accepted tokens included.
    [Fact]
    public async Task A_corrupt_record_that_cannot_be_set_aside_holds_a_token_this_process_knew_as_accepted()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var serve = Client(ha, new HaAuthGuard(Record, time));
        Assert.Null(await Publish(serve));
        File.WriteAllBytes(Record, new byte[64]);
        Directory.CreateDirectory(Record + HaAuthGuard.CorruptSuffix);

        Assert.False(Assert.IsType<HomeAssistantAuthException>(await Publish(serve)).Sent);
        Assert.Single(ha.Seen);
    }

    // A request let go before the record was lost and answered after: its answer says nothing the
    // lost record could still be held to, so it does not make the token accepted.
    [Fact]
    public async Task An_answer_to_a_request_let_go_before_the_record_was_lost_does_not_accept_the_token()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        var token = guard.Credential(Url, Token);
        using (var l = await guard.EnterAsync(guard.Credential(Url, "other-token-not-a-secret"))) { l.Answered(HttpStatusCode.OK); }

        var early = await guard.EnterAsync(token);
        File.WriteAllBytes(Record, new byte[64]);
        Assert.NotNull(guard.Refused(guard.Credential(Url, "third-token-not-a-secret")));
        early.Answered(HttpStatusCode.OK);
        early.Dispose();

        Assert.Equal(HaAuthKind.RecordLost, Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, new HaAuthGuard(Record, time)))).Kind);
    }

    // A record cut short or zero-filled (a power cut mid-write) is no clean record: it is set
    // aside, and every token waits for a person, here and after a restart. ha token set allows one
    // attempt; deleting the record starts over.
    [Fact]
    public async Task A_corrupt_record_is_set_aside_and_holds_every_token_until_a_person_allows_one()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        foreach (var t in new[] { "a-not-a-secret", "b-not-a-secret", "c-not-a-secret" }) { await Publish(Client(ha, guard, t)); }
        Assert.Equal(3, ha.Seen.Count);
        File.WriteAllBytes(Record, new byte[File.ReadAllBytes(Record).Length]);

        var restarted = new HaAuthGuard(Record, time);
        var serve = Client(ha, restarted);
        var held = Assert.IsType<HomeAssistantAuthException>(await Publish(serve));
        Assert.Equal(HaAuthKind.RecordLost, held.Kind);
        Assert.True(File.Exists(Record + HaAuthGuard.CorruptSuffix));
        Assert.Equal(HaAuthKind.RecordLost, Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, new HaAuthGuard(Record, time), "a-not-a-secret"))).Kind);
        Assert.Equal(3, ha.Seen.Count);

        restarted.Forget(restarted.Credential(Url, Token));
        await Hammer(serve, TimeSpan.FromDays(1));
        Assert.Equal(4, ha.Seen.Count);

        File.Delete(Record);
        await Publish(Client(ha, new HaAuthGuard(Record, time), "d-not-a-secret"));
        Assert.Equal(5, ha.Seen.Count);
    }

    // JSON that parses but is no record (an address with nothing under it, a negative count, a
    // field missing, as a stopped flag would be from a record cut short and patched) is
    // as corrupt as one that does not parse: recording a refusal in it must not fail and leave the
    // token free to be retried.
    [Theory]
    [InlineData("""{ "servers": { "0123456789abcdef": null } }""")]
    [InlineData("""{ "servers": { "0123456789abcdef": { "credentials": null } } }""")]
    [InlineData("""{ "strikes": -100, "servers": {} }""")]
    [InlineData("""{ "servers": null }""")]
    [InlineData("""{}""")]
    [InlineData("""{ "version": 1, "strikes": 0, "strikeAt": null, "refusals": 0, "probeBy": null, "probeUntil": null, "lost": null, "servers": { "0123456789abcdef": { "bannedSince": null, "banLooks": 0, "banCheckAt": null, "touched": null, "credentials": { "fedcba9876543210": {} } } } }""")]
    public async Task A_record_that_parses_but_is_not_one_is_set_aside_and_holds_every_token(string record)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Record, record);
        var ha = new Answering(time);

        await Hammer(Client(ha, new HaAuthGuard(Record, time)), TimeSpan.FromHours(1));

        Assert.Empty(ha.Seen);
        Assert.True(File.Exists(Record + HaAuthGuard.CorruptSuffix));
    }

    // A record that is there but cannot be read now (another program has it open exclusively) is
    // not taken for an empty one: a fresh process sends nothing with a token until it can read it.
    [Fact]
    public async Task An_unreadable_record_holds_a_new_processs_tokens_until_it_can_be_read()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        await Hammer(Client(ha, guard), TimeSpan.FromHours(1));
        Assert.Equal(3, ha.Seen.Count);

        using (new FileStream(Record, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var restarted = Client(ha, new HaAuthGuard(Record, time));
            Assert.Equal(HaAuthKind.Unrecorded, Assert.IsType<HomeAssistantAuthException>(await Publish(restarted)).Kind);
        }

        await Hammer(Client(ha, new HaAuthGuard(Record, time)), TimeSpan.FromDays(1));
        Assert.Equal(3, ha.Seen.Count);
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

    // A WebSocket login resets Home Assistant's count, so the guard's count goes down as well as up:
    // a slow login must not reset it over a refusal recorded after it began, even when the count
    // has meanwhile come back to no more than it saw.
    [Fact]
    public async Task A_slow_login_does_not_reset_the_count_over_a_refusal_recorded_after_it_began()
    {
        var guard = new HaAuthGuard(Record, time);
        var good = guard.Credential(Url, Token);
        using (var l = await guard.EnterAsync(good)) { l.Authenticated(true); }
        async Task Refused(string token)
        {
            using var l = await guard.EnterAsync(guard.Credential(Url, token));
            l.Authenticated(false);
        }
        await Refused("a-not-a-secret");
        await Refused("b-not-a-secret");
        await Refused("c-not-a-secret");

        var slow = await guard.EnterAsync(good);
        using (var l = await guard.EnterAsync(good)) { l.Authenticated(true); }
        await Refused("d-not-a-secret");
        slow.Authenticated(true);
        slow.Dispose();
        await Refused("e-not-a-secret");
        await Refused("f-not-a-secret");

        // d, e and f are three refusals since the last login that could have reset the count.
        Assert.Contains("Stopped", guard.Describe(guard.Credential(Url, "d-not-a-secret")), StringComparison.Ordinal);
    }

    // An answer to one request lets go of its own reservation only: another token's login resetting
    // the count must not open the way for a second process's attempt while the first is out.
    [Fact]
    public async Task Another_tokens_answer_does_not_let_a_second_process_past_an_attempt_still_on_its_way()
    {
        var gate = new TaskCompletionSource();
        var sent = 0;
        var handler = new Waiting(async () =>
        {
            Interlocked.Increment(ref sent);
            await gate.Task;
            return HttpStatusCode.Unauthorized;
        });
        var serve = new HaAuthGuard(Record, time);
        var good = serve.Credential(Url, "good-token-not-a-secret");
        using (var l = await serve.EnterAsync(good)) { l.Authenticated(true); }
        using (var l = await serve.EnterAsync(serve.Credential(Url, "old-token-not-a-secret"))) { l.Authenticated(false); }

        var first = Publish(new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: serve));
        while (Volatile.Read(ref sent) == 0) { await Task.Delay(10); }
        using (var l = await serve.EnterAsync(good)) { l.Authenticated(true); }
        var second = Publish(new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: new HaAuthGuard(Record, time)));
        await Task.Delay(500);
        gate.SetResult();
        await first;
        await second;

        Assert.Equal(1, sent);
    }

    // A 401 from a request let go before a ban was recorded says nothing about the ban.
    [Fact]
    public async Task A_late_401_does_not_clear_a_ban_recorded_after_its_request_was_let_go()
    {
        var guard = new HaAuthGuard(Record, time);
        var token = guard.Credential(Url, Token);
        using (var l = await guard.EnterAsync(token)) { l.Answered(HttpStatusCode.OK); }

        using var late = await guard.EnterAsync(token);
        using (var l = await guard.EnterAsync(token)) { l.Answered(HttpStatusCode.Forbidden); }
        late.Answered(HttpStatusCode.Unauthorized);

        Assert.StartsWith("Home Assistant has banned this machine's address", guard.Describe(token), StringComparison.Ordinal);
    }

    // A record another process wrote within one file-time tick of what was last read, looked at
    // long after: its time says nothing changed, its contents say otherwise.
    [Fact]
    public async Task A_refusal_written_without_moving_the_records_time_is_still_read()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var serve = Client(ha, new HaAuthGuard(Record, time));
        Assert.Null(await Publish(serve));
        var stamp = DateTime.UtcNow.AddSeconds(-30);
        File.SetLastWriteTimeUtc(Record, stamp);
        Assert.Null(await Publish(serve));

        ha.Status = HttpStatusCode.Unauthorized;
        var shadow = Client(ha, new HaAuthGuard(Record, time));
        foreach (var wait in new[] { 5, 15, 0 })
        {
            await Publish(shadow);
            time.Advance(TimeSpan.FromMinutes(wait));
        }
        File.SetLastWriteTimeUtc(Record, stamp);
        var before = ha.Seen.Count;

        Assert.False(Assert.IsType<HomeAssistantAuthException>(await Publish(serve)).Sent);
        Assert.Equal(before, ha.Seen.Count);
    }

    // serve's token finds the ban; shadow run's token then has its one look. The ban is still looked
    // at fifteen minutes on, not an hour, or a cleared ban would keep the machine off for hours.
    [Fact]
    public async Task A_look_with_a_new_token_during_a_ban_does_not_push_the_next_scheduled_look_back()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.Forbidden };
        var guard = new HaAuthGuard(Record, time);
        var serve = Client(ha, guard);
        await Publish(serve);
        await Publish(Client(ha, guard, "second-token-not-a-secret"));
        Assert.Equal(2, ha.Seen.Count);

        await Hammer(serve, TimeSpan.FromMinutes(16));

        Assert.Equal(3, ha.Seen.Count);
    }

    // A token that used up its attempts is not the one to look for the end of a ban with.
    [Fact]
    public async Task A_stopped_token_stays_held_while_another_finds_the_address_banned()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        var stopped = Client(ha, guard);
        await Hammer(stopped, TimeSpan.FromHours(1));
        Assert.Equal(3, ha.Seen.Count);

        ha.Status = HttpStatusCode.Forbidden;
        Assert.Equal(HaAuthKind.Banned, Assert.IsType<HomeAssistantAuthException>(await Publish(Client(ha, guard, "second-token-not-a-secret"))).Kind);
        ha.Status = HttpStatusCode.Unauthorized;
        await Hammer(stopped, TimeSpan.FromDays(1));

        Assert.Equal(4, ha.Seen.Count);
    }

    // The one attempt a change earns, once the count is spent, is one: a login with yet another
    // token resetting the count does not give it more.
    [Fact]
    public async Task A_replacement_token_stopped_after_its_one_attempt_stays_stopped_when_another_token_logs_in()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        await Hammer(Client(ha, guard), TimeSpan.FromHours(1));
        var replacement = Client(ha, guard, "second-token-not-a-secret");
        await Publish(replacement);
        Assert.Equal(4, ha.Seen.Count);

        using (var l = await guard.EnterAsync(guard.Credential(Url, "third-token-not-a-secret"))) { l.Authenticated(true); }
        await Hammer(replacement, TimeSpan.FromDays(1));

        Assert.Equal(4, ha.Seen.Count);
    }

    // Three tokens refused once each spend the machine's three: all three stop, and stay stopped
    // when a fourth logs in and Home Assistant's count, and the guard's, start again.
    [Fact]
    public async Task Tokens_that_spent_the_machines_count_between_them_stay_stopped_after_another_token_logs_in()
    {
        var ha = new Answering(time);
        var guard = new HaAuthGuard(Record, time);
        var tokens = new[] { "a-not-a-secret", "b-not-a-secret", "c-not-a-secret" }.Select(t => Client(ha, guard, t)).ToList();
        foreach (var t in tokens) { await Publish(t); }
        Assert.Equal(3, ha.Seen.Count);

        using (var l = await guard.EnterAsync(guard.Credential(Url, "d-not-a-secret"))) { l.Authenticated(true); }
        foreach (var t in tokens) { await Hammer(t, TimeSpan.FromHours(2)); }

        Assert.Equal(3, ha.Seen.Count);
    }

    // An upgrade answered 200 or 404 never carried the token, so it says nothing of it: the token is
    // still not known to be accepted, and callers with it still go one at a time.
    [Fact]
    public async Task A_websocket_upgrade_that_fails_without_a_refusal_does_not_count_the_token_as_accepted()
    {
        var gate = new TaskCompletionSource();
        var sent = 0;
        var handler = new Waiting(async () =>
        {
            Interlocked.Increment(ref sent);
            await gate.Task;
            return HttpStatusCode.Unauthorized;
        });
        var guard = new HaAuthGuard(Record, time);
        var client = new HomeAssistantClient(Url, Token, new HttpClient(handler),
            (_, _) => throw new System.Net.WebSockets.WebSocketException("The server returned status code '404' when status code '101' was expected."), guard);
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => client.ListEntityRegistryAsync());

        var burst = Enumerable.Range(0, 6).Select(_ => Publish(client)).ToList();
        await Task.Delay(200);
        gate.SetResult();
        await Task.WhenAll(burst);

        Assert.Equal(1, sent);
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

    // A token accepted, then revoked while a burst is on its way: the requests already out are
    // refused one each, so only a couple may be out at once, and none follows the refusals.
    [Fact]
    public async Task A_burst_with_an_accepted_token_revoked_meanwhile_sends_no_more_than_two_before_a_refusal_comes_back()
    {
        var revoked = false;
        var gate = new TaskCompletionSource();
        var sent = 0;
        var handler = new Waiting(async () =>
        {
            Interlocked.Increment(ref sent);
            if (!Volatile.Read(ref revoked)) { return HttpStatusCode.OK; }
            await gate.Task;
            return HttpStatusCode.Unauthorized;
        });
        var client = new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: new HaAuthGuard(Record, time));
        Assert.Null(await Publish(client));
        Volatile.Write(ref revoked, true);

        var burst = Enumerable.Range(0, 10).Select(_ => Publish(client)).ToList();
        await Task.Delay(500);
        Assert.Equal(1 + HaAuthGuard.MaxOutstanding, Volatile.Read(ref sent));
        gate.SetResult();
        await Task.WhenAll(burst);

        Assert.Equal(1 + HaAuthGuard.MaxOutstanding, sent);
    }

    // Home Assistant serves an image without reading the token, so an image found, or not, says
    // nothing of it: callers with a token not yet accepted still go one at a time.
    [Fact]
    public async Task An_image_fetch_answered_without_a_refusal_does_not_count_the_token_as_accepted()
    {
        var gate = new TaskCompletionSource();
        var sent = 0;
        var handler = new Routed(async request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/image/", StringComparison.Ordinal)) { return HttpStatusCode.NotFound; }
            Interlocked.Increment(ref sent);
            await gate.Task;
            return HttpStatusCode.Unauthorized;
        });
        var client = new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: new HaAuthGuard(Record, time));
        Assert.Null(await client.DownloadImageAsync("abc123", 1024));

        var burst = Enumerable.Range(0, 6).Select(_ => Publish(client)).ToList();
        await Task.Delay(300);
        gate.SetResult();
        await Task.WhenAll(burst);

        Assert.Equal(1, sent);
    }

    sealed class Routed(Func<HttpRequestMessage, Task<HttpStatusCode>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(await answer(request));
    }

    // One attempt ends without an answer and another process's takes the reservation over: the
    // process still waiting gives the new one its full time, not what was left of the first's.
    [Fact]
    public async Task A_reservation_handed_on_to_another_attempt_is_waited_for_in_full()
    {
        var sent = 0;
        var handler = new Waiting(() =>
        {
            Interlocked.Increment(ref sent);
            return Task.FromResult(HttpStatusCode.Unauthorized);
        });
        var first = new HaAuthGuard(Record, time);
        using var out1 = await first.EnterAsync(first.Credential(Url, Token));
        var waiter = Publish(new HomeAssistantClient(Url, Token, new HttpClient(handler), auth: new HaAuthGuard(Record, time)));
        await Task.Delay(500);

        time.Advance(TimeSpan.FromSeconds(20));
        HandOn("another-attempt");
        await Task.Delay(500);
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref sent));

        time.Advance(TimeSpan.FromMinutes(1));
        await waiter;
        Assert.Equal(1, sent);
    }

    // Another process's attempt taking over the reservation, as one atomic write of the record.
    void HandOn(string attempt)
    {
        using var held = new FileStream(Record + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Record))!;
        record["probeBy"] = attempt;
        record["probeUntil"] = time.GetUtcNow() + TimeSpan.FromSeconds(45);
        File.WriteAllText(Record + ".test", record.ToJsonString());
        File.Move(Record + ".test", Record, overwrite: true);
    }

    // During a ban each token has one look, then waits for the scheduled one; however many other
    // tokens look after it, the record does not forget it had its look.
    [Fact]
    public async Task A_token_that_looked_during_a_ban_waits_for_the_scheduled_look_however_many_tokens_follow()
    {
        var ha = new Answering(time) { Status = HttpStatusCode.OK };
        var guard = new HaAuthGuard(Record, time);
        var serve = Client(ha, guard);
        Assert.Null(await Publish(serve));
        ha.Status = HttpStatusCode.Forbidden;
        await Publish(serve);
        for (var i = 0; i < 20; i++) { await Publish(Client(ha, guard, $"token-{i}-not-a-secret")); }
        var before = ha.Seen.Count;

        await Hammer(serve, TimeSpan.FromMinutes(10));

        Assert.Equal(before, ha.Seen.Count);
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
