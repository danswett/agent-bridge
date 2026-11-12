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
    public async Task Repeated_policy_checks_share_the_publishers_100_release_window_and_one_GitHub_read()
    {
        var metadataReads = 0;
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var empty = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = new Handler(uri =>
        {
            if (uri.Host != "api.github.com") { return uri.AbsolutePath.Contains("/v0.1.2349/") ? body : empty; }
            metadataReads++;
            var releases = Enumerable.Repeat(Metadata("1", empty), 99).Append(Metadata("2349", body))
                .Select(json => Encoding.UTF8.GetString(json)[1..^1]);
            return Encoding.UTF8.GetBytes("[" + string.Join(",", uri.Query.Contains("per_page=100", StringComparison.Ordinal) ? releases : releases.Take(10)) + "]");
        });
        for (var i = 0; i < 3; i++)
        { Assert.Equal(new HaltCheck(true), await new ReleaseEligibility(Policy(handler)).IsHalted("0.1.2349")); }
        Assert.Equal(1, metadataReads);
    }

    [Fact]
    public async Task An_older_GitHub_answer_cannot_remove_a_previously_verified_halt()
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var run = "2349";
        var source = Policy(new Handler(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases ? Metadata(run, body) : body));
        var eligibility = new ReleaseEligibility(source);
        var first = time.GetUtcNow();
        Assert.Equal(new HaltCheck(true), await eligibility.IsHalted("0.1.2349"));
        run = "2348";
        body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new HaltCheck(true), await eligibility.IsHalted("0.1.2349"));
        Assert.Equal(first, source.Snapshot.At);
        time.Advance(TimeSpan.FromHours(1));
        run = "2349";
        Assert.Equal(new HaltCheck(false), await eligibility.IsHalted("0.1.2349"));
    }

    [Fact]
    public async Task A_clock_correction_rebounds_the_policy_hold_without_trusting_future_freshness()
    {
        var reads = 0;
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = new Handler(uri =>
        {
            if (uri.AbsoluteUri != GitHubHaltListSource.Releases) { return body; }
            reads++;
            return Metadata("2349", body);
        });
        await Policy(handler, new FakeTimeProvider(time.GetUtcNow().AddDays(1))).ReadAsync(default);
        var source = Policy(handler);
        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(default));
        time.Advance(TimeSpan.FromHours(1));
        await source.ReadAsync(default);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task A_failed_policy_commit_retains_the_halt_across_another_writer_and_an_older_answer()
    {
        var failSave = false;
        var run = "2348";
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var handler = new Handler(uri =>
        {
            if (uri.Host == "api.github.com") { return Metadata(run, body); }
            if (failSave) { Directory.CreateDirectory(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp")); }
            return body;
        });
        var source = Policy(handler);
        await source.ReadAsync(default);
        time.Advance(TimeSpan.FromHours(1));
        run = "2349";
        body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        failSave = true;
        Assert.Equal(new HaltCheck(true), await new ReleaseEligibility(source).IsHalted("0.1.2349"));
        Assert.False(source.Snapshot.Fresh);
        Assert.Equal(new HaltCheck(null, ReleaseBlockedReason.HaltListUnavailable), await new ReleaseEligibility(Policy(handler)).IsHalted("0.1.2350"));
        failSave = false;
        Directory.Delete(Path.Combine(folder, ReleaseDiscovery.FileName + ".tmp"));
        time.Advance(TimeSpan.FromMinutes(1));
        using (var writer = new ReleaseDiscovery(folder, handler, time, random: () => 0))
        { await writer.ReleasesAsync(ReleaseDiscovery.GitHubList, default); }
        Assert.Equal(new HaltCheck(true), await new ReleaseEligibility(source).IsHalted("0.1.2349"));
        time.Advance(TimeSpan.FromMinutes(59));
        run = "2348";
        body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        Assert.Equal(new HaltCheck(true), await new ReleaseEligibility(source).IsHalted("0.1.2349"));
        Assert.False(source.Snapshot.Fresh);
    }

    [Fact]
    public async Task A_relay_list_omitting_the_halt_release_cannot_replace_the_shared_policy()
    {
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var feed = $$"""{"tag_name":"v0.1.2348","draft":false,"assets":[{"name":"releases.win.json","browser_download_url":"{{ReleaseDiscovery.DownloadPrefix}}v0.1.2348/releases.win.json"}]}""";
        var direct = Encoding.UTF8.GetString(Metadata("2349", body))[..^1] + "," + feed + "]";
        var relay = "[" + feed + "]";
        var metadataReads = 0;
        var relayReads = 0;
        var handler = new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == ReleaseDiscovery.CdnList)
            {
                relayReads++;
                return new(HttpStatusCode.OK) { Content = new StringContent(relay), Headers = { Age = TimeSpan.Zero } };
            }
            if (request.RequestUri.AbsoluteUri is not (GitHubHaltListSource.Releases or ReleaseDiscovery.GitHubList)) { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }; }
            metadataReads++;
            return new(HttpStatusCode.OK) { Content = new StringContent(direct) };
        });
        await Policy(handler).ReadAsync(default);
        using var discovery = new ReleaseDiscovery(folder, handler, time, random: () => 0);
        await discovery.ReleasesAsync(ReleaseDiscovery.GitHubList, b => b, default, "0.1.1", "win");
        time.Advance(ReleaseDiscovery.Interval);
        Assert.Equal(relay, await discovery.ReleasesAsync(ReleaseDiscovery.GitHubList, b => b, default, "0.1.1", "win"));
        Assert.Equal(new HaltCheck(true), await new ReleaseEligibility(Policy(handler)).IsHalted("0.1.2349"));
        Assert.Equal(1, relayReads);
        Assert.Equal(2, metadataReads);
    }

    [Fact]
    public async Task Conditional_policy_reads_are_hourly_and_a_304_renews_only_verified_policy()
    {
        var reads = 0;
        var assets = 0;
        var answered = time.GetUtcNow();
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}""");
        var source = Policy(new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri != GitHubHaltListSource.Releases)
            {
                assets++;
                time.Advance(TimeSpan.FromMinutes(2));
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            }
            reads++;
            if (reads > 1)
            {
                Assert.Equal("\"policy\"", request.Headers.IfNoneMatch.ToString());
                return new(HttpStatusCode.NotModified);
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata("2349", body)), Headers = { ETag = new("\"policy\"") } };
        }));
        await source.ReadAsync(default);
        Assert.Equal(answered, source.Snapshot.At);
        time.Advance(TimeSpan.FromMinutes(57));
        await source.ReadAsync(default);
        Assert.Equal(1, reads);
        time.Advance(TimeSpan.FromMinutes(1));
        await source.ReadAsync(default);
        Assert.Equal(time.GetUtcNow(), source.Snapshot.At);
        Assert.Equal(2, reads);
        Assert.Equal(1, assets);
    }

    [Fact]
    public async Task Refusals_respect_NotBefore_and_stale_policy_keeps_known_halts_without_becoming_readable()
    {
        var reads = 0;
        var refuse = false;
        var body = Encoding.UTF8.GetBytes("""{"v":1,"halted":["0.1.2349"]}""");
        var source = Policy(new ReplyHandler(request =>
        {
            Assert.NotEqual(new Uri(ReleaseDiscovery.CdnList).Host, request.RequestUri!.Host);
            if (request.RequestUri.AbsoluteUri != GitHubHaltListSource.Releases) { return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }; }
            reads++;
            return refuse ? new(HttpStatusCode.Forbidden) { Headers = { RetryAfter = new(TimeSpan.FromHours(8)) } }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata("2349", body)) };
        }));
        await source.ReadAsync(default);
        time.Advance(TimeSpan.FromHours(1));
        refuse = true;
        await source.ReadAsync(default);
        time.Advance(TimeSpan.FromHours(1));
        await source.ReadAsync(default);
        Assert.Equal(3, reads);
        time.Advance(TimeSpan.FromHours(4));
        var eligibility = new ReleaseEligibility(source);
        Assert.Equal(new HaltCheck(null, ReleaseBlockedReason.HaltListUnavailable), await eligibility.IsHalted("0.1.2350"));
        Assert.Equal(new HaltCheck(true), await eligibility.IsHalted("0.1.2349"));
        Assert.False(source.Snapshot.Fresh);
        Assert.Contains("0.1.2349", source.Snapshot.Policy!.Body);
        Assert.Equal(4, reads);
    }

    static byte[] Metadata(string run, byte[] body) => JsonSerializer.SerializeToUtf8Bytes(new[] {
        new { tag_name = "v0.1." + run, draft = false, assets = new[] { new { name = "halts.json",
                browser_download_url = $"https://github.com/danswett/agent-bridge/releases/download/v0.1.{run}/halts.json",
                size = body.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(body)) } },
        },
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

    sealed class Source(string? json) : IHaltListSource
    {
        public Task<byte[]> ReadAsync(CancellationToken cancellationToken) =>
            json is null ? throw new IOException("unreadable") : Task.FromResult(Encoding.UTF8.GetBytes(json));
    }

    sealed class Handler(Func<Uri, byte[]> read) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(read(request.RequestUri!)) });
    }

    sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> read) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(read(request));
    }
}
