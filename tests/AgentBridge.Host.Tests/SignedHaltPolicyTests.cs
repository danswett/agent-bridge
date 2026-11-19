using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
    public async Task A_key_pinned_twice_trusts_no_key_instead_of_throwing()
    {
        using var discovery = Discovery();
        var doubled = new SignedHaltPolicy([Pub(TestOnlyKeyA), Pub(TestOnlyKeyA)]);
        Assert.Null((await doubled.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyA, Payload(5)))).Policy);
        Assert.EndsWith("(unknown key); stored policy kept", Assert.Single(logs), StringComparison.Ordinal);
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

    // What a release before the verifier does to the shared cache: it rewrites it in its own record shape, without seq,
    // expires or kid. It never writes the floor file.
    void LegacyRewrite()
    {
        var path = Path.Combine(folder, ReleaseDiscovery.FileName);
        var state = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var name in new[] { "sequence", "expires", "kid" }) { state["haltPolicy"]!["policy"]!.AsObject().Remove(name); }
        File.WriteAllText(path, state.ToJsonString());
    }

    [Fact]
    public async Task A_rollback_that_rewrites_the_shared_cache_cannot_replay_an_older_sequence_or_reopen_the_legacy_read()
    {
        var older = Envelope(TestOnlyKeyA, Payload(1, ""));
        using (var keyed = Discovery()) { await Trust.OfferAsync(keyed, "cdn", Envelope(TestOnlyKeyA, Payload(2))); }
        LegacyRewrite();
        using var returned = Discovery();
        var replay = await Trust.OfferAsync(returned, "relay", older);
        Assert.Equal("0.1.7", Assert.Single(replay.Policy!.Halted));
        Assert.EndsWith("(older sequence); stored policy kept", logs[^1], StringComparison.Ordinal);
        using var client = new HttpClient(http);
        Assert.Equal(new HaltCheck(true), await new ReleaseEligibility(new GitHubHaltListSource(client, returned)).IsHalted("0.1.7"));
        Assert.Equal(0, http.Count);
    }

    [Fact]
    public async Task A_stolen_key_cannot_jump_its_sequence_past_the_bound_and_the_next_key_alone_recovers_from_its_own_floor()
    {
        using var discovery = Discovery();
        foreach (var jump in new[] { long.MaxValue, ReleaseDiscovery.MaxSequenceJump + 1 })
        {
            Assert.Null((await Trust.OfferAsync(discovery, "relay", Envelope(TestOnlyKeyA, Payload(jump)))).Policy);
            Assert.EndsWith("(sequence jump too large); stored policy kept", logs[^1], StringComparison.Ordinal);
        }
        Assert.Equal(ReleaseDiscovery.MaxSequenceJump, (await Trust.OfferAsync(discovery, "relay", Envelope(TestOnlyKeyA, Payload(ReleaseDiscovery.MaxSequenceJump)))).Policy!.Sequence);
        time.Advance(TimeSpan.FromDays(2));
        using var rebuilt = Discovery();
        var next = await new SignedHaltPolicy(["", Pub(TestOnlyKeyB)]).OfferAsync(rebuilt, "cdn", Envelope(TestOnlyKeyB, Payload(1, "")));
        Assert.Equal((true, 1L, 0), (next.Fresh, next.Policy!.Sequence, next.Policy.Halted.Length));
    }

    [Fact]
    public async Task Accepting_the_next_key_retires_the_previous_one_so_its_unexpired_copies_cannot_be_replayed()
    {
        using var discovery = Discovery();
        var lastOfA = Envelope(TestOnlyKeyA, Payload(5, ""));
        await Trust.OfferAsync(discovery, "cdn", lastOfA);
        Assert.Equal(1, (await Trust.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyB, Payload(1)))).Policy!.Sequence);
        var replay = await Trust.OfferAsync(discovery, "relay", lastOfA);
        Assert.Equal((1L, "0.1.7"), (replay.Policy!.Sequence, Assert.Single(replay.Policy.Halted)));
        Assert.EndsWith("(retired key); stored policy kept", logs[^1], StringComparison.Ordinal);
        Assert.Equal(2, (await Trust.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyB, Payload(2, "")))).Policy!.Sequence);
    }

    [Theory]
    [InlineData("""{"v":1,"keys":[{"kid":""")]
    [InlineData("""{"v":1,"keys":[{"kid":"a","seq":"9"}]}""")]
    [InlineData("[]")]
    public async Task An_unreadable_floor_refuses_every_signed_policy_and_keeps_the_legacy_read_closed(string floor)
    {
        using (var first = Discovery()) { await Trust.OfferAsync(first, "cdn", Envelope(TestOnlyKeyA, Payload(5))); }
        LegacyRewrite();
        File.WriteAllText(Path.Combine(folder, ReleaseDiscovery.FloorFileName), floor);
        using var discovery = Discovery();
        var kept = await Trust.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyA, Payload(6, "")));
        Assert.Equal("0.1.7", Assert.Single(kept.Policy!.Halted));
        Assert.EndsWith("(floor unreadable); stored policy kept", logs[^1], StringComparison.Ordinal);
        using var client = new HttpClient(http);
        await new ReleaseEligibility(new GitHubHaltListSource(client, discovery)).IsHalted("0.1.7");
        Assert.Equal(0, http.Count);
    }

    // A deleted floor file (a local admin) is rebuilt from the signed policy still held, never from zero.
    [Fact]
    public async Task A_deleted_floor_still_refuses_an_older_sequence_of_the_held_policy()
    {
        using var discovery = Discovery();
        await Trust.OfferAsync(discovery, "cdn", Envelope(TestOnlyKeyA, Payload(5)));
        File.Delete(Path.Combine(folder, ReleaseDiscovery.FloorFileName));
        Assert.Equal(5, (await Trust.OfferAsync(discovery, "relay", Envelope(TestOnlyKeyA, Payload(4, "")))).Policy!.Sequence);
        Assert.EndsWith("(older sequence); stored policy kept", logs[^1], StringComparison.Ordinal);
    }

    sealed class Requests : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref Count) > 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
    }
}
