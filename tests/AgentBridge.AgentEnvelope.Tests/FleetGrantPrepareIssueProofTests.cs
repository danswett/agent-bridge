using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetGrantPrepareIssueProofTests : IDisposable
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FleetGrantProofContext context = new("account", "default",
        "0123456789abcdef0123456789abcdef", "grant.prepareIssue", Guid.NewGuid());
    byte[] Point
    {
        get { var q = key.ExportParameters(false).Q; return [4, .. q.X!, .. q.Y!]; }
    }
    FleetGrantPrepared Prepared()
    {
        var challenge = new FleetGrantChallenge(Guid.NewGuid(), Guid.NewGuid(), 1, context.Account,
            context.Profile, context.Receiver, "grant.confirmIssue", new('a', 64),
            new DateTimeOffset(2026, 11, 22, 8, 45, 0, TimeSpan.Zero));
        var preview = JsonSerializer.SerializeToElement(new
        {
            challenge.Id, challenge.Generation, challenge.Receiver, challenge.Profile,
            Document = "{\"caps\":{\"hourly\":1,\"daily\":2,\"live\":1}}", challenge.Digest,
            Allocation = new { Hourly = 1, Daily = 2, Live = 1 },
            EffectivePolicy = "ask; isolated", UnavailableActions = new[] { "stop" }
        }, Wire);
        return new(challenge, preview);
    }
    FleetGrantPrepareIssueResult Seal() =>
        FleetGrantPrepareIssueProof.Seal(Prepared(), context, new EcdsaEnvelopeSigner(key));
    public void Dispose() => key.Dispose();

    [Fact]
    public void The_prepared_value_round_trips_and_every_confirmation_field_has_a_fixed_frame()
    {
        var result = Seal();
        var p = result.Prepared; var c = p.Challenge; var v = p.Preview;
        byte[] Number(long value) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, value); return b; }
        var expected = Framing.F(Framing.Label("agent-bridge fleet prepare issue v1"),
            Framing.Utf8(c.Request.ToString("D")), Framing.Utf8(c.Id.ToString("D")), Number(c.Generation),
            Framing.Utf8(c.Account), Framing.Utf8(c.Profile), Framing.Utf8(c.Receiver), Framing.Utf8(c.Action),
            Convert.FromHexString(c.Digest), Framing.Utf8(c.ExpiresAt.ToString("O")),
            Framing.Utf8(c.Id.ToString("D")), Number(c.Generation), Framing.Utf8(c.Receiver), Framing.Utf8(c.Profile),
            Framing.Utf8(v.GetProperty("document").GetString()!), Convert.FromHexString(c.Digest),
            Number(1), Number(2), Number(1), Framing.Utf8("ask; isolated"), Framing.F(Framing.Utf8("stop")));
        Assert.Equal(expected, FleetGrantProof.Verify(result.Proof, Point, context));
        var parsed = FleetGrantPrepareIssueProof.Parse(JsonSerializer.SerializeToElement(result, Wire));
        Assert.Equal(c, FleetGrantPrepareIssueProof.Verify(parsed, Point, context).Challenge);
        Assert.Equal(v.GetRawText(), FleetGrantPrepareIssueProof.Verify(parsed, Point, context).Preview.GetRawText());
    }

    [Theory]
    [InlineData("challenge.request", "\"11111111-1111-1111-1111-111111111111\"")]
    [InlineData("challenge.id", "\"11111111-1111-1111-1111-111111111111\"")]
    [InlineData("challenge.generation", "2")]
    [InlineData("challenge.account", "\"other\"")]
    [InlineData("challenge.profile", "\"other\"")]
    [InlineData("challenge.receiver", "\"fedcba9876543210fedcba9876543210\"")]
    [InlineData("challenge.action", "\"grant.read\"")]
    [InlineData("challenge.digest", "\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"")]
    [InlineData("challenge.expiresAt", "\"2026-11-22T08:46:00+00:00\"")]
    [InlineData("preview.id", "\"11111111-1111-1111-1111-111111111111\"")]
    [InlineData("preview.generation", "2")]
    [InlineData("preview.receiver", "\"fedcba9876543210fedcba9876543210\"")]
    [InlineData("preview.profile", "\"other\"")]
    [InlineData("preview.document", "\"{\\\"caps\\\":{\\\"hourly\\\":2,\\\"daily\\\":2,\\\"live\\\":1}}\"")]
    [InlineData("preview.digest", "\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"")]
    [InlineData("preview.allocation.hourly", "2")]
    [InlineData("preview.allocation.daily", "3")]
    [InlineData("preview.allocation.live", "2")]
    [InlineData("preview.effectivePolicy", "\"allowAll\"")]
    [InlineData("preview.unavailableActions", "[]")]
    public void Changing_any_challenge_or_preview_field_without_a_new_seal_is_refused(string path, string value)
    {
        var result = Seal();
        var node = JsonSerializer.SerializeToNode(result.Prepared, Wire)!;
        var parts = path.Split('.'); var parent = node;
        foreach (var part in parts[..^1]) { parent = parent[part]!; }
        parent[parts[^1]] = JsonNode.Parse(value);
        var changed = node.Deserialize<FleetGrantPrepared>(Wire)!;
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantPrepareIssueProof.Verify(result with { Prepared = changed }, Point, context));
    }

    [Fact]
    public void Unknown_missing_duplicate_null_or_wrong_case_fields_at_every_envelope_level_refuse()
    {
        var result = Seal();
        var json = JsonSerializer.SerializeToElement(result, Wire);
        void Check(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object) { return; }
            var fields = element.EnumerateObject().ToArray();
            var raw = element.GetRawText();
            foreach (var bad in new[] { raw[..^1] + ",\"extra\":1}",
                raw[..^1] + ",\"" + fields[0].Name + "\":" + fields[0].Value.GetRawText() + "}" })
            {
                Refuse(bad, path);
            }
            foreach (var f in fields)
            {
                var obj = JsonNode.Parse(raw)!.AsObject(); obj.Remove(f.Name);
                Refuse(obj.ToJsonString(), path);
                obj = JsonNode.Parse(raw)!.AsObject(); obj[f.Name] = null;
                Refuse(obj.ToJsonString(), path);
                obj = JsonNode.Parse(raw)!.AsObject(); var value = obj[f.Name]; obj.Remove(f.Name);
                obj[char.ToUpperInvariant(f.Name[0]) + f.Name[1..]] = value;
                Refuse(obj.ToJsonString(), path);
                Check(f.Value, path.Length == 0 ? f.Name : path + "." + f.Name);
            }
        }
        void Refuse(string bad, string path)
        {
            var raw = json.GetRawText();
            var target = json;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) { target = target.GetProperty(part); }
            using var doc = JsonDocument.Parse(path.Length == 0 ? bad : raw.Replace(target.GetRawText(), bad));
            Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Parse(doc.RootElement));
        }
        Check(json, "");
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Parse(default));
    }

    [Fact]
    public void Wrong_context_action_key_missing_proof_and_invalid_values_refuse_without_defaults()
    {
        var result = Seal();
        foreach (var other in new[]
        {
            context with { Account = "other" }, context with { Profile = "other" },
            context with { Receiver = "fedcba9876543210fedcba9876543210" },
            context with { Action = "grant.read" }, context with { Request = Guid.NewGuid() }, context with { Request = Guid.Empty }
        })
        {
            Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Verify(result, Point, other));
            if (other.Request == context.Request || other.Request == Guid.Empty)
            {
                Assert.Throws<InvalidDataException>(() =>
                    FleetGrantPrepareIssueProof.Seal(result.Prepared, other, new EcdsaEnvelopeSigner(key)));
            }
        }
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = otherKey.ExportParameters(false).Q;
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Verify(result, [4, .. q.X!, .. q.Y!], context));
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Verify(result with { Proof = null! }, Point, context));
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Verify(null!, Point, context));
        foreach (var bad in new FleetGrantPrepared[]
        {
            result.Prepared with { Challenge = null! }, result.Prepared with { Preview = default },
            result.Prepared with { Challenge = result.Prepared.Challenge with { Request = Guid.Empty } },
            result.Prepared with { Challenge = result.Prepared.Challenge with { ExpiresAt = default } },
            result.Prepared with { Challenge = result.Prepared.Challenge with { Action = "grant.prepareIssue" } }
        })
        {
            Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Seal(bad, context, new EcdsaEnvelopeSigner(key)));
        }
    }

    [Fact]
    public void Read_and_receipt_bodies_cannot_be_replayed_as_prepare_even_when_resigned_for_its_context()
    {
        var result = Seal();
        var read = FleetGrantReadProof.Seal(FleetGrantReadProof.Reply(FleetGrantReadProblem.None),
            context with { Action = "grant.read" }, new EcdsaEnvelopeSigner(key));
        var c = result.Prepared.Challenge;
        var receipt = FleetGrantReceiptProof.Seal(JsonSerializer.SerializeToElement(new
        { c.Id, c.Generation, c.Receiver, c.Digest, Rotated = true }, Wire),
            context with { Action = "grant.confirmIssue" }, new EcdsaEnvelopeSigner(key));
        foreach (var proof in new[] { read.Proof, receipt.Proof })
        {
            Assert.Throws<InvalidDataException>(() =>
                FleetGrantPrepareIssueProof.Verify(result with { Proof = proof }, Point, context));
            var resigned = FleetGrantProof.Seal(Codec.FromBase64Url(proof.Body, "body"), context, new EcdsaEnvelopeSigner(key));
            Assert.Throws<InvalidDataException>(() =>
                FleetGrantPrepareIssueProof.Verify(result with { Proof = resigned }, Point, context));
        }
        var bytes = Codec.FromBase64Url(result.Proof.Body, "body"); bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Verify(
            result with { Proof = result.Proof with { Body = Codec.Base64Url(bytes) } }, Point, context));
    }

    [Theory]
    [InlineData("allocation", "{\"hourly\":0,\"daily\":2,\"live\":1}")]
    [InlineData("allocation", "{\"hourly\":3,\"daily\":2,\"live\":1}")]
    [InlineData("allocation", "{\"hourly\":1,\"daily\":5001,\"live\":1}")]
    [InlineData("allocation", "{\"hourly\":1,\"daily\":2,\"live\":101}")]
    [InlineData("allocation", "{\"hourly\":\"1\",\"daily\":2,\"live\":1}")]
    [InlineData("allocation", "{\"hourly\":1.5,\"daily\":2,\"live\":1}")]
    [InlineData("unavailableActions", "[\"unknown\"]")]
    [InlineData("unavailableActions", "[\"stop\",\"stop\"]")]
    [InlineData("unavailableActions", "[null]")]
    [InlineData("document", "null")]
    [InlineData("document", "\"\"")]
    [InlineData("effectivePolicy", "true")]
    public void Invalid_preview_values_cannot_be_sealed_or_verified(string field, string value)
    {
        var result = Seal();
        var preview = JsonNode.Parse(result.Prepared.Preview.GetRawText())!;
        preview[field] = JsonNode.Parse(value);
        var bad = result.Prepared with { Preview = JsonSerializer.SerializeToElement(preview, Wire) };
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Seal(bad, context, new EcdsaEnvelopeSigner(key)));
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Verify(result with { Prepared = bad }, Point, context));
    }

    [Fact]
    public void Empty_unavailable_actions_are_explicit_and_prepared_snapshots_outlive_the_input_document()
    {
        var p = Prepared();
        var preview = JsonNode.Parse(p.Preview.GetRawText())!; preview["unavailableActions"] = new JsonArray();
        FleetGrantPrepareIssueResult result;
        using (var doc = JsonDocument.Parse(preview.ToJsonString()))
        { result = FleetGrantPrepareIssueProof.Seal(p with { Preview = doc.RootElement }, context, new EcdsaEnvelopeSigner(key)); }
        Assert.Empty(FleetGrantPrepareIssueProof.Verify(result, Point, context).Preview.GetProperty("unavailableActions").EnumerateArray());
    }

    [Fact]
    public void A_challenge_nonce_echoing_the_callers_request_is_refused_even_with_a_valid_host_signature()
    {
        var result = Seal();
        var bad = result.Prepared with { Challenge = result.Prepared.Challenge with { Request = context.Request } };
        var body = Codec.FromBase64Url(result.Proof.Body, "body");
        Framing.Utf8(context.Request.ToString("D")).CopyTo(body, 4 + Framing.Label(FleetGrantPrepareIssueProof.Domain).Length + 4);
        var proof = FleetGrantProof.Seal(body, context, new EcdsaEnvelopeSigner(key));
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Verify(new(bad, proof), Point, context));
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareIssueProof.Seal(bad, context, new EcdsaEnvelopeSigner(key)));
    }
}
