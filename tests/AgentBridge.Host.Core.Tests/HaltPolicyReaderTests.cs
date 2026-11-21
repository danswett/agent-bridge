using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgentBridge.Host.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Core.Tests;

// Synthetic anchor feeds, fixture certificates and an injected clock only: nothing here asks GitHub.
public class HaltPolicyReaderTests
{
    const string Pin = "1.3.6.1.4.1.311.97.123.456.789.1";
    const string Feed = "https://github.com/danswett/agent-bridge/releases/download/v0.1.7300/releases.win.json";
    static readonly string Descriptor = $"{{\"tag\":\"v0.1.7300\",\"channel\":\"win\",\"prerelease\":true,\"feed\":\"{Feed}\",\"sha256\":\"{new string('a', 64)}\",\"size\":1234}}";
    static readonly string Valid = "{\"v\":1,\"repository\":\"danswett/agent-bridge\",\"purpose\":\"update-halt-policy\",\"sequence\":42,"
        + "\"issuedAt\":\"2026-11-21T23:00:00Z\",\"expiresAt\":\"2026-11-22T05:00:00Z\",\"halted\":[\"0.1.7214\"],\"releases\":[" + Descriptor + "]}";

    static byte[] Changed(string replace, string with)
    {
        Assert.Contains(replace, Valid);
        return Encoding.UTF8.GetBytes(Valid.Replace(replace, with));
    }

