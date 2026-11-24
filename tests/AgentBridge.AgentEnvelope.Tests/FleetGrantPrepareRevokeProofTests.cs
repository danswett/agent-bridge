using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetGrantPrepareRevokeProofTests : IDisposable
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    const string Document = "{\"id\":\"00000000-0000-0000-0000-000000000001\",\"generation\":4}";
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FleetGrantProofContext context = new("account", "default", "0123456789abcdef0123456789abcdef",
        "grant.prepareRevoke", Guid.Parse("00000000-0000-0000-0000-000000000003"));
    readonly FleetGrantPrepareRevokeRequest request = new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 4);
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    readonly Clock clock = new();
    byte[] Point { get { var q = key.ExportParameters(false).Q; return [4, .. q.X!, .. q.Y!]; } }
    public void Dispose() => key.Dispose();
    FleetGrantPrepared Prepared(FleetGrantChallenge? challenge = null, string document = Document)
    {
        var c = challenge ?? new(Guid.Parse("00000000-0000-0000-0000-000000000002"), request.Id, request.Generation,
            context.Account, context.Profile, context.Receiver, "grant.confirmRevoke", "", clock.Now.AddMinutes(5));
        c = c with { Digest = FleetGrantPrepareRevokeProof.Digest(c, document) };
        return new(c, JsonSerializer.SerializeToElement(new { c.Id, c.Generation, c.Receiver, c.Account, c.Profile,
            c.Action, Document = document, c.Digest }, Wire));
    }
    FleetGrantPrepareRevokeResult Seal(FleetGrantPrepared? p = null) => FleetGrantPrepareRevokeProof.Seal(
        p ?? Prepared(), context, request, new EcdsaEnvelopeSigner(key), clock);
    FleetGrantPrepared Verify(FleetGrantPrepareRevokeResult result) => FleetGrantPrepareRevokeProof.Verify(result, Point, context, request, clock);

    [Fact]
    public void Verified_preparation_retains_the_reviewed_tuple_and_confirms_without_rewriting_context()
    {
        var result = FleetGrantPrepareRevokeProof.Parse(JsonSerializer.SerializeToUtf8Bytes(Seal(), Wire));
        var prepared = Verify(result); var confirm = context with { Action = "grant.confirmRevoke", Request = Guid.NewGuid() };
        var retained = FleetGrantPrepareRevokeProof.ConfirmContext(prepared, confirm, clock);
        Assert.Same(confirm, retained.Context);
        Assert.Equal(new FleetGrantConfirmation(prepared.Challenge.Request, request.Id, request.Generation, prepared.Challenge.Digest), retained.Confirmation);
        var proof = FleetGrantRevokeProof.SealRequest(retained.Confirmation, retained, new EcdsaEnvelopeSigner(key));
        Assert.Equal(retained.Confirmation, FleetGrantRevokeProof.VerifyRequest(proof, Point, retained));
    }

    [Fact]
    public void Digest_preimage_uses_the_approved_exact_byte_order_and_not_the_issuance_digest()
    {
        var c = Prepared().Challenge; var n = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(n, 4);
        var bytes = Framing.F(Framing.Label("agent-bridge fleet revoke digest v1"), Framing.Utf8(c.Account),
            Framing.Utf8(c.Profile), Framing.Utf8(c.Receiver), Framing.Utf8("grant.confirmRevoke"),
            Framing.Utf8(c.Request.ToString("D")), Framing.Utf8(c.Id.ToString("D")), n,
            Framing.Utf8(c.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)), Framing.Utf8(Document));
        Assert.Equal(bytes, FleetGrantPrepareRevokeProof.DigestBytes(c, Document));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), c.Digest);
        Assert.Equal("2c3e384e27cf621c807f0541187bd48e2fdd1091f5f37ef8dcd1a9b52e28e0cf", c.Digest);
        Assert.Equal(c.Digest, FleetGrantPrepareRevokeProof.Digest(c with { Digest = new('f', 64) }, Document));
        Assert.NotEqual(c.Digest, FleetGrantPrepareRevokeProof.Digest(c, Document + " "));
    }

    [Fact]
    public void Every_challenge_and_preview_field_is_sealed_and_the_digest_is_recomputed()
    {
        var result = Seal();
        foreach (var part in new[] { "challenge", "preview" })
        foreach (var field in JsonSerializer.SerializeToElement(result.Prepared, Wire).GetProperty(part).EnumerateObject())
        {
            var node = JsonSerializer.SerializeToNode(result.Prepared, Wire)!;
            node[part]![field.Name] = field.Value.ValueKind == JsonValueKind.Number ? JsonValue.Create(5)
                : JsonValue.Create(field.Name is "request" or "id" ? Guid.NewGuid().ToString("D")
                    : field.Name == "expiresAt" ? "2026-11-22T10:06:00+00:00" : "changed");
            var changed = node.Deserialize<FleetGrantPrepared>(Wire);
            Assert.Throws<InvalidDataException>(() => Verify(result with { Prepared = changed! }));
        }
        var p = Prepared(); var preview = JsonNode.Parse(p.Preview.GetRawText())!; preview["document"] = "changed";
        Assert.Throws<InvalidDataException>(() => Seal(p with { Preview = JsonSerializer.SerializeToElement(preview) }));
    }

    [Fact]
    public void Caller_and_receiver_clocks_refuse_expired_challenges_at_the_exact_boundary_and_before_confirm()
    {
        var result = Seal(); var expiry = result.Prepared.Challenge.ExpiresAt;
        foreach (var instant in new[] { expiry, expiry.AddTicks(1) })
        {
            var late = new Clock { Now = instant };
            Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.Seal(result.Prepared, context, request, new EcdsaEnvelopeSigner(key), late));
            Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.Verify(result, Point, context, request, late));
            Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.ConfirmContext(result.Prepared, context with { Action = "grant.confirmRevoke" }, late));
        }
        clock.Now = expiry.AddTicks(-1); Assert.NotNull(Verify(result));
    }

    [Fact]
    public void Wrong_selectors_context_actions_keys_nonce_and_other_domains_refuse()
    {
        var result = Seal();
        foreach (var c in new[] { context with { Account = "other" }, context with { Profile = "other" },
            context with { Receiver = new('f', 32) }, context with { Request = Guid.NewGuid() }, context with { Action = "grant.confirmRevoke" } })
        { Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.Verify(result, Point, c, request, clock)); }
        foreach (var r in new[] { request with { Id = Guid.NewGuid() }, request with { Generation = 5 } })
        { Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.Verify(result, Point, context, r, clock)); }
        Assert.Throws<InvalidDataException>(() => Seal(Prepared(result.Prepared.Challenge with { Request = context.Request })));
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256); var q = wrong.ExportParameters(false).Q;
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.Verify(result, [4, .. q.X!, .. q.Y!], context, request, clock));
        foreach (var domain in new[] { FleetGrantPrepareIssueProof.Domain, FleetGrantReadDataProof.Domain,
            FleetGrantReadProof.Domain, FleetGrantReceiptProof.Domain, FleetGrantRevokeProof.RequestDomain, FleetGrantRevokeProof.ReceiptDomain })
        {
            var proof = FleetGrantProof.Seal(Framing.F(Framing.Label(domain), [1]), context, new EcdsaEnvelopeSigner(key));
            Assert.Throws<InvalidDataException>(() => Verify(result with { Proof = proof }));
        }
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.ConfirmContext(result.Prepared, context, clock));
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.ConfirmContext(result.Prepared, context with { Action = "grant.confirmRevoke", Request = result.Prepared.Challenge.Request }, clock));
    }

    [Fact]
    public void Strict_whole_reply_and_nested_fields_refuse_missing_extra_duplicate_case_null_and_bad_expiry()
    {
        var root = JsonSerializer.SerializeToElement(Seal(), Wire);
        void Refuse(string json) => Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.Parse(Encoding.UTF8.GetBytes(json)));
        void Visit(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) { return; }
            var raw = value.GetRawText();
            void Bad(string changed) => Refuse(root.GetRawText().Replace(raw, changed));
            Bad(raw[..^1] + ",\"extra\":1}");
            foreach (var p in value.EnumerateObject())
            {
                Bad(raw[..^1] + ",\"" + p.Name + "\":" + p.Value.GetRawText() + "}");
                Bad(raw.Replace("\"" + p.Name + "\":", "\"" + p.Name.ToUpperInvariant() + "\":"));
                var node = JsonNode.Parse(raw)!.AsObject(); node.Remove(p.Name); Bad(node.ToJsonString());
                node = JsonNode.Parse(raw)!.AsObject(); node[p.Name] = null; Bad(node.ToJsonString()); Visit(p.Value);
                node = JsonNode.Parse(raw)!.AsObject(); node[p.Name] = new JsonArray(); Bad(node.ToJsonString());
            }
        }
        Visit(root);
        Refuse(root.GetRawText().Replace("2026-11-22T10:05:00", "unparsable"));
    }

    [Fact]
    public void Raw_limit_precedes_JSON_and_document_bytes_count_inside_the_complete_body_limit()
    {
        var oversized = new byte[128 * 1024 + 1];
        Assert.Null(Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.Parse(oversized)).InnerException);
        byte[] invalid = [.. Encoding.UTF8.GetBytes("{\"prepared\":{\"challenge\":{\"action\":\""), 0xff, .. Encoding.UTF8.GetBytes("\"}}}")];
        Assert.Throws<InvalidDataException>(() => FleetGrantPrepareRevokeProof.Parse(invalid));
        Assert.Throws<InvalidDataException>(() => Seal(Prepared(document: new string('x', 30 * 1024))));
        Assert.Throws<InvalidDataException>(() => Seal(Prepared(document: new string('\u754c', 11 * 1024))));
        Assert.Throws<InvalidDataException>(() => Prepared(document: "\ud800"));
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(Prepared(), Wire));
        var result = Seal(doc.RootElement.Deserialize<FleetGrantPrepared>(Wire)); doc.Dispose();
        Assert.Equal(Document, Verify(result).Preview.GetProperty("document").GetString());
    }
}
