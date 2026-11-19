using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.Host;
using AgentBridge.Host.Core.Updates;
using AgentBridge.Roster;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class SignedHaltPolicyTests : IDisposable
{
    // TEST-ONLY throwaway keys, generated in memory per run: never production keys, never written anywhere.
    static readonly ECDsa TestOnlyKeyA = ECDsa.Create(ECCurve.NamedCurves.nistP256), TestOnlyKeyB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly SignedHaltPolicy Trust = new([Pub(TestOnlyKeyA), Pub(TestOnlyKeyB)]);
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-signed-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
    readonly List<string> logs = [];
    readonly Requests http = new();

    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }
    ReleaseDiscovery Discovery() => new(folder, http, time, random: () => 0, log: logs.Add);
    static byte[] Point(ECDsa key) => key.ExportParameters(false).Q is var q ? [4, .. q.X!, .. q.Y!] : [];
    static string Pub(ECDsa key) => Codec.Base64Url(Point(key));
    static string Z(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    string Payload(long seq, string halted = "\"0.1.7\"", double issuedHours = 0, double lifeDays = 1)
    {
        var issued = time.GetUtcNow().AddHours(issuedHours);
        return $$"""{"v":1,"seq":{{seq}},"issued":"{{Z(issued)}}","expires":"{{Z(issued.AddDays(lifeDays))}}","halts":{"v":1,"halted":[{{halted}}]""" + "}}";
    }

    static byte[] Envelope(ECDsa key, string signed, string? carried = null, string alg = "ES256", byte[]? sig = null, string extra = "")
    {
        var kid = SignedHaltPolicy.KeyId(Point(key));
        var der = P256.Normalize(key.SignData([.. Encoding.UTF8.GetBytes(SignedHaltPolicy.Domain + kid + "\n"), .. Encoding.UTF8.GetBytes(signed)],
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return Encoding.UTF8.GetBytes($$"""{"alg":"{{alg}}","kid":"{{kid}}","payload":"{{Codec.Base64Url(Encoding.UTF8.GetBytes(carried ?? signed))}}","sig":"{{Codec.Base64Url(sig ?? der)}}"{{extra}}}""");
    }

    [Fact]
    public async Task A_signed_policy_survives_a_restart_and_supersedes_the_legacy_GitHub_read()
    {
        using (var first = Discovery())
        {
            var accepted = await Trust.OfferAsync(first, "cdn", Envelope(TestOnlyKeyA, Payload(5)));
            Assert.Equal((true, 5L, "0.1.7"), (accepted.Fresh, accepted.Policy!.Sequence, Assert.Single(accepted.Policy.Halted)));
        }
        using var client = new HttpClient(http);
        using var again = Discovery();
        var eligibility = new ReleaseEligibility(new GitHubHaltListSource(client, again));
        Assert.Equal(new HaltCheck(true), await eligibility.IsHalted("0.1.7"));
        Assert.Equal(new HaltCheck(false), await eligibility.IsHalted("0.1.8"));
        Assert.Equal(0, http.Count);
    }

    [Fact]
    public async Task Every_refusal_keeps_the_stored_policy_and_its_halts_and_logs_its_own_reason()
    {
        using var discovery = Discovery();
        await Trust.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyA, Payload(5)));
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var lift = Payload(6, "");
        (byte[] Envelope, string Reason)[] offers =
        [
            (Encoding.UTF8.GetBytes("""{"v":1,"halted":[]}"""), "unsigned"),
            (Envelope(stranger, lift), "unknown key"),
            (Envelope(TestOnlyKeyA, Payload(6), carried: lift), "bad signature"),
            (Envelope(TestOnlyKeyA, lift, carried: lift.Replace(",", ", ")), "bad signature"), // same JSON, other bytes
            (Envelope(TestOnlyKeyA, lift.Replace("{\"v\":1,\"seq\"", "{\"v\":1,\"seq\":9,\"seq\"")), "malformed"),
            (Envelope(TestOnlyKeyA, lift, alg: "ES384"), "unsupported algorithm"),
            (Envelope(TestOnlyKeyA, lift, sig: TestOnlyKeyA.SignData(Encoding.UTF8.GetBytes(lift), HashAlgorithmName.SHA256)), "malformed signature"),
            (Envelope(TestOnlyKeyA, Payload(6, "", issuedHours: -48)), "expired"),
            (Envelope(TestOnlyKeyA, Payload(6, "", issuedHours: 1)), "not yet valid"),
            (Envelope(TestOnlyKeyA, Payload(6, "", lifeDays: 30)), "lifetime too long"),
            (Envelope(TestOnlyKeyA, lift, extra: ",\"pad\":\"" + new string('x', 100_000) + "\""), "oversize"),
            (Envelope(TestOnlyKeyA, Payload(4, "")), "older sequence"),
            (Envelope(TestOnlyKeyA, lift.Replace("\"seq\":6", "\"seq\":\"6\"")), "malformed"), // signed, but a string seq
            (Envelope(TestOnlyKeyA, Payload(5, "")), "sequence reused"),
        ];
        foreach (var (envelope, reason) in offers)
        {
            var kept = await Trust.OfferAsync(discovery, "relay", envelope);
            Assert.Equal((true, 5L, "0.1.7"), (kept.Fresh, kept.Policy!.Sequence, Assert.Single(kept.Policy.Halted)));
            Assert.Equal($"update policy: signed policy from relay refused ({reason}); stored policy kept", logs[^1]);
        }
        Assert.Equal(offers.Length, logs.Count);
    }

    [Fact]
    public async Task Rotation_accepts_either_pinned_key_and_refuses_a_retired_or_unprovisioned_one()
    {
        using var discovery = Discovery();
        Assert.Equal(5, (await Trust.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyA, Payload(5)))).Policy!.Sequence);
        Assert.Equal(6, (await Trust.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyB, Payload(6)))).Policy!.Sequence);
        Assert.Equal(6, (await new SignedHaltPolicy(["", Pub(TestOnlyKeyB)]).OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyA, Payload(7)))).Policy!.Sequence);
        Assert.Equal(6, (await SignedHaltPolicy.Production.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyB, Payload(8)))).Policy!.Sequence);
        Assert.Equal(2, logs.Count(line => line.EndsWith("(unknown key); stored policy kept", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_replayed_copy_refreshes_until_it_expires_then_stays_stale_with_its_halts_until_a_newer_sequence()
    {
        using var discovery = Discovery();
        var copy = Envelope(TestOnlyKeyA, Payload(5));
        await Trust.OfferAsync(discovery, "cdn", copy);
        time.Advance(TimeSpan.FromHours(7));
        Assert.Equal((false, true), (discovery.HaltPolicy.Fresh, (await Trust.OfferAsync(discovery, "relay", copy)).Fresh));
        time.Advance(TimeSpan.FromHours(18));
        var stale = await Trust.OfferAsync(discovery, "relay", copy);
        Assert.Equal((false, "0.1.7"), (stale.Fresh, Assert.Single(stale.Policy!.Halted)));
        Assert.EndsWith("(expired); stored policy kept", Assert.Single(logs), StringComparison.Ordinal);
        var lifted = await Trust.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyA, Payload(6, "")));
        Assert.Equal((true, 0), (lifted.Fresh, lifted.Policy!.Halted.Length));
    }

    sealed class Requests : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref Count) > 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
    }
}