    [Fact]
    public void A_valid_policy_reads_its_sequence_validity_halts_and_releases()
    {
        var policy = HaltPolicyReader.Parse(Encoding.UTF8.GetBytes(Valid));
        Assert.Equal(42, policy.Sequence);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 23, 0, 0, TimeSpan.Zero), policy.IssuedAt);
        Assert.Equal(policy.IssuedAt.AddHours(6), policy.ExpiresAt);
        Assert.Equal(["0.1.7214"], policy.Halted);
        var release = Assert.Single(policy.Releases);
        Assert.Equal(("v0.1.7300", "win", true, Feed, 1234L), (release.Tag, release.Channel, release.Prerelease, release.Feed, release.Size));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Valid))), policy.BodyHash);
    }

    [Fact]
    public void An_empty_halt_list_is_a_checked_answer_not_a_missing_one() =>
        Assert.Empty(HaltPolicyReader.Parse(Changed("[\"0.1.7214\"]", "[]")).Halted);

    [Theory]
    [InlineData("\"v\":1,", "\"v\":1,\"v\":1,")]
    [InlineData("\"size\":1234", "\"size\":1234,\"size\":1234")]
    [InlineData("\"v\":1,", "\"v\":2,")]
    [InlineData("\"v\":1,", "\"v\":1,\"extra\":0,")]
    [InlineData("\"v\":1,", "\"v\":1,\"x\":[[[[1]]]],")]
    [InlineData("\"repository\":\"danswett/agent-bridge\"", "\"repository\":\"danswett/agent-bridge-fork\"")]
    [InlineData("update-halt-policy", "update-release-list")]
    [InlineData("\"sequence\":42,", "")]
    [InlineData("\"sequence\":42", "\"sequence\":0")]
    [InlineData("\"sequence\":42", "\"sequence\":42.5")]
    [InlineData("\"sequence\":42", "\"sequence\":\"42\"")]
    [InlineData("2026-11-22T05:00:00Z", "2026-11-21T23:00:00Z")]
    [InlineData("2026-11-22T05:00:00Z", "2026-11-22T05:00:01Z")]
    [InlineData("2026-11-21T23:00:00Z", "2026-11-21T15:00:00-08:00")]
    [InlineData("[\"0.1.7214\"]", "[\"0.1.07214\"]")]
    [InlineData("[\"0.1.7214\"]", "[\"0.1.7214\",\"0.1.7214\"]")]
    [InlineData("\"prerelease\":true", "\"prerelease\":\"true\"")]
    [InlineData("\"channel\":\"win\"", "\"channel\":\"linux\"")]
    [InlineData("\"tag\":\"v0.1.7300\"", "\"tag\":\"v0.1.7301\"")]
    [InlineData(Feed, "https://github.com/someone/agent-bridge/releases/download/v0.1.7300/releases.win.json")]
    [InlineData(Feed, Feed + "?x=1")]
    [InlineData(Feed, "https://x@github.com/danswett/agent-bridge/releases/download/v0.1.7300/releases.win.json")]
    [InlineData("\"sha256\":\"a", "\"sha256\":\"A")]
    [InlineData("\"size\":1234", "\"size\":0")]
    public void A_policy_outside_strict_v1_is_unavailable(string replace, string with) =>
        Assert.Throws<HaltPolicyUnavailableException>(() => HaltPolicyReader.Parse(Changed(replace, with)));

    [Fact]
    public void Overflowing_or_conflicting_lists_are_unavailable_never_truncated()
    {
        var halts = string.Join(',', Enumerable.Range(1, 1025).Select(n => $"\"0.1.{n}\""));
        Assert.Throws<HaltPolicyUnavailableException>(() => HaltPolicyReader.Parse(Changed("[\"0.1.7214\"]", $"[{halts}]")));
        Assert.Throws<HaltPolicyUnavailableException>(() => HaltPolicyReader.Parse(Changed(Descriptor, Descriptor + "," + Descriptor)));
        var other = Descriptor.Replace("true", "false").Replace("\"win\"", "\"win-arm64\"").Replace("releases.win.json", "releases.win-arm64.json");
        var five = string.Join(',', Descriptor, other, other.Replace("false", "true"), Descriptor.Replace("true", "false"), Descriptor);
        Assert.Throws<HaltPolicyUnavailableException>(() => HaltPolicyReader.Parse(Changed(Descriptor, five)));
        Assert.Equal(4, HaltPolicyReader.Parse(Changed(Descriptor, five[..five.LastIndexOf(",{", StringComparison.Ordinal)])).Releases.Count);
    }

    static X509Certificate2 Signer(params string[] ekus)
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=Halt Policy Fixture", key, HashAlgorithmName.SHA256);
        var usages = new OidCollection();
        Array.ForEach(ekus, eku => usages.Add(new Oid(eku)));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        return request.CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    static readonly string[] Pinned = [Pin, "1.3.6.1.4.1.311.97.1.0", "1.3.6.1.5.5.7.3.3"];
    static HaltCatalogEvidence Good => new(true, true, true, Signer(Pinned));

    [Fact]
    public void A_trusted_timestamped_catalog_covering_the_policy_with_the_owner_pin_passes() => HaltPolicyReader.RequirePin(Good, Pin);

    [Theory]
    [InlineData(false, true, true, "pin,trust,code")]
    [InlineData(true, false, true, "pin,trust,code")]
    [InlineData(true, true, false, "pin,trust,code")]
    [InlineData(true, true, true, "trust,code")]
    [InlineData(true, true, true, "pin,code")]
    [InlineData(true, true, true, "pin,trust")]
    [InlineData(true, true, true, "other,trust,code")]
    public void A_catalog_short_of_any_requirement_is_unavailable(bool chain, bool timestamped, bool covers, string ekus)
    {
        var map = new Dictionary<string, string> { ["pin"] = Pin, ["trust"] = Pinned[1], ["code"] = Pinned[2], ["other"] = "1.3.6.1.4.1.311.97.9.9.9.9" };
        var evidence = new HaltCatalogEvidence(chain, timestamped, covers, Signer([.. ekus.Split(',').Select(k => map[k])]));
        Assert.Throws<HaltPolicyUnavailableException>(() => HaltPolicyReader.RequirePin(evidence, Pin));
    }

    [Fact]
    public void An_unsigned_catalog_or_a_pin_that_names_no_publisher_is_unavailable()
    {
        Assert.Throws<HaltPolicyUnavailableException>(() => HaltPolicyReader.RequirePin(Good with { Signer = null }, Pin));
        Assert.Throws<HaltPolicyUnavailableException>(() => HaltPolicyReader.RequirePin(new(true, true, true, Signer(Pinned[1], Pinned[2])), Pinned[1]));
        Assert.Throws<HaltPolicyUnavailableException>(() => HaltPolicyReader.RequirePin(Good, ""));
    }

    sealed class Fixed(HaltCatalogEvidence evidence) : IHaltCatalogVerifier
    { public byte[]? Body; public HaltCatalogEvidence Inspect(byte[] catalog, byte[] body) { Body = body; return evidence; } }

    sealed class Anchor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public readonly List<Uri> Asked = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Asked.Add(request.RequestUri!); return answer(request, ct); }
    }

    static Task<HttpResponseMessage> Ok(byte[] bytes) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    static Task<HttpResponseMessage> Redirect(string to) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } });

    [Fact]
    public async Task The_pinned_anchor_pair_is_read_by_browser_download_through_the_asset_store_and_never_the_api()
    {
        var anchor = new Anchor((r, _) => r.RequestUri!.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/x?sig=1&" + r.RequestUri.Segments[^1])
            : Ok(r.RequestUri.Query.EndsWith("json", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(Valid) : [1, 2, 3]));
        var verifier = new Fixed(Good);
        var policy = await HaltPolicyReader.ReadAsync(verifier, Pin, anchor);
        Assert.Equal(42, policy.Sequence);
        Assert.Equal(Encoding.UTF8.GetBytes(Valid), verifier.Body);
        Assert.Equal(new Uri(HaltPolicyReader.JsonUrl), anchor.Asked[0]);
        Assert.Contains(new Uri(HaltPolicyReader.CatalogUrl), anchor.Asked);
        Assert.DoesNotContain(anchor.Asked, u => u.Host == "api.github.com");
    }

    [Theory]
    [InlineData("https://evil.example/halt-policy.json")]
    [InlineData("http://release-assets.githubusercontent.com/halt-policy.json")]
    [InlineData("https://user@release-assets.githubusercontent.com/halt-policy.json")]
    [InlineData("https://api.github.com/repos/danswett/agent-bridge/releases")]
    public async Task A_redirect_off_the_release_asset_store_is_unavailable(string to)
    {
        var anchor = new Anchor((_, _) => Redirect(to));
        await Assert.ThrowsAsync<HaltPolicyUnavailableException>(() => HaltPolicyReader.ReadAsync(new Fixed(Good), Pin, anchor));
        Assert.DoesNotContain(anchor.Asked, u => u.Host != "github.com");
    }

    [Fact]
    public async Task A_missing_asset_or_an_unpinned_catalog_is_unavailable_not_an_empty_policy()
    {
        var missing = new Anchor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HaltPolicyUnavailableException>(() => HaltPolicyReader.ReadAsync(new Fixed(Good), Pin, missing));
        var served = new Anchor((_, _) => Ok(Encoding.UTF8.GetBytes(Valid)));
        await Assert.ThrowsAsync<HaltPolicyUnavailableException>(() => HaltPolicyReader.ReadAsync(new Fixed(Good with { CoversBody = false }), Pin, served));
    }

    // Never ends and never says how long it is: only the streamed cap stops it.
    sealed class Endless : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Zeros());
        sealed class Zeros : MemoryStream { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(buffer.Length); }
    }

    [Theory]
    [InlineData("halt-policy.json")]
    [InlineData("halt-policy.cat")]
    public async Task An_endless_asset_without_a_length_is_cut_off_at_its_cap(string endless)
    {
        var anchor = new Anchor((r, _) => r.RequestUri!.AbsolutePath.EndsWith(endless, StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Endless() })
            : Ok(Encoding.UTF8.GetBytes(Valid)));
        await Assert.ThrowsAsync<HaltPolicyUnavailableException>(() => HaltPolicyReader.ReadAsync(new Fixed(Good), Pin, anchor));
    }

    [Fact]
    public async Task A_download_that_outlasts_its_ten_second_deadline_is_unavailable()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));
        var entered = new TaskCompletionSource();
        var anchor = new Anchor(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
        var read = HaltPolicyReader.ReadAsync(new Fixed(Good), Pin, anchor, time);
        await entered.Task;
        time.Advance(HaltPolicyReader.Deadline - TimeSpan.FromMilliseconds(1));
        Assert.False(read.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<HaltPolicyUnavailableException>(() => read);
    }
}
