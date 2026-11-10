using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Relay;
using Xunit;

namespace AgentBridge.Transport.Tests;

// Web Push (spike S5): hosts encrypt to the browser, and the relay only signs and forwards.
public class PushTests
{
    [Fact]
    public void RFC_8291_encryption_reproduces_the_RFCs_example_byte_for_byte_and_decrypts()
    {
        var asPub = B64Url.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var asKey = P256KeyPair.FromParameters(B64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"), asPub[1..33], asPub[33..65]);
        var uaPub = B64Url.Decode("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        using var uaKey = P256KeyPair.FromParameters(B64Url.Decode("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94"), uaPub[1..33], uaPub[33..65]);
        var auth = B64Url.Decode("BTBZMqHH6r4Tts7J_aSIgg");
        var plaintext = B64Url.Decode("V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24");
        byte[] expected = [.. B64Url.Decode("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"),
            .. B64Url.Decode("8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ")];

        var body = WebPushCrypto.Encrypt(plaintext, uaPub, auth, asKey, B64Url.Decode("DGv6ra1nlYgDCS1FRnbzlw"));

        Assert.Equal(expected, body);
        Assert.Equal(plaintext, WebPushCrypto.Decrypt(body, uaKey, auth));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_relays_VAPID_header_verifies_against_its_published_key_for_the_endpoints_origin_and_expires_within_the_hour()
    {
        await using var relay = await TestRelay.StartAsync();
        var published = JsonDocument.Parse(await relay.Http.GetStringAsync(new Uri(relay.Url, "/v1/push/key"))).RootElement.GetProperty("key").GetString()!;

        var header = Vapid.FromPkcs8(Convert.ToBase64String(relay.VapidKey.ExportPkcs8PrivateKey()), "https://example.test")
            .Header(new Uri("https://web.push.apple.com/QGuQ"), DateTimeOffset.UtcNow);

        var jwt = header.Split("t=")[1].Split(',')[0];
        var k = header.Split("k=")[1];
        var parts = jwt.Split('.');
        using var verify = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = B64Url.Decode(k)[1..33], Y = B64Url.Decode(k)[33..65] },
        });
        Assert.True(verify.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64Url.Decode(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(published, k);
        var claims = JsonDocument.Parse(B64Url.Decode(parts[1])).RootElement;
        Assert.Equal("https://web.push.apple.com", claims.GetProperty("aud").GetString());
        var exp = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64()) - DateTimeOffset.UtcNow;
        Assert.InRange(exp, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_push_crosses_the_relay_as_ciphertext_with_its_headers()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "pusher", "host");
        using var ua = P256KeyPair.Generate();
        var auth = RandomNumberGenerator.GetBytes(16);
        var secret = "Which database should I use?";
        var topic = PushTopic.From(RandomNumberGenerator.GetBytes(9));
        var endpoint = $"{relay.PushService.Url}/push/phone-1";

        var r = await host.Relay.PushAsync(endpoint, WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(secret), ua.Public, auth), topic: topic);

        Assert.Equal(201, r.Status);
        var push = Assert.Single(relay.PushService.Received);
        Assert.Equal(secret, Encoding.UTF8.GetString(WebPushCrypto.Decrypt(push.Body, ua, auth)));
        Assert.DoesNotContain("database", Encoding.UTF8.GetString(push.Body));
        Assert.Equal(("aes128gcm", "high", "3600", topic), (push.Headers["Content-Encoding"], push.Headers["Urgency"], push.Headers["TTL"], push.Headers["Topic"]));
        Assert.StartsWith("vapid t=", push.Headers["Authorization"]);
        // A push endpoint is a bearer capability: anyone holding it can notify that phone.
        Assert.DoesNotContain(relay.Logs, l => l.Contains(endpoint) || l.Contains(secret));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_relay_refuses_push_endpoints_off_its_allowlist_without_making_a_request()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var c = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "pusher", "host");

        var results = new List<PushResult>();
        foreach (var e in new[] { "http://169.254.169.254/latest/meta-data", "https://evil.example/push", "https://web.push.apple.com.evil.example/x", "ftp://web.push.apple.com/x", "not a url" })
        {
            results.Add(await c.Relay.PushAsync(e, new byte[100]));
        }
        var big = await c.Relay.PushAsync($"{relay.PushService.Url}/push/x", new byte[WebPushCrypto.RecordSize + 1]);

