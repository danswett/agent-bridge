using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class ReleaseEligibilityTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-policy-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
    readonly List<IDisposable> clients = [];
    GitHubHaltListSource Policy(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        var http = new HttpClient(handler);
        var discovery = new ReleaseDiscovery(folder, handler, clock ?? time, random: () => 0);
        clients.AddRange([http, discovery]);
        return new(http, discovery);
    }
    public void Dispose()
    {
        foreach (var client in clients) { client.Dispose(); }
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Repeated_policy_checks_share_the_first_page_of_a_realistic_100_release_repository_and_one_GitHub_read()
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var empty = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = ByteReplies(uri =>
        {
            if (uri.Host != "api.github.com") { return uri.AbsolutePath.Contains("/v0.1.2349/") ? body : empty; }
            // As GitHub lists them, newest first: the highest release, which carries halts.json, is on the first page of ten.
            var releases = new[] { Metadata("2349", body) }.Concat(Enumerable.Repeat(Metadata("1", empty), 99))
                .Select(json => Encoding.UTF8.GetString(json)[1..^1].PadRight(28_350));
            return Encoding.UTF8.GetBytes("[" + string.Join(",", uri.Query.Contains("per_page=100", StringComparison.Ordinal) ? releases : releases.Take(10)) + "]");
        });
        for (var i = 0; i < 3; i++) { Assert.Equal(new HaltCheck(true), await new ReleaseEligibility(Policy(handler)).IsHalted("0.1.2349")); }
        Assert.Equal(1, handler.Requests.Count(uri => uri.Host == "api.github.com"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task Policy_metadata_is_bounded_to_four_MiB_with_or_without_ContentLength(int excess, bool lengthKnown)
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var metadata = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Metadata("2349", body)).PadRight(4 * 1024 * 1024 + excess));
        var handler = new ReplyHandler(request => new(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsoluteUri == GitHubHaltListSource.Releases
                ? new MetadataContent(metadata, lengthKnown) : new ByteArrayContent(body),
        });
        var eligibility = new ReleaseEligibility(Policy(handler));
        Assert.Equal(excess == 0 ? new HaltCheck(false) : new HaltCheck(null, ReleaseBlockedReason.HaltListUnavailable),
            await eligibility.IsHalted("0.1.2349"));
        Assert.Equal(excess == 0 ? 2 : 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_null_or_missing_cached_policy_body_keeps_known_halts_and_blocks_other_versions(bool missing)
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var handler = ByteReplies(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases ? Metadata("2349", body) : body);
        await Policy(handler).ReadAsync(default);
        var path = Path.Combine(folder, ReleaseDiscovery.FileName);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var policy = state["haltPolicy"]!["policy"]!.AsObject();
        if (missing) { policy.Remove("body"); }
        else { policy["body"] = null; }
        File.WriteAllText(path, state.ToJsonString());

        var source = Policy(handler);
        var eligibility = new ReleaseEligibility(source);
        Assert.Equal(new HaltCheck(true), await eligibility.IsHalted("0.1.2349"));
        Assert.Equal(new HaltCheck(null, ReleaseBlockedReason.HaltListUnavailable), await eligibility.IsHalted("0.1.2350"));
        Assert.False(source.Snapshot.Fresh);
        Assert.Equal(new ReleaseEligibilityResult(false, ReleaseBlockedReason.Halted), await eligibility.Evaluate("0.1.2349", "0.1.999"));
        Assert.Equal(new ReleaseEligibilityResult(false, ReleaseBlockedReason.HaltListUnavailable), await eligibility.Evaluate("0.1.2350", "0.1.999"));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clock_correction_rebounds_the_policy_hold_without_trusting_future_freshness(bool legacy)
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = ByteReplies(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases ? Metadata("2349", body) : body);
        await Policy(handler, new FakeTimeProvider(time.GetUtcNow().AddDays(1))).ReadAsync(default);
        if (legacy) { ForgetObservation("attemptedAt"); }
        var source = Policy(handler);
        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(default));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new HaltCheck(false), await new ReleaseEligibility(source).IsHalted("0.1.2349"));
        Assert.Equal(2, handler.Requests.Count(uri => uri.Host == "api.github.com"));
    }

    [Theory]
    [InlineData("2349", false)]
    [InlineData("2348", true)]
    [InlineData("2349", true)]
    public async Task Older_answers_cannot_clear_a_halt_including_a_failed_policy_commit_and_another_writer(string firstRun, bool failedCommit)
    {
        var failSave = false;
        var run = firstRun;
        var body = Encoding.UTF8.GetBytes(failedCommit ? """{"v":1,"halted":[]}""" : """{"v":1,"halted":["0.1.2349"]}""");
        var handler = ByteReplies(uri =>
        {
            if (failSave) { Directory.CreateDirectory(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp")); }
            return uri.Host == "api.github.com" ? Metadata(run, body) : body;
        });
        var source = Policy(handler);
        var eligibility = new ReleaseEligibility(source);
        Assert.Equal(new HaltCheck(!failedCommit), await eligibility.IsHalted("0.1.2349"));
        var first = source.Snapshot.At;
        time.Advance(TimeSpan.FromHours(1));
        using var writer = new ReleaseDiscovery(folder, handler, time, random: () => 0);
        if (failedCommit)
        {
            await FailOrdinaryWrite(writer);
            (run, body, failSave) = ("2349", Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}"""), true);
            Assert.Equal((new HaltCheck(true), false), (await eligibility.IsHalted("0.1.2349"), source.Snapshot.Fresh));
            Assert.Equal(new HaltCheck(null, ReleaseBlockedReason.HaltListUnavailable), await new ReleaseEligibility(Policy(handler)).IsHalted("0.1.2350"));
            failSave = false;
            Directory.Delete(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
            time.Advance(TimeSpan.FromMinutes(1));
            await writer.ReleasesAsync(ReleaseDiscovery.GitHubList, default);
        }
        (run, body) = ("2348", Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}"""));
        Assert.Equal((new HaltCheck(true), failedCommit ? null : first), (await eligibility.IsHalted("0.1.2349"), source.Snapshot.At));
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases));
        time.Advance(failedCommit ? TimeSpan.FromMinutes(59) : TimeSpan.FromHours(1));
        if (!failedCommit) { run = "2349"; }
        Assert.Equal((new HaltCheck(failedCommit), !failedCommit), (await eligibility.IsHalted("0.1.2349"), source.Snapshot.Fresh));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_older_ordinary_writer_cannot_erase_a_same_release_halt_during_a_cancelled_refresh(bool legacy)
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = ByteReplies(uri => uri.Host == "api.github.com" ? Metadata("2349", body) : body);
        await Policy(handler).ReadAsync(default);
        if (legacy) { ForgetObservation(); }
        var oldState = File.ReadAllText(Path.Combine(folder, ReleaseDiscovery.FileName));
        using var writer = new ReleaseDiscovery(folder, handler, time, random: () => 0);
        await writer.ReleasesAsync(ReleaseDiscovery.GitHubList, default);
        time.Advance(TimeSpan.FromHours(1));
        body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        await Policy(handler).ReadAsync(default);
        if (legacy) { ForgetObservation(); }
        time.Advance(TimeSpan.FromHours(1));
        using var refresh = new ReleaseDiscovery(folder, handler, time, random: () => 0);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.ReadHaltPolicyAsync((_, _) =>
        { cancel.Cancel(); return Task.FromCanceled<VerifiedHaltPolicy>(cancel.Token); }, cancel.Token));
        await writer.ReleasesAsync(ReleaseDiscovery.GitHubList, default);
        var eligibility = new ReleaseEligibility(Policy(handler));
        Assert.Equal(new HaltCheck(true), await eligibility.IsHalted("0.1.2349"));
        if (legacy) { File.WriteAllText(Path.Combine(folder, ReleaseDiscovery.FileName), oldState); }
        Assert.Equal(new HaltCheck(true), await eligibility.IsHalted("0.1.2349"));
    }

    async Task FailOrdinaryWrite(ReleaseDiscovery writer)
    {
        await writer.ReleasesAsync(ReleaseDiscovery.GitHubList, default);
        Directory.CreateDirectory(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
        await writer.ReleasesAsync(ReleaseDiscovery.GitHubList + "&alternate=1", default);
        Directory.Delete(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
    }

    void ForgetObservation(string property = "verified")
    {
        var path = Path.Combine(folder, ReleaseDiscovery.FileName);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        state["haltPolicy"]!.AsObject().Remove(property);
        File.WriteAllText(path, state.ToJsonString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_later_relay_or_GitHub_success_keeps_the_failed_refusal_until_expiry_or_clock_correction(bool relayWriter, bool rollback)
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var (refuse, cdnFails) = (false, false);
        var relay = Encoding.UTF8.GetString(Metadata("2348", body, "releases.win.json"));
        var direct = Encoding.UTF8.GetString(Metadata("2349", body))[..^1] + "," + relay[1..];
        var handler = new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList)
            {
                return cdnFails ? new(HttpStatusCode.ServiceUnavailable)
                    : new(HttpStatusCode.OK) { Content = new StringContent(relay), Headers = { Age = TimeSpan.Zero } };
            }
            if (refuse && request.RequestUri.AbsoluteUri == GitHubHaltListSource.Releases)
            {
                Directory.CreateDirectory(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
                return new(HttpStatusCode.Forbidden) { Headers = { RetryAfter = new(TimeSpan.FromHours(1)) } };
            }
            if (request.RequestUri.AbsoluteUri is not (GitHubHaltListSource.Releases or ReleaseDiscovery.GitHubList)) { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }; }
            return new(HttpStatusCode.OK) { Content = new StringContent(direct) };
        });
        await Policy(handler).ReadAsync(default);
        using var discovery = new ReleaseDiscovery(folder, handler, time, random: () => 0);
        await discovery.ReleasesAsync(ReleaseDiscovery.GitHubList, b => b, default, "0.1.1", "win");
        time.Advance(TimeSpan.FromHours(1));
        refuse = true;
        var deadline = time.GetUtcNow().AddMinutes(61);
        using var refuser = new ReleaseDiscovery(folder, handler, time, random: () => 1);
        await refuser.ReadHaltPolicyAsync((_, _) => throw new Xunit.Sdk.XunitException("A refusal must not verify an asset."));
        Directory.Delete(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(relayWriter ? relay : direct, await discovery.ReleasesAsync(ReleaseDiscovery.GitHubList, b => b, default, "0.1.1", relayWriter ? "win" : null));
        Assert.Equal(new HaltCheck(true), await new ReleaseEligibility(Policy(handler)).IsHalted("0.1.2349"));
        var calls = relayWriter ? 3 : 4;
        Assert.Equal((relayWriter ? 1 : 0, calls), (handler.Requests.Count(uri => uri.AbsoluteUri == ReleaseDiscovery.CdnList), handler.Requests.Count(uri => uri.Host == "api.github.com")));
        time.Advance(relayWriter ? ReleaseDiscovery.Interval : TimeSpan.Zero);
        cdnFails = true;
        var held = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => refuser.ReleasesAsync(ReleaseDiscovery.GitHubList, b => b, default, "0.1.1", relayWriter ? "win" : null));
        Assert.Equal((true, deadline, calls), (held.Limited, held.RetryAt, handler.Requests.Count(uri => uri.Host == "api.github.com")));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(deadline, (await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => refuser.ReleasesAsync(ReleaseDiscovery.GitHubList, default))).RetryAt);
        time.AdjustTime(rollback ? time.GetUtcNow().AddHours(-1) : deadline);
        Assert.Equal(direct, await refuser.ReleasesAsync(ReleaseDiscovery.GitHubList, default));
        Assert.Equal(calls + 1, handler.Requests.Count(uri => uri.Host == "api.github.com"));
    }

    [Fact]
    public async Task Conditional_policy_reads_are_hourly_and_a_304_renews_only_verified_policy()
    {
        var answered = time.GetUtcNow();
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri != GitHubHaltListSource.Releases)
            {
                time.Advance(TimeSpan.FromMinutes(2));
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            }
            if (request.Headers.IfNoneMatch.Any())
            {
                Assert.Equal("\"policy\"", request.Headers.IfNoneMatch.ToString());
                return new(HttpStatusCode.NotModified);
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata("2349", body)), Headers = { ETag = new("\"policy\"") } };
        });
        var source = Policy(handler);
        await source.ReadAsync(default);
        Assert.Equal(answered, source.Snapshot.At);
        time.Advance(TimeSpan.FromMinutes(57));
        await source.ReadAsync(default);
        Assert.Equal(1, handler.Requests.Count(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases));
        time.Advance(TimeSpan.FromMinutes(1));
        await source.ReadAsync(default);
        Assert.Equal(time.GetUtcNow(), source.Snapshot.At);
        Assert.Equal((2, 1), (handler.Requests.Count(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases), handler.Requests.Count(uri => uri.Host != "api.github.com")));
    }

    // A positive signal for the fleet: ONE info line on every successful renewal, from GitHub (a new list or a 304) and from the relay,
    // saying where it came from and the time the policy now counts from, so a log read says the policy is being renewed, not only that it is not.
    (GitHubHaltListSource Source, List<string> Logged) PolicyWithLog(HttpMessageHandler handler)
    {
        var logged = new List<string>();
        var http = new HttpClient(handler);
        var discovery = new ReleaseDiscovery(folder, handler, time, random: () => 0, log: logged.Add);
        clients.AddRange([http, discovery]);
        return (new(http, discovery), logged);
    }

    [Fact]
    public async Task A_policy_renewed_from_GitHub_logs_one_line_for_a_new_list_and_one_for_a_304()
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri != GitHubHaltListSource.Releases) { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }; }
            return request.Headers.IfNoneMatch.Any() ? new(HttpStatusCode.NotModified)
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata("2349", body)), Headers = { ETag = new("\"policy\"") } };
        });
        var (source, logged) = PolicyWithLog(handler);

        await source.ReadAsync(default);
        var first = Assert.Single(logged, line => line.StartsWith("update policy: renewed from GitHub", StringComparison.Ordinal));
        Assert.Contains($"At={source.Snapshot.At!.Value:O}", first);
        Assert.Contains("0.1.2349", first);

        time.Advance(TimeSpan.FromHours(1));
        await source.ReadAsync(default);
        var renewals = logged.Where(line => line.StartsWith("update policy: renewed from GitHub", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, renewals.Count);
        Assert.Contains("not modified", renewals[1]);
        Assert.Contains($"At={source.Snapshot.At!.Value:O}", renewals[1]);
    }

    [Fact]
    public async Task A_policy_renewed_from_the_relay_logs_one_line_with_the_time_it_counts_from()
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => TimeSpan.FromMinutes(5));
        await GitHubAnswersTheReleaseList(handler);
        time.Advance(TimeSpan.FromHours(1));
        var (source, logged) = PolicyWithLog(handler);

        await source.ReadAsync(default);

        var line = Assert.Single(logged, l => l.StartsWith("update policy: renewed from the relay's list", StringComparison.Ordinal));
        Assert.Contains($"At={source.Snapshot.At!.Value:O}", line);
    }

    [Fact]
    public async Task A_log_sink_that_throws_never_skips_saving_a_renewed_policy()
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = new ReplyHandler(request => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsoluteUri == GitHubHaltListSource.Releases ? Metadata("2349", body) : body),
        });
        var http = new HttpClient(handler);
        var discovery = new ReleaseDiscovery(folder, handler, time, random: () => 0, log: _ => throw new NotSupportedException("a sink that throws"));
        clients.AddRange([http, discovery]);
        var source = new GitHubHaltListSource(http, discovery);

        await source.ReadAsync(default);

        Assert.True(source.Snapshot.Fresh);
        // Saved: a second reader of the same folder has it at once, without asking GitHub.
        var noRequests = new ReplyHandler(_ => throw new InvalidOperationException("no request was expected"));
        var again = new ReleaseDiscovery(folder, noRequests, time, random: () => 0);
        var second = new HttpClient(noRequests);
        clients.AddRange([second, again]);
        await new GitHubHaltListSource(second, again).ReadAsync(default);
        Assert.Empty(noRequests.Requests);
    }

    [Fact]
    public async Task A_policy_that_is_not_renewed_logs_no_renewal_line()
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => null, policyFailure: () => new(HttpStatusCode.ServiceUnavailable));
        await GitHubAnswersTheReleaseList(handler);
        time.Advance(TimeSpan.FromHours(1));
        var (source, logged) = PolicyWithLog(handler);

        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(default));

        Assert.DoesNotContain(logged, line => line.Contains("renewed from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refusals_respect_NotBefore_and_stale_policy_keeps_known_halts_without_becoming_readable()
    {
        var refuse = false;
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var handler = new ReplyHandler(request =>
        {
            // The relay is down here: a refused policy read finds nothing there and stays unrenewed.
            if (request.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnPolicyList) { return new(HttpStatusCode.ServiceUnavailable); }
            Assert.NotEqual(ReleaseDiscovery.CdnList, request.RequestUri.AbsoluteUri);
            if (request.RequestUri.AbsoluteUri != GitHubHaltListSource.Releases)
            { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.Host == "api.github.com" ? Metadata("2349", body) : body) }; }
            return refuse ? new(HttpStatusCode.Forbidden) { Headers = { RetryAfter = new(TimeSpan.FromHours(8)) } }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata("2349", body)) };
        });
        var source = Policy(handler);
        await source.ReadAsync(default);
        time.Advance(TimeSpan.FromHours(1));
        using var writer = new ReleaseDiscovery(folder, handler, time, random: () => 0);
        await FailOrdinaryWrite(writer);
        refuse = true;
        await source.ReadAsync(default);
        await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => writer.ReleasesAsync(ReleaseDiscovery.GitHubList, default));
        time.Advance(TimeSpan.FromHours(1));
        await source.ReadAsync(default);
        Assert.Equal(3, handler.Requests.Count(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases));
        time.Advance(TimeSpan.FromHours(4));
        var eligibility = new ReleaseEligibility(source);
        Assert.Equal((new HaltCheck(null, ReleaseBlockedReason.HaltListUnavailable), new HaltCheck(true), false),
            (await eligibility.IsHalted("0.1.2350"), await eligibility.IsHalted("0.1.2349"), source.Snapshot.Fresh));
        Assert.Contains("0.1.2349", source.Snapshot.Policy!.Body);
        Assert.Equal(4, handler.Requests.Count(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases));
    }

    static byte[] Metadata(string run, byte[] body, string asset = "halts.json") => JsonSerializer.SerializeToUtf8Bytes(new[] {
        new { tag_name = "v0.1." + run, draft = false, assets = new[] { new { name = asset,
                browser_download_url = $"https://github.com/danswett/agent-bridge/releases/download/v0.1.{run}/{asset}",
                size = body.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(body)) } } },
    });

    [Fact]
    public async Task A_published_empty_halt_list_means_none_are_halted()
    {
        var eligibility = new ReleaseEligibility(new Source("""{"v":1,"halted":[]}"""));
        Assert.Equal(new HaltCheck(false), await eligibility.IsHalted("0.1.2349"));
        Assert.True((await eligibility.Evaluate("0.1.2349", "0.1.999")).Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"v":2,"halted":[]}""")]
    [InlineData("""{"v":1,"halted":["oops"]}""")]
    public async Task Unreadable_missing_or_malformed_halts_is_HaltListUnavailable_not_up_to_date(string? json)
    {
        var eligibility = new ReleaseEligibility(new Source(json));
        Assert.Equal(new HaltCheck(null, ReleaseBlockedReason.HaltListUnavailable), await eligibility.IsHalted("0.1.2349"));
        Assert.Equal(new ReleaseEligibilityResult(false, ReleaseBlockedReason.HaltListUnavailable), await eligibility.Evaluate("0.1.999", "0.1.999"));
    }

    [Fact]
    public async Task A_halted_target_is_refused_even_when_it_is_newer()
    {
        var eligibility = new ReleaseEligibility(new Source("""{"v":1,"halted":["0.1.2349"]}"""));
        Assert.Equal(new HaltCheck(true), await eligibility.IsHalted("0.1.2349"));
        Assert.Equal(new ReleaseEligibilityResult(false, ReleaseBlockedReason.Halted), await eligibility.Evaluate("0.1.2349", "0.1.999"));
    }

    [Theory]
    [InlineData("0.1.2349", "0.1.999", true)]
    [InlineData("0.1.999", "0.1.2349", false)]
    [InlineData("0.1.2349", "0.1.2349", false)]
    [InlineData("0.1.02349", "0.1.999", false)]
    [InlineData("0.2.2349", "0.1.999", false)]
    [InlineData("0.1.2349-beta", "0.1.999", false)]
    public void Versions_are_compared_numerically_and_never_downgrade(string target, string installed, bool expected) =>
        Assert.Equal(expected, ReleaseEligibility.IsNewer(target, installed));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GitHub_policy_bytes_require_the_exact_release_asset_and_matching_digest(bool corruptDigest, bool foreignUrl)
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        const string url = "https://github.com/danswett/agent-bridge/releases/download/v0.1.2349/halts.json";
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                tag_name = "v0.1.2349", draft = false,
                assets = new[] { new { name = "halts.json", browser_download_url = foreignUrl ? "https://example.invalid/halts.json" : url,
                    size = body.Length, digest = "sha256:" + (corruptDigest ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(body))) } },
            },
        });
        var eligibility = new ReleaseEligibility(Policy(new Handler(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases ? metadata : body)));
        var result = await eligibility.IsHalted("0.1.2349");
        Assert.Equal(corruptDigest || foreignUrl ? new HaltCheck(null, ReleaseBlockedReason.HaltListUnavailable) : new HaltCheck(false), result);
    }

    // A refused GitHub policy read renews from the relay's copy of the same list while the release
    // list's relay grace is open (halt policy below; the signed anchor stays the real fix).
    ReplyHandler HeldGitHub(byte[] halts, Func<byte[]> relayList, Func<TimeSpan?> relayAge, Func<bool>? limited = null, string githubRun = "2349", (string Run, byte[] Body)? carrier = null, Func<HttpResponseMessage>? policyFailure = null) => new(request =>
    {
        var uri = request.RequestUri!.AbsoluteUri;
        if (uri == GitHubHaltListSource.Releases)
        {
            if (limited?.Invoke() == false) { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata(githubRun, halts)) }; }
            return policyFailure is { } failure ? failure() : new(HttpStatusCode.Forbidden) { Headers = { RetryAfter = new(TimeSpan.FromHours(1)) } };
        }
        if (uri == ReleaseDiscovery.GitHubList) { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata(githubRun, halts)) }; }
        if (uri == ReleaseDiscovery.CdnPolicyList)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(relayList()) };
            if (relayAge() is { } age) { response.Headers.Age = age; }
            return response;
        }
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(carrier is { } c && uri.Contains($"/v0.1.{c.Run}/") ? c.Body : halts) };
    });

    async Task GitHubAnswersTheReleaseList(ReplyHandler handler)
    {
        using var discovery = new ReleaseDiscovery(folder, handler, time, random: () => 0);
        await discovery.ReleasesAsync(ReleaseDiscovery.GitHubList, b => b, default, "0.1.1", "win");
    }

    [Fact]
    public async Task A_refused_policy_read_renews_from_the_relay_list_while_the_relay_grace_is_open()
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => TimeSpan.FromMinutes(5));
        await GitHubAnswersTheReleaseList(handler);
        time.Advance(TimeSpan.FromHours(1));
        var source = Policy(handler);

        await source.ReadAsync(default);

        Assert.True(source.Snapshot.Fresh);
        Assert.Contains("0.1.2349", source.KnownHalts);
        Assert.Equal(time.GetUtcNow() - TimeSpan.FromMinutes(5), source.Snapshot.At);
        Assert.Equal(1, handler.Requests.Count(uri => uri.AbsoluteUri == ReleaseDiscovery.CdnPolicyList));
    }

    // halts.json is on the newest release, so the policy list is one small page: a hundred releases of a dozen assets each ran
    // past the 4 MiB cap and the read failed outright, on a machine running v0.1.7522.
    [Fact]
    public void The_policy_list_is_the_first_ten_releases_on_GitHub_and_on_the_relay()
    {
        Assert.Equal("https://api.github.com/repos/danswett/agent-bridge/releases?per_page=10", ReleaseDiscovery.HaltPolicyList);
        Assert.Equal("https://releases.slipgates.com/repos/danswett/agent-bridge/releases?per_page=10", ReleaseDiscovery.CdnPolicyList); // page defaults to 1 on the Worker, which keys its cache on both
    }

    [Theory]
    [InlineData("500")]
    [InlineData("502")]
    [InlineData("404")]
    [InlineData("401")]
    [InlineData("timeout")]
    [InlineData("oversize")]
    [InlineData("notalist")]
    public async Task A_policy_read_that_fails_in_any_way_renews_from_the_relay_list_while_the_grace_is_open(string failure)
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        HttpResponseMessage Failed() => failure switch
        {
            "timeout" => throw new HttpRequestException("timed out"),
            "oversize" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]) },
            "notalist" => new(HttpStatusCode.OK) { Content = new StringContent("{}") },
            _ => new((HttpStatusCode)int.Parse(failure)),
        };
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => TimeSpan.FromMinutes(5), policyFailure: Failed);
        await GitHubAnswersTheReleaseList(handler);
        time.Advance(TimeSpan.FromHours(1));
        var source = Policy(handler);

        await source.ReadAsync(default);

        Assert.True(source.Snapshot.Fresh);
        Assert.Contains("0.1.2349", source.KnownHalts);
        Assert.Equal(1, handler.Requests.Count(uri => uri.AbsoluteUri == ReleaseDiscovery.CdnPolicyList));
    }

    [Theory]
    [InlineData("503", "GitHub answered HTTP 503")]
    [InlineData("timeout", "GitHub gave no answer")]
    public async Task An_unrenewed_policy_says_what_GitHub_actually_answered(string failure, string said)
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => null, policyFailure: () => failure == "timeout" ? throw new HttpRequestException("timed out") : new(HttpStatusCode.ServiceUnavailable));
        await GitHubAnswersTheReleaseList(handler);
        time.Advance(TimeSpan.FromHours(1));
        var logged = new List<string>();
        var http = new HttpClient(handler);
        var discovery = new ReleaseDiscovery(folder, handler, time, random: () => 0, log: logged.Add);
        clients.AddRange([http, discovery]);

        await Assert.ThrowsAsync<IOException>(() => new GitHubHaltListSource(http, discovery).ReadAsync(default));

        Assert.Contains(logged, line => line.Contains("stored policy was not renewed") && line.Contains(said));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 25)]
    public async Task A_refused_policy_read_does_not_ask_the_relay_without_an_open_grace(bool githubAnswered, int hoursLater)
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => TimeSpan.Zero);
        if (githubAnswered) { await GitHubAnswersTheReleaseList(handler); }
        time.Advance(TimeSpan.FromHours(hoursLater));
        var source = Policy(handler);

        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(default));

        Assert.False(source.Snapshot.Fresh);
        Assert.Equal(0, handler.Requests.Count(uri => uri.AbsoluteUri == ReleaseDiscovery.CdnPolicyList));
    }

    [Theory]
    [InlineData(61, "age")]
    [InlineData(-1, "no age")]
    public async Task A_relay_policy_list_with_an_old_or_unknown_age_is_not_used(int minutes, string why)
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => minutes < 0 ? null : TimeSpan.FromMinutes(minutes));
        await GitHubAnswersTheReleaseList(handler);
        time.Advance(TimeSpan.FromHours(1));
        var source = Policy(handler);

        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(default));

        Assert.False(source.Snapshot.Fresh, why);
    }

    [Fact]
    public async Task A_relay_policy_older_than_the_known_one_or_failing_its_hash_renews_nothing_and_keeps_known_halts()
    {
        var known = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2350"]}""");
        var older = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var (relay, limited) = (Metadata("2349", older), false);
        var handler = HeldGitHub(known, () => relay, () => TimeSpan.Zero, () => limited, "2350", ("2349", older));
        await GitHubAnswersTheReleaseList(handler);
        var source = Policy(handler);
        await source.ReadAsync(default);
        time.Advance(TimeSpan.FromHours(7));
        limited = true;

        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(default));
        Assert.Contains("0.1.2350", source.KnownHalts);
        Assert.False(source.Snapshot.Fresh);

        // A newer release whose asset bytes do not match its listed digest: the hash fails.
        relay = Metadata("2351", Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}  """));
        time.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(default));
        Assert.Contains("0.1.2350", source.KnownHalts);
        Assert.False(source.Snapshot.Fresh);
    }

    [Fact]
    public async Task A_later_GitHub_answer_replaces_a_relay_renewal()
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");

        var limited = true;
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => TimeSpan.Zero, () => limited, "2352");
        await GitHubAnswersTheReleaseList(handler);
        time.Advance(TimeSpan.FromHours(1));
        var source = Policy(handler);
        await source.ReadAsync(default);
        Assert.Contains("0.1.2349", source.KnownHalts);

        limited = false;
        time.Advance(TimeSpan.FromHours(1));
        await source.ReadAsync(default);

        Assert.Equal(time.GetUtcNow(), source.Snapshot.At);
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases));
    }

    [Fact]
    public async Task A_relay_renewal_is_never_fresh_past_the_end_of_the_relay_grace()
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => TimeSpan.Zero);
        await GitHubAnswersTheReleaseList(handler);
        var graceEnd = time.GetUtcNow() + ReleaseDiscovery.GithubRelayAtMost;
        time.Advance(TimeSpan.FromHours(20));
        var source = Policy(handler);

        await source.ReadAsync(default);

        // Stamped so that it is stale a revalidation period before the grace ends, not six hours after the read.
        Assert.Equal(graceEnd - ReleaseDiscovery.GithubRevalidateEvery, source.Snapshot.At);
        Assert.True(source.Snapshot.Fresh);
        time.AdjustTime(graceEnd);
        Assert.False(source.Snapshot.Fresh);
        Assert.Contains("0.1.2349", source.KnownHalts);
    }

    // Not stale on arrival: a renewal is stamped graceEnd minus six hours, so one that arrives at any time inside the grace is
    // fresh from then until the grace ends, including one arriving after the eighteenth hour.
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(23)]
    public async Task A_relay_renewal_arriving_inside_the_grace_is_fresh_until_the_grace_ends(int hoursAfterGitHubAnswered)
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => TimeSpan.Zero);
        await GitHubAnswersTheReleaseList(handler);
        var graceEnd = time.GetUtcNow() + ReleaseDiscovery.GithubRelayAtMost;
        time.Advance(TimeSpan.FromHours(hoursAfterGitHubAnswered));
        var source = Policy(handler);

        var arrived = time.GetUtcNow();
        await source.ReadAsync(default);

        // Fresh for the six hours, or until the grace ends if that comes first: never stale on arrival.
        var staleAt = arrived + ReleaseDiscovery.GithubRevalidateEvery < graceEnd ? arrived + ReleaseDiscovery.GithubRevalidateEvery : graceEnd;
        Assert.True(source.Snapshot.Fresh);
        time.AdjustTime(staleAt - TimeSpan.FromMinutes(1));
        Assert.True(source.Snapshot.Fresh);
        time.AdjustTime(staleAt);
        Assert.False(source.Snapshot.Fresh);
    }

    [Fact]
    public async Task A_relay_policy_that_omits_a_known_halt_renews_nothing_and_never_clears_it()
    {
        var known = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2350"]}""");
        var omits = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var limited = false;
        var handler = HeldGitHub(known, () => Metadata("2351", omits), () => TimeSpan.Zero, () => limited, "2350", ("2351", omits));
        await GitHubAnswersTheReleaseList(handler);
        var source = Policy(handler);
        await source.ReadAsync(default);
        time.Advance(TimeSpan.FromHours(7));
        limited = true;

        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(default));

        Assert.Equal(2350, source.Snapshot.Policy!.Release);
        Assert.Contains("0.1.2350", source.KnownHalts);
        Assert.Contains("0.1.2350", source.Snapshot.Policy.Body);
        Assert.False(source.Snapshot.Fresh);
    }

    [Fact]
    public async Task The_relay_is_not_asked_again_while_the_refusal_hold_lasts()
    {
        var halts = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = HeldGitHub(halts, () => Metadata("2349", halts), () => TimeSpan.Zero);
        await GitHubAnswersTheReleaseList(handler);
        var source = Policy(handler);
        await source.ReadAsync(default);
        time.Advance(TimeSpan.FromMinutes(10));

        await source.ReadAsync(default);

        Assert.Equal((1, 1), (handler.Requests.Count(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases),
            handler.Requests.Count(uri => uri.AbsoluteUri == ReleaseDiscovery.CdnPolicyList)));
    }

    sealed class Source(string? json) : IHaltListSource
    {
        public Task<byte[]> ReadAsync(CancellationToken cancellationToken) =>
            json is null ? throw new IOException("unreadable") : Task.FromResult(Encoding.UTF8.GetBytes(json));
    }

    static ReplyHandler ByteReplies(Func<Uri, byte[]> read) => new(request => new(HttpStatusCode.OK) { Content = new ByteArrayContent(read(request.RequestUri!)) });

    sealed class MetadataContent(byte[] bytes, bool lengthKnown) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return lengthKnown; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    sealed class Handler(Func<Uri, byte[]> read) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(read(request.RequestUri!)) });
    }

    sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> read) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(read(request));
        }
    }
}
