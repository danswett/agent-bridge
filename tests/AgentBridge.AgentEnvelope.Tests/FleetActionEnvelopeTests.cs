using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetActionEnvelopeTests : IDisposable
{
    const string Source = "0123456789abcdef0123456789abcdef";
    const string Audience = "fedcba9876543210fedcba9876543210";
    static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    byte[] Point
    {
        get
        {
            var q = key.ExportParameters(false).Q;
            return [4, .. q.X!, .. q.Y!];
        }
    }
    public void Dispose() => key.Dispose();

    static FleetActionBody Body(Action<FleetActionBody>? _ = null) => new("account", Source, "subject-1", Audience,
        "fleet.launch", "grant-1", 3, Guid.NewGuid(), FleetActionEnvelope.NewNonce(), Now.AddSeconds(60),
        FleetActionEnvelope.PayloadSha256("{\"name\":\"SG Builder\"}"u8));
    FleetActionSigned Seal(FleetActionBody body) => FleetActionEnvelope.Seal(body, new EcdsaEnvelopeSigner(key));
    FleetActionBody Verify(FleetActionSigned s, string audience = Audience, DateTimeOffset? now = null) =>
        FleetActionEnvelope.Verify(s, Point, "account", Source, audience, now ?? Now);

    [Fact]
    public void A_sealed_launch_envelope_verifies_to_exactly_its_fields()
    {
        var body = Body();
        Assert.Equal(body, Verify(Seal(body)));
    }

    [Fact]
    public void Wrong_audience_account_or_signer_refuses()
    {
        var signed = Seal(Body());
        Assert.Throws<InvalidDataException>(() => Verify(signed, "00000000000000000000000000000000"));
        Assert.Throws<InvalidDataException>(() => FleetActionEnvelope.Verify(signed, Point, "other", Source, Audience, Now));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = other.ExportParameters(false).Q;
        Assert.Throws<InvalidDataException>(() =>
            FleetActionEnvelope.Verify(signed, [4, .. q.X!, .. q.Y!], "account", Source, Audience, Now));
    }

    [Fact]
    public void Expired_and_too_long_lived_envelopes_refuse()
    {
        var signed = Seal(Body());
        Assert.Throws<InvalidDataException>(() => Verify(signed, now: Now.AddSeconds(60)));
        var long_ = Seal(Body() with { ExpiresAt = Now.AddSeconds(121) });
        Assert.Throws<InvalidDataException>(() => Verify(long_));
        Assert.Equal(Now.AddSeconds(120), Verify(Seal(Body() with { ExpiresAt = Now.AddSeconds(120) })).ExpiresAt);
    }

    [Theory]
    [InlineData("fleet.end")]
    [InlineData("grant.read")]
    [InlineData("")]
    public void Only_the_launch_action_is_accepted_in_g9(string action) =>
        Assert.Throws<InvalidDataException>(() => Verify(Seal(Body() with { Action = action })));

    [Fact]
    public void A_tampered_body_or_signature_refuses()
    {
        var signed = Seal(Body());
        var bytes = Codec.TryBase64Url(signed.Body, out var raw) ? raw : throw new InvalidOperationException();
        raw[^3] ^= 1;
        Assert.Throws<InvalidDataException>(() => Verify(signed with { Body = Codec.Base64Url(raw) }));
        var sig = Codec.TryBase64Url(signed.Signature, out var s) ? s : throw new InvalidOperationException();
        s[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => Verify(signed with { Signature = Codec.Base64Url(s) }));
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void A_signed_body_with_an_extra_or_duplicate_property_refuses()
    {
        var json = Encoding.UTF8.GetString(Codec.TryBase64Url(Seal(Body()).Body, out var b) ? b : []);
        var extra = json.TrimEnd('}') + ",\"x\":1}";
        Assert.Throws<InvalidDataException>(() => FleetActionEnvelope.Verify(
            FleetActionEnvelope.SealRaw(Encoding.UTF8.GetBytes(extra), new EcdsaEnvelopeSigner(key)),
            Point, "account", Source, Audience, Now));
    }

    [Theory]
    [InlineData("subject-1", "", true)]
    [InlineData("", "grant-1", true)]
    public void Empty_required_fields_refuse(string subject, string grant, bool _) =>
        Assert.Throws<ArgumentException>(() => Seal(Body() with { Subject = subject, GrantId = grant }));

    [Fact]
    public void Verify_binds_the_source_device_and_a_zero_generation_is_valid()
    {
        var signed = Seal(Body() with { Generation = 0 });
        Assert.Equal(0, Verify(signed).Generation);
        Assert.Throws<InvalidDataException>(() => FleetActionEnvelope.Verify(signed, Point, "account", Audience, Audience, Now));
        Assert.Throws<ArgumentException>(() => Seal(Body() with { Generation = -1 }));
    }

    [Fact]
    public void A_restarted_cache_accepts_nothing_until_every_earlier_envelope_has_expired()
    {
        var cache = new FleetActionReplayCache(4, TimeSpan.FromSeconds(30), Now);
        var body = Body() with { ExpiresAt = Now.AddSeconds(60) };
        Assert.False(cache.TryAdd(body, Now));
        Assert.False(cache.TryAdd(Body() with { ExpiresAt = Now.AddSeconds(200) }, Now.AddSeconds(149)));
        Assert.True(cache.TryAdd(Body() with { ExpiresAt = Now.AddSeconds(200) }, Now.AddSeconds(150)));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(65537, 30)]
    [InlineData(4, -1)]
    [InlineData(4, 301)]
    public void The_replay_cache_refuses_unbounded_or_negative_settings(int capacity, int skewSeconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FleetActionReplayCache(capacity, TimeSpan.FromSeconds(skewSeconds), Now));

    [Fact]
    public void The_replay_cache_refuses_an_expiry_that_is_past_or_longer_than_the_envelope_lifetime()
    {
        var cache = new FleetActionReplayCache(4, TimeSpan.FromSeconds(30), Now.AddMinutes(-10));
        Assert.False(cache.TryAdd(Body() with { ExpiresAt = Now }, Now));
        Assert.False(cache.TryAdd(Body() with { ExpiresAt = Now.AddSeconds(121) }, Now));
        Assert.True(cache.TryAdd(Body() with { ExpiresAt = Now.AddSeconds(120) }, Now));
    }

    [Fact]
    public void The_payload_hash_is_base64url_sha256_of_the_canonical_bytes()
    {
        var hash = FleetActionEnvelope.PayloadSha256("abc"u8);
        Assert.Equal(Codec.Base64Url(SHA256.HashData("abc"u8)), hash);
    }

    [Fact]
    public void Replay_is_refused_per_source_and_nonce_until_expiry_plus_skew()
    {
        var cache = new FleetActionReplayCache(capacity: 4, skew: TimeSpan.FromSeconds(30), Now.AddMinutes(-10));
        var body = Body();
        Assert.True(cache.TryAdd(body, Now));
        Assert.False(cache.TryAdd(body, Now.AddSeconds(30)));
        Assert.True(cache.TryAdd(body with { SourceDevice = Audience }, Now));
        // The entry is kept to expiry (60 s) plus skew (30 s); the same nonce on a later-expiring body is refused until then.
        Assert.False(cache.TryAdd(body with { ExpiresAt = Now.AddSeconds(149) }, Now.AddSeconds(89)));
        Assert.True(cache.TryAdd(body with { ExpiresAt = Now.AddSeconds(150) }, Now.AddSeconds(91)));
    }

    [Fact]
    public void A_full_replay_cache_fails_closed_rather_than_evicting_live_entries()
    {
        var cache = new FleetActionReplayCache(capacity: 2, skew: TimeSpan.FromSeconds(30), Now.AddMinutes(-10));
        Assert.True(cache.TryAdd(Body(), Now));
        Assert.True(cache.TryAdd(Body(), Now));
        Assert.False(cache.TryAdd(Body(), Now));
        Assert.True(cache.TryAdd(Body() with { ExpiresAt = Now.AddSeconds(150) }, Now.AddSeconds(91)));
    }
}

public sealed class FleetActionSubjectDigestTests
{
    [Fact]
    public void The_subject_digest_is_stable_and_changes_with_every_tuple_field()
    {
        var same = FleetActionEnvelope.SubjectDigest("a", "h", "s", "c", 7, 99);
        Assert.Equal(same, FleetActionEnvelope.SubjectDigest("a", "h", "s", "c", 7, 99));
        foreach (var other in new[] { FleetActionEnvelope.SubjectDigest("b", "h", "s", "c", 7, 99),
            FleetActionEnvelope.SubjectDigest("a", "i", "s", "c", 7, 99), FleetActionEnvelope.SubjectDigest("a", "h", "t", "c", 7, 99),
            FleetActionEnvelope.SubjectDigest("a", "h", "s", "d", 7, 99), FleetActionEnvelope.SubjectDigest("a", "h", "s", "c", 8, 99),
            FleetActionEnvelope.SubjectDigest("a", "h", "s", "c", 7, 100), FleetActionEnvelope.SubjectDigest("ah", "", "s", "c", 7, 99) })
        { Assert.NotEqual(same, other); }
    }
}