        Assert.All(results, r => Assert.Equal(0, r.Status));
        Assert.All(results, r => Assert.True(r.Error is "bad-endpoint" || r.Error!.StartsWith("endpoint"), r.Error));
        Assert.Equal("bad-body-size", big.Error);
        Assert.Empty(relay.PushService.Received);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_push_topic_Apple_would_refuse_is_dropped_and_the_push_still_goes()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var c = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "topic-pusher", "host");

        // 13 characters is a length no base64 encodes to; Apple answers 400 BadWebPushTopic.
        var bad = await c.Relay.PushAsync($"{relay.PushService.Url}/push/topic", new byte[64], topic: "q7fe69a91e0b1");
        var good = await c.Relay.PushAsync($"{relay.PushService.Url}/push/topic", new byte[64], topic: "q7fe69a91e0b");

        Assert.Equal((201, 201), (bad.Status, good.Status));
        var sent = relay.PushService.Received.ToArray();
        Assert.Equal(2, sent.Length);
        Assert.False(sent[0].Headers.ContainsKey("Topic"));
        Assert.Equal("q7fe69a91e0b", sent[1].Headers["Topic"]);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Pushes_are_rate_limited_per_account_and_a_gone_subscription_is_reported_as_410()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        await using var a = await relay.ConnectAsync(token, "pusher-a", "host");
        await using var b = await relay.ConnectAsync(token, "pusher-b", "host");
        await using var other = await relay.ConnectAsync(relay.TokenFor(2, "mallory"), "other", "host");

        var gone = await a.Relay.PushAsync($"{relay.PushService.Url}/gone/1", new byte[64]);
        var statuses = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            var r = await (i % 2 == 0 ? a : b).Relay.PushAsync($"{relay.PushService.Url}/push/ok", new byte[64]);
            statuses.Add(r.Error ?? r.Status.ToString());
        }
        var otherAccount = await other.Relay.PushAsync($"{relay.PushService.Url}/push/ok", new byte[64]);

        Assert.Equal(410, gone.Status);
        // The limit is 20 a minute for the account, whichever of its devices asks.
        Assert.Equal(Enumerable.Repeat("201", 19).Concat(Enumerable.Repeat("rate-limited", 5)), statuses);
        Assert.Equal(201, otherAccount.Status);
    }

    static PushGateway Gateway(bool key = true, HttpMessageHandler? handler = null)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new RelayOptions { VapidPrivateKey = key ? Convert.ToBase64String(k.ExportPkcs8PrivateKey()) : null };
        return new PushGateway(options, new FakeFactory(handler ?? new CapturingHandler()), Microsoft.Extensions.Logging.Abstractions.NullLogger<PushGateway>.Instance);
    }

    sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Created));
        }
    }

    [Theory]
    [InlineData("https://web.push.apple.com/x", 100, null)]
    [InlineData("https://WEB.PUSH.APPLE.COM/x", 100, null)]
    [InlineData("https://eu.web.push.apple.com/x", 100, null)]
    [InlineData("https://web.push.apple.com/x", WebPushCrypto.RecordSize, null)]
    [InlineData("https://web.push.apple.com/x", 0, "bad-body-size")]
    [InlineData("https://web.push.apple.com/x", WebPushCrypto.RecordSize + 1, "bad-body-size")]
    [InlineData("not a url", 100, "bad-endpoint")]
    [InlineData("/relative/path", 100, "bad-endpoint")]
    [InlineData(null, 100, "bad-endpoint")]
    [InlineData("http://web.push.apple.com/x", 100, "endpoint-not-https")]
    [InlineData("ftp://web.push.apple.com/x", 100, "endpoint-not-https")]
    [InlineData("https://web.push.apple.com:8443/x", 100, "endpoint-port")]
    [InlineData("https://evil.example/push", 100, "endpoint-not-allowed")]
    [InlineData("https://evilweb.push.apple.com/x", 100, "endpoint-not-allowed")]
    [InlineData("https://web.push.apple.com.evil.example/x", 100, "endpoint-not-allowed")]
    [InlineData("https://web.push.apple.com@evil.example/", 100, "endpoint-not-allowed")]
    public void The_gateway_gives_the_exact_reason_for_each_refusal(string? endpoint, int bodyLength, string? reason)
    {
        Assert.Equal(reason, Gateway().Refusal(endpoint, bodyLength));
    }

    [Fact]
    public void Without_a_VAPID_key_every_push_is_refused_as_push_disabled()
    {
        Assert.Equal("push-disabled", Gateway(key: false).Refusal("https://web.push.apple.com/x", 100));
    }

    [Fact]
    public async Task A_body_that_is_not_base64_is_answered_bad_body_without_a_request()
    {
        var handler = new CapturingHandler();

        var r = await Gateway(handler: handler).SendAsync("1", JsonDocument.Parse("""{"endpoint":"https://web.push.apple.com/x","body":"***"}""").RootElement);

        Assert.Equal(new PushGateway.Result(0, "bad-body"), r);
        Assert.Empty(handler.Sent);
    }

    [Theory]
    [InlineData("\"ttl\":-5,\"urgency\":\"high\"", "0", "high")]
    [InlineData("\"ttl\":86400,\"urgency\":\"very-low\"", "86400", "very-low")]
    [InlineData("\"ttl\":86401,\"urgency\":\"low\"", "86400", "low")]
    [InlineData("\"ttl\":\"soon\",\"urgency\":\"urgent\"", "3600", "normal")]
    [InlineData("\"urgency\":\"HIGH\"", "3600", "normal")]
    [InlineData("\"x\":1", "3600", "normal")]
    public async Task A_ttl_outside_its_range_is_clamped_and_an_unknown_urgency_falls_back_to_normal(string extras, string ttl, string urgency)
    {
        var handler = new CapturingHandler();
        var message = "{\"endpoint\":\"https://web.push.apple.com/x\",\"body\":\"AAAA\"," + extras + "}";

        var r = await Gateway(handler: handler).SendAsync("1", JsonDocument.Parse(message).RootElement);

        Assert.Equal(201, r.Status);
        var sent = Assert.Single(handler.Sent);
        Assert.Equal((ttl, urgency), (sent.Headers.GetValues("TTL").Single(), sent.Headers.GetValues("Urgency").Single()));
    }

    [Fact]
    public void A_push_topic_made_from_bytes_is_always_one_a_push_service_accepts()
    {
        for (int n = 1; n <= 24; n++) { Assert.True(PushTopic.IsValid(PushTopic.From(new byte[n])), $"{n} bytes"); }
        Assert.False(PushTopic.IsValid("q7fe69a91e0b1"));
        Assert.False(PushTopic.IsValid(new string('a', 33)));
    }
}
