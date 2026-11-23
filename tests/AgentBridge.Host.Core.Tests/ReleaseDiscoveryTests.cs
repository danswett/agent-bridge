using System.Globalization;
using System.Net;
using System.Text.Json;
using AgentBridge.Host.Core.Updates;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Release discovery (#530) against a fake GitHub: what each answer does to the next check, and
// that every app and host on a machine shares one budget. Nothing here reaches the network.
public sealed class ReleaseDiscoveryTests : IDisposable
{
    const string Url = "https://api.github.com/repos/danswett/agent-bridge/releases?per_page=10&page=1";
    const string Releases = """[{"name":"Agent Bridge 0.1.2","prerelease":true,"assets":[]}]""";
    static readonly DateTimeOffset Now = new(2026, 11, 4, 20, 0, 0, TimeSpan.Zero);

    readonly string folder = Path.Combine(Path.GetTempPath(), "abrd-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time = new(Now);
    readonly FakeGitHub github = new();

    // Where the words are shown, in a zone that isn't the runner's or Pacific: a half-hour offset,
    // already the next day at Now, so the times can only come from it.
    static readonly TimeZoneInfo Shown = TimeZoneInfo.CreateCustomTimeZone("Test+05:30", new TimeSpan(5, 30, 0), "Test", "Test");

    string Said(ReleaseDiscoveryException e) => e.Describe(Shown, time.GetUtcNow(), CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    ReleaseDiscovery Discovery() => new(folder, github, time, random: () => 0);

    [Fact]
    public async Task Relay_and_shared_cache_reads_preserve_the_last_direct_GitHub_quota_observation()
    {
        var list = $$"""[{"tag_name":"v0.1.2","assets":[{"name":"releases.win.json","browser_download_url":"{{ReleaseDiscovery.DownloadPrefix}}v0.1.2/releases.win.json"}]}]""";
        github.Answer = _ => new(HttpStatusCode.OK) { Content = new StringContent(list), Headers = { { "X-RateLimit-Remaining", "37" } } };
        using (var direct = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: false))
        {
            await direct.ReleasesAsync(Url, CancellationToken.None);
            Assert.Equal((37, Now), (direct.GitHubRemaining, direct.GitHubQuotaAt));
        }
        time.Advance(ReleaseDiscovery.Interval);
        github.Answer = _ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(list),
            Headers = { { "Age", "180" }, { "X-RateLimit-Remaining", "999" } },
        };
        using var relay = Discovery();
        await relay.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        Assert.Equal(("cdn", 37, Now), (relay.Source, relay.GitHubRemaining, relay.GitHubQuotaAt));
        time.Advance(TimeSpan.FromMinutes(1));
        using var cached = Discovery();
        await cached.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        Assert.Equal(("cdn", 37, Now), (cached.Source, cached.GitHubRemaining, cached.GitHubQuotaAt));
        Assert.Equal(time.GetUtcNow() - TimeSpan.FromMinutes(4), cached.FetchedAt);
        Assert.Equal(new[] { Url, ReleaseDiscovery.CdnList }, github.Requests.Select(r => r.Url));
        time.Advance(TimeSpan.FromHours(7));
        github.Answer = r => r.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList
            ? new(HttpStatusCode.OK) { Content = new StringContent(list), Headers = { Age = TimeSpan.Zero } }
            : new(HttpStatusCode.Forbidden) { Headers = { { "X-RateLimit-Remaining", "0" } } };
        using var grace = Discovery();
        await grace.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        Assert.Equal(("cdn", 0, time.GetUtcNow()), (grace.Source, grace.GitHubRemaining, grace.GitHubQuotaAt));
    }

    // #3441: a person's press read the CDN's copy of 37 minutes ago, older than the latest publish.
    [Fact]
    public async Task A_pressed_check_asks_GitHub_past_a_fresh_CDN_copy_while_a_background_check_keeps_the_copy()
    {
        static string List(string version) => $$"""[{"tag_name":"v{{version}}","assets":[{"name":"releases.win.json","browser_download_url":"{{ReleaseDiscovery.DownloadPrefix}}v{{version}}/releases.win.json"}]}]""";
        github.Answer = _ => Ok(List("0.1.2"), "\"v1\"");
        using (var direct = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: false)) { await direct.ReleasesAsync(Url, CancellationToken.None); }
        time.Advance(ReleaseDiscovery.Interval);
        github.Answer = r => r.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList
            ? new(HttpStatusCode.OK) { Content = new StringContent(List("0.1.2")), Headers = { Age = TimeSpan.FromMinutes(37) } }
            : Ok(List("0.1.3"), "\"v2\"");
        using var discovery = Discovery();

        Assert.Equal(List("0.1.2"), await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(List("0.1.2"), await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        Assert.Equal(new[] { Url, ReleaseDiscovery.CdnList }, github.Requests.Select(r => r.Url));

        Assert.Equal(List("0.1.3"), await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win", pressed: true));
        Assert.Equal((Url, (string?)null), github.Requests[^1]);
        Assert.Equal(ReleaseDiscovery.Sources.GitHub, discovery.Source);
    }

    static string ListOf(string version) => $$"""[{"tag_name":"v{{version}}","assets":[{"name":"releases.win.json","browser_download_url":"{{ReleaseDiscovery.DownloadPrefix}}v{{version}}/releases.win.json"}]}]""";

    HttpResponseMessage Cdn(string version, TimeSpan age) => new(HttpStatusCode.OK) { Content = new StringContent(ListOf(version)), Headers = { Age = age } };

    async Task GitHubAnswered(string version = "0.1.2")
    {
        github.Answer = _ => Ok(ListOf(version), "\"v1\"");
        using var direct = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: false);
        await direct.ReleasesAsync(Url, CancellationToken.None);
    }

    // Owner, Nov 22: a person's press reads the CDN first, fresh (at most five minutes old), then GitHub.
    [Fact]
    public async Task A_pressed_check_reads_a_fresh_CDN_copy_first_and_asks_GitHub_nothing()
    {
        await GitHubAnswered();
        time.Advance(TimeSpan.FromMinutes(1));
        github.Answer = r => r.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList ? Cdn("0.1.3", TimeSpan.FromSeconds(60)) : Ok(ListOf("0.1.2"), "\"v2\"");
        using var discovery = Discovery();

        Assert.Equal(ListOf("0.1.3"), await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win", pressed: true));

        Assert.Equal(new[] { Url, ReleaseDiscovery.CdnList }, github.Requests.Select(r => r.Url));
        Assert.Equal(ReleaseDiscovery.Sources.Cdn, discovery.Source);
    }

    [Fact]
    public async Task A_pressed_check_does_not_use_a_CDN_copy_older_than_five_minutes_and_asks_GitHub()
    {
        await GitHubAnswered();
        time.Advance(TimeSpan.FromMinutes(1));
        github.Answer = r => r.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList ? Cdn("0.1.3", TimeSpan.FromMinutes(6)) : Ok(ListOf("0.1.4"), "\"v2\"");
        using var discovery = Discovery();

        Assert.Equal(ListOf("0.1.4"), await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win", pressed: true));

        Assert.Equal(ReleaseDiscovery.Sources.GitHub, discovery.Source);
    }

    [Fact]
    public async Task A_pressed_check_while_GitHub_is_held_re_reads_the_CDN_past_a_kept_CDN_copy()
    {
        await GitHubAnswered();
        time.Advance(ReleaseDiscovery.Interval);
        github.Answer = r => r.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList ? Cdn("0.1.3", TimeSpan.Zero)
            : new(HttpStatusCode.Forbidden) { Headers = { { "X-RateLimit-Remaining", "0" } } };
        using (var held = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: false)) { await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => held.ReleasesAsync(Url, CancellationToken.None)); }
        using var discovery = Discovery();
        Assert.Equal(ListOf("0.1.3"), await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        time.Advance(TimeSpan.FromMinutes(1));
        github.Answer = r => r.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList ? Cdn("0.1.4", TimeSpan.FromSeconds(30))
            : new(HttpStatusCode.Forbidden) { Headers = { { "X-RateLimit-Remaining", "0" } } };

        Assert.Equal(ListOf("0.1.4"), await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win", pressed: true));
    }

    [Fact]
    public async Task A_pressed_check_still_asks_GitHub_first_when_its_revalidation_is_due()
    {
        await GitHubAnswered();
        time.Advance(ReleaseDiscovery.GithubRevalidateEvery + TimeSpan.FromMinutes(1));
        github.Answer = r => r.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList ? Cdn("0.1.3", TimeSpan.Zero) : Ok(ListOf("0.1.4"), "\"v2\"");
        using var discovery = Discovery();

        Assert.Equal(ListOf("0.1.4"), await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win", pressed: true));

        Assert.Equal(Url, github.Requests[^1].Url);
        Assert.Equal(ReleaseDiscovery.Sources.GitHub, discovery.Source);
    }

    [Fact]
    public async Task A_pressed_check_of_a_list_GitHub_gave_asks_again_conditional_on_its_ETag()
    {
        using var discovery = Discovery();
        github.Answer = _ => Ok(Releases, "\"v1\"");
        await discovery.ReleasesAsync(Url, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        github.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotModified) { Headers = { ETag = new("\"v1\"") } };

        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, pressed: true));

        Assert.Equal(2, github.Requests.Count);
        Assert.Equal("\"v1\"", github.Requests[1].IfNoneMatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_pressed_500_keeps_the_fresh_list_without_backoff_and_the_next_background_check_proceeds(bool relay) =>
        PressedTransientFailure(relay, () => new(HttpStatusCode.InternalServerError));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_pressed_network_failure_keeps_the_fresh_list_without_backoff_and_the_next_background_check_proceeds(bool relay) =>
        PressedTransientFailure(relay, () => throw new HttpRequestException("No such host is known."));

    async Task PressedTransientFailure(bool relay, Func<HttpResponseMessage> fail)
    {
        var list = RelayList("0.1.2");
        github.Answer = _ => Ok(list, "\"v1\"");
        using var discovery = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: relay);
        await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        if (relay)
        {
            time.Advance(ReleaseDiscovery.Interval);
            github.Answer = _ => new(HttpStatusCode.OK) { Content = new StringContent(list), Headers = { Age = TimeSpan.FromMinutes(37) } };
            await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        }
        var fetched = discovery.FetchedAt;
        time.Advance(TimeSpan.FromMinutes(1));
        github.Answer = _ => fail();

        Assert.Equal(list, await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win", pressed: true));
        Assert.Equal(fetched, discovery.FetchedAt);
        using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, ReleaseDiscovery.FileName))))
        {
            Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("notBefore").ValueKind);
            Assert.Equal(0, saved.RootElement.GetProperty("failures").GetInt32());
        }
        var requests = github.Requests.Count;
        using var otherProfile = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: relay);
        Assert.Equal(list, await otherProfile.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        Assert.Equal(requests, github.Requests.Count);
        time.Advance(ReleaseDiscovery.Interval);
        github.Answer = _ => new(HttpStatusCode.OK) { Content = new StringContent(list), Headers = { Age = TimeSpan.Zero } };
        Assert.Equal(list, await otherProfile.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        Assert.Equal(requests + 1, github.Requests.Count);
    }

    [Fact]
    public async Task A_pressed_transient_failure_does_not_extend_the_freshness_of_the_kept_list()
    {
        using var discovery = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: false);
        github.Answer = _ => Ok(Releases, "\"v1\"");
        await discovery.ReleasesAsync(Url, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(9));
        github.Answer = _ => new(HttpStatusCode.InternalServerError);
        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, pressed: true));

        time.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(3, github.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_kept_list_that_ages_out_during_a_pressed_transient_failure_is_not_served(bool relay)
    {
        var list = RelayList("0.1.2");
        github.Answer = _ => Ok(list, "\"v1\"");
        using var discovery = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: relay);
        await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        if (relay)
        {
            time.Advance(ReleaseDiscovery.Interval);
            github.Answer = _ => new(HttpStatusCode.OK) { Content = new StringContent(list), Headers = { Age = TimeSpan.FromMinutes(59) } };
            await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        }
        github.Answer = _ =>
        {
            time.Advance(relay ? TimeSpan.FromMinutes(2) : ReleaseDiscovery.Interval);
            return new(HttpStatusCode.InternalServerError);
        };

        var failed = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win", pressed: true));
        Assert.False(failed.Limited);
    }

    [Fact]
    public async Task A_pressed_429_still_backs_off_even_with_a_fresh_kept_list()
    {
        using var discovery = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: false);
        github.Answer = _ => Ok(Releases, "\"v1\"");
        await discovery.ReleasesAsync(Url, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        github.Answer = _ => new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(5)) } };

        var limited = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, b => b, CancellationToken.None, pressed: true));
        Assert.True(limited.Limited);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(5), limited.RetryAt);
        using var otherProfile = new ReleaseDiscovery(folder, github, time, random: () => 0, cdn: false);
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => otherProfile.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_pressed_429_with_a_recent_GitHub_answer_logs_the_relays_grace_window()
    {
        var list = RelayList("0.1.2");
        var log = new List<string>();
        using var discovery = new ReleaseDiscovery(folder, github, time, random: () => 0, log: log.Add);
        github.Answer = _ => Ok(list, "\"v1\"");
        await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        time.Advance(TimeSpan.FromMinutes(1));
        // A CDN copy too old for a press (over five minutes), so GitHub is asked, refuses, and the grace serves it.
        github.Answer = r => r.RequestUri!.AbsoluteUri == Url
            ? new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(5)) } }
            : new(HttpStatusCode.OK) { Content = new StringContent(list), Headers = { Age = TimeSpan.FromMinutes(10) } };

        Assert.Equal(list, await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win", pressed: true));
        Assert.Contains($"update check: using the relay under a grace window ending {(Now + ReleaseDiscovery.GithubRelayAtMost):O}", log);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("unknown")]
    [InlineData("99999999999999999999")]
    public async Task A_missing_or_invalid_quota_is_unknown_not_zero(string? remaining)
    {
        using var discovery = Discovery();
        github.Answer = _ =>
        {
            var answer = Ok(Releases, "\"v1\"");
            if (remaining is not null) { answer.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining); }
            return answer;
        };
        await discovery.ReleasesAsync(Url, CancellationToken.None);
        Assert.Null(discovery.GitHubRemaining);
        Assert.Null(discovery.GitHubQuotaAt);
    }

    [Fact]
    public async Task A_200_is_kept_so_checks_within_the_interval_ask_GitHub_nothing()
    {
        using var discovery = Discovery();
        github.Answer = _ => Ok(Releases, "\"v1\"");

        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));
        time.Advance(ReleaseDiscovery.Interval - TimeSpan.FromSeconds(1));
        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));

        Assert.Single(github.Requests);
        Assert.Null(github.Requests[0].IfNoneMatch);
    }

    [Fact]
    public async Task After_the_interval_the_check_is_conditional_and_a_304_is_answered_from_what_was_kept()
    {
        using var discovery = Discovery();
        github.Answer = _ => Ok(Releases, "\"v1\"");
        await discovery.ReleasesAsync(Url, CancellationToken.None);
        time.Advance(ReleaseDiscovery.Interval);
        github.Answer = _ =>
        {
            var answer = new HttpResponseMessage(HttpStatusCode.NotModified) { Headers = { ETag = new("\"v1\"") } };
            answer.Headers.Add("X-RateLimit-Remaining", "36");
            return answer;
        };

        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal((36, time.GetUtcNow()), (discovery.GitHubRemaining, discovery.GitHubQuotaAt));
        // And the 304 starts a new interval: the next check soon after asks nothing.
        await discovery.ReleasesAsync(Url, CancellationToken.None);

        Assert.Equal(2, github.Requests.Count);
        Assert.Equal("\"v1\"", github.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task A_403_with_the_limit_spent_is_not_asked_again_until_GitHub_says_it_resets()
    {
        using var discovery = Discovery();
        var reset = Now + TimeSpan.FromMinutes(20);
        github.Answer = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Headers = { { "X-RateLimit-Remaining", "0" }, { "X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString() } },
            Content = new StringContent("""{"message":"API rate limit exceeded"}"""),
        };

        var limited = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.True(limited.Limited);
        Assert.Equal(reset, limited.RetryAt);
        Assert.Equal((0, Now), (discovery.GitHubRemaining, discovery.GitHubQuotaAt));
        Assert.Equal($"GitHub is limiting how often this network can check for updates. {Product.Name} will check again after 01:50.", Said(limited));

        // Clicks before the reset are answered at once, with the same words, and ask nothing.
        time.Advance(TimeSpan.FromMinutes(19));
        var again = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(limited.Message, again.Message);
        Assert.Equal((0, Now), (discovery.GitHubRemaining, discovery.GitHubQuotaAt));
        Assert.Single(github.Requests);

        time.Advance(TimeSpan.FromMinutes(1));
        github.Answer = _ => Ok(Releases, "\"v1\"");
        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_429_with_Retry_After_is_not_asked_again_until_then_and_says_when_the_last_good_check_was()
    {
        using var discovery = Discovery();
        github.Answer = _ => Ok(Releases, "\"v1\"");
        await discovery.ReleasesAsync(Url, CancellationToken.None);
        time.Advance(ReleaseDiscovery.Interval);
        github.Answer = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(5)) } };

        var limited = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.True(limited.Limited);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(5), limited.RetryAt);
        // Carried as the instant, and said in the zone it is shown in: 20:00 UTC on 4 November
        // 2026 is 01:30 on the 5th there.
        Assert.Equal(Now, limited.LastSuccess);
        Assert.EndsWith("The last successful check was at 01:30.", Said(limited));

        time.Advance(TimeSpan.FromMinutes(4));
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(2, github.Requests.Count);

        time.Advance(TimeSpan.FromMinutes(1));
        github.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(3, github.Requests.Count);
    }

    [Fact]
    public async Task A_network_failure_is_said_plainly_and_backed_off_doubling_rather_than_thrown_as_is()
    {
        using var discovery = Discovery();
        github.Answer = _ => throw new HttpRequestException("No such host is known.");

        var failed = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.False(failed.Limited);
        Assert.StartsWith("GitHub could not be reached to check for updates.", failed.Message);
        Assert.Equal(Now + ReleaseDiscovery.FirstBackoff, failed.RetryAt);

        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Single(github.Requests);

        time.Advance(ReleaseDiscovery.FirstBackoff);
        var second = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(time.GetUtcNow() + 2 * ReleaseDiscovery.FirstBackoff, second.RetryAt);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_backoff_that_could_not_be_saved_still_holds_this_processs_checks_rather_than_the_older_file()
    {
        using var discovery = Discovery();
        github.Answer = _ => Ok(Releases, "\"v1\"");
        await discovery.ReleasesAsync(Url, CancellationToken.None);
        time.Advance(ReleaseDiscovery.Interval);
        // The file stays readable but can no longer be replaced: where its new copy goes is taken.
        Directory.CreateDirectory(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
        github.Answer = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(30)) } };

        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));

        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_lock_file_that_fails_for_any_reason_but_another_holder_is_not_waited_on()
    {
        using var discovery = Discovery();
        // A disk error (CRC), not a sharing violation: waiting won't make it pass.
        discovery.OpenLock = _ => throw new IOException("Data error (cyclic redundancy check).", unchecked((int)0x80070017));
        github.Answer = _ => Ok(Releases, "\"v1\"");
        var took = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(10), $"took {took.Elapsed}");
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task A_backoff_that_could_not_be_saved_over_a_file_from_a_clock_since_put_back_still_holds_checks()
    {
        // The file, written while the clock was a day ahead, and so stamped later than anything now.
        using (var ahead = new ReleaseDiscovery(folder, github, new FakeTimeProvider(Now.AddDays(1)), random: () => 0))
        {
            github.Answer = _ => Ok(Releases, "\"v1\"");
            await ahead.ReleasesAsync(Url, CancellationToken.None);
        }
        using var discovery = Discovery();
        Directory.CreateDirectory(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
        github.Answer = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(30)) } };

        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));

        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_wait_written_under_a_clock_since_put_back_does_not_hold_checks_for_the_difference()
    {
        // The same folder, written while the clock was a day ahead.
        using (var ahead = new ReleaseDiscovery(folder, github, new FakeTimeProvider(Now.AddDays(1)), random: () => 0))
        {
            github.Answer = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(30)) } };
            await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => ahead.ReleasesAsync(Url, CancellationToken.None));
        }
        using var discovery = Discovery();
        github.Answer = _ => Ok(Releases, "\"v1\"");

        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_list_holding_anything_but_releases_is_an_answer_that_could_not_be_read_not_none_newer()
    {
        using var discovery = Discovery();
        github.Answer = _ => Ok("[null]", "\"v1\"");

        var failed = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.StartsWith("GitHub's answer to the update check could not be read.", failed.Message);
    }

    [Fact]
    public async Task A_release_with_a_field_of_the_wrong_type_is_an_answer_that_could_not_be_read_and_is_not_kept()
    {
        using var discovery = Discovery();
        github.Answer = _ => Ok("""[{"prerelease":{}}]""", "\"v1\"");

        var failed = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.StartsWith("GitHub's answer to the update check could not be read.", failed.Message);
        Assert.Equal(Now + ReleaseDiscovery.FirstBackoff, failed.RetryAt);

        // Backed off, then asked again in full: nothing of that answer was kept.
        time.Advance(ReleaseDiscovery.FirstBackoff);
        github.Answer = _ => Ok(Releases, "\"v2\"");
        Assert.Equal(Releases, await discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.Equal((2, null), (github.Requests.Count, github.Requests[1].IfNoneMatch));
    }

    [Fact]
    public async Task A_refusal_under_a_clock_since_put_back_is_not_answered_from_the_list_before_it()
    {
        // Asked at 19:50 and refused at 20:00, on a clock since put back by a minute.
        using (var before = new ReleaseDiscovery(folder, github, new FakeTimeProvider(Now - ReleaseDiscovery.Interval), random: () => 0))
        {
            github.Answer = _ => Ok(Releases, "\"v1\"");
            await before.ReleasesAsync(Url, CancellationToken.None);
        }
        using (var refused = new ReleaseDiscovery(folder, github, new FakeTimeProvider(Now), random: () => 0))
        {
            github.Answer = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(5)) } };
            await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => refused.ReleasesAsync(Url, CancellationToken.None));
        }
        using var discovery = new ReleaseDiscovery(folder, github, new FakeTimeProvider(Now - TimeSpan.FromMinutes(1)), random: () => 0);

        var held = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, CancellationToken.None));
        Assert.True(held.Limited);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task The_app_and_hosts_sharing_the_apps_folder_make_one_request_between_them_even_checking_at_once()
    {
        // Each process has its own; only the folder is shared.
        using var app = Discovery();
        using var host = Discovery();
        using var other = Discovery();
        var answer = new TaskCompletionSource();
        github.Answer = _ => { answer.Task.Wait(TimeSpan.FromSeconds(10)); return Ok(Releases, "\"v1\""); };

        var checks = new[] { app, host, other, app, host }.Select(d => Task.Run(() => d.ReleasesAsync(Url, CancellationToken.None))).ToArray();
        await Task.Delay(200);
        answer.SetResult();

        Assert.All(await Task.WhenAll(checks), r => Assert.Equal(Releases, r));
        Assert.Single(github.Requests);
        Assert.True(File.Exists(Path.Combine(folder, ReleaseDiscovery.FileName)));
    }

    static HttpResponseMessage Ok(string body, string etag) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body), Headers = { ETag = new(etag) } };

    static string RelayList(string version) =>
        $$"""[{"tag_name":"v{{version}}","assets":[{"name":"releases.win.json","browser_download_url":"{{ReleaseDiscovery.DownloadPrefix}}v{{version}}/releases.win.json"}]}]""";

    [Theory]
    [InlineData(7, HttpStatusCode.Forbidden)]
    [InlineData(7, HttpStatusCode.TooManyRequests)]
    public async Task An_overdue_GitHub_refusal_uses_a_valid_relay_without_renewing_trust_or_backoff(int hours, HttpStatusCode status)
        => await RefusalGraceAsync(hours, status);

    [Fact]
    public async Task A_missing_GitHub_answer_uses_one_bootstrap_window_across_restarts()
        => await RefusalGraceAsync(-1, HttpStatusCode.Forbidden);

    async Task RefusalGraceAsync(int hours, HttpStatusCode status)
    {
        Directory.CreateDirectory(folder);
        var anchor = hours < 0 ? (DateTimeOffset?)null : Now.AddHours(-hours);
        File.WriteAllText(Path.Combine(folder, ReleaseDiscovery.FileName),
            JsonSerializer.Serialize(new { url = Url, gitHubAnsweredAt = anchor, floors = new Dictionary<string, string> { ["win|pre"] = "0.1.1" } }));
        var said = new List<string>();
        var body = RelayList("0.1.2");
        github.Answer = r => r.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList
            ? new(HttpStatusCode.OK) { Content = new StringContent(body), Headers = { Age = TimeSpan.Zero } }
            : new(status) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(30)) } };
        using (var discovery = new ReleaseDiscovery(folder, github, time, random: () => 0, log: said.Add))
        {
            Assert.Equal(body, await discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        }
        Assert.Equal(new[] { Url, ReleaseDiscovery.CdnList }, github.Requests.Select(r => r.Url));
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, ReleaseDiscovery.FileName)));
        Assert.Equal(anchor, saved.RootElement.GetProperty("gitHubAnsweredAt").Deserialize<DateTimeOffset?>());
        Assert.Equal(Now.AddMinutes(30), saved.RootElement.GetProperty("notBefore").GetDateTimeOffset());
        Assert.Equal(1, saved.RootElement.GetProperty("failures").GetInt32());
        var ends = (anchor ?? Now).AddHours(24);
        Assert.Contains(said, line => line.Contains("grace", StringComparison.Ordinal) && line.Contains(ends.ToString("O"), StringComparison.Ordinal));
        using var restarted = new ReleaseDiscovery(folder, github, time, random: () => 0);
        time.Advance(TimeSpan.FromMinutes(10));
        body = RelayList("0.1.999");
        await restarted.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win");
        time.Advance(TimeSpan.FromMinutes(10));
        body = RelayList("0.1.2");
        Assert.Equal(body, await restarted.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        Assert.Single(github.Requests, r => r.Url == Url);
        time.Advance(ends - time.GetUtcNow());
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => restarted.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        time.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => restarted.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        Assert.Equal(3, github.Requests.Count(r => r.Url == ReleaseDiscovery.CdnList));
    }

    [Theory]
    [InlineData(-25, false, false)]
    [InlineData(1, false, false)]
    [InlineData(-25, true, false)]
    [InlineData(1, true, false)]
    [InlineData(-24, false, true)]
    public async Task An_expired_or_future_grace_stays_closed_after_clock_changes(int hours, bool bootstrap, bool crossing)
    {
        Directory.CreateDirectory(folder);
        var anchor = hours > 0 ? Now.AddMinutes(5) : Now.AddHours(hours).AddMinutes(crossing ? 1 : 0);
        File.WriteAllText(Path.Combine(folder, ReleaseDiscovery.FileName),
            JsonSerializer.Serialize(new { url = Url, gitHubAnsweredAt = bootstrap ? (DateTimeOffset?)null : anchor, relayBootstrapAt = bootstrap ? anchor : (DateTimeOffset?)null, notBefore = crossing ? (DateTimeOffset?)null : Now.AddMinutes(30), problem = "limited" }));
        github.Answer = _ =>
        {
            if (crossing) { time.Advance(TimeSpan.FromMinutes(2)); }
            return new HttpResponseMessage(crossing ? HttpStatusCode.InternalServerError : HttpStatusCode.Forbidden);
        };
        using var discovery = Discovery();
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        if (!crossing) { Assert.Empty(github.Requests); }
        time.Advance(TimeSpan.FromMinutes(10));
        if (!crossing) { await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win")); }
        github.Answer = _ => new(HttpStatusCode.Forbidden);
        foreach (var rollback in new[] { 2, 24 })
        {
            using var rolledBack = new ReleaseDiscovery(folder, github, new FakeTimeProvider(Now.AddHours(-rollback)), random: () => 0);
            await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => rolledBack.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        }
        Assert.All(github.Requests, r => Assert.Equal(Url, r.Url));
    }

    [Fact]
    public async Task A_bootstrap_that_cannot_be_saved_does_not_authorize_the_relay()
    {
        Directory.CreateDirectory(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
        github.Answer = _ => new(HttpStatusCode.Forbidden);
        using var discovery = Discovery();
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => discovery.ReleasesAsync(Url, b => b, CancellationToken.None, "0.1.0", "win"));
        Assert.Equal(Url, Assert.Single(github.Requests).Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotModified, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task Only_a_GitHub_success_for_this_list_clears_the_bootstrap_and_renews_its_answer(HttpStatusCode status, bool otherList)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ReleaseDiscovery.FileName),
            JsonSerializer.Serialize(new { url = Url, body = RelayList("0.1.2"), source = "github", gitHubAnsweredAt = Now.AddHours(-7), relayBootstrapAt = Now.AddHours(-25), relayExpired = true }));
        github.Answer = _ => status == HttpStatusCode.OK ? Ok(RelayList("0.1.3"), "\"v3\"") : new(status);
        using var discovery = Discovery();
        await discovery.ReleasesAsync(otherList ? Url + "&other=1" : Url, b => b, CancellationToken.None, "0.1.0", "win");
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, ReleaseDiscovery.FileName)));
        Assert.Equal(otherList ? Now.AddHours(-7) : Now, saved.RootElement.GetProperty("gitHubAnsweredAt").GetDateTimeOffset());
        Assert.Equal(otherList ? Now.AddHours(-25) : (DateTimeOffset?)null, saved.RootElement.GetProperty("relayBootstrapAt").Deserialize<DateTimeOffset?>());
        Assert.Equal(otherList, saved.RootElement.GetProperty("relayExpired").GetBoolean());
    }

    sealed class FakeGitHub : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer = _ => new(HttpStatusCode.InternalServerError);
        public List<(string Url, string? IfNoneMatch)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("If-None-Match", out var v) ? v.Single() : null));
            }
            return Task.FromResult(Answer(request));
        }
    }
}
