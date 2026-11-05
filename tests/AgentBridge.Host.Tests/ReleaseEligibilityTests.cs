using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using Xunit;

public sealed class ReleaseEligibilityTests
{
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
        using var http = new HttpClient(new Handler(uri => uri.AbsoluteUri == GitHubHaltListSource.Releases ? metadata : body));
        var eligibility = new ReleaseEligibility(new GitHubHaltListSource(http));
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
}
