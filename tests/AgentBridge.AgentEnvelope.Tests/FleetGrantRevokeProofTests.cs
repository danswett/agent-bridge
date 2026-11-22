using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetGrantRevokeProofTests : IDisposable
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FleetGrantRevokeContext expected = new(new("account", "default",
        "0123456789abcdef0123456789abcdef", "grant.confirmRevoke", Guid.NewGuid()),
        new(Guid.NewGuid(), Guid.NewGuid(), 1, new('a', 64)));
    byte[] Point { get { var q = key.ExportParameters(false).Q; return [4, .. q.X!, .. q.Y!]; } }
    public void Dispose() => key.Dispose();
    JsonObject Reply(string problem = "none", string state = "revoked")
    {
        var r = expected.Confirmation; var c = expected.Context;
        var obj = JsonSerializer.SerializeToNode(new { r.Id, r.Generation, c.Receiver, c.Account, c.Profile,
            c.Action, r.Digest, Problem = problem, FinalState = state }, Wire)!.AsObject();
        if (problem == "none")
        {
            obj["tombstone"] = true;
            obj["counters"] = JsonSerializer.SerializeToNode(new { Hour = 1, Day = 2, Pending = 1, Live = 1,
                Requests = Array.Empty<string>(), LastCaller = (string?)null, LastOutcome = (string?)null,
                ObservedAt = new DateTimeOffset(2026, 11, 22, 9, 30, 0, TimeSpan.Zero),
                PendingRequests = Array.Empty<string>(), LiveSessions = Array.Empty<string>() }, Wire);
            obj["inFlightRequests"] = JsonSerializer.SerializeToNode(new[] { Guid.Parse("00000000-0000-0000-0000-000000000001") }, Wire);
        }
        return obj;
    }
    FleetGrantRevokeReceiptResult Seal(JsonNode? reply = null) => FleetGrantRevokeProof.SealReceipt(
        JsonSerializer.SerializeToElement(reply ?? Reply(), Wire), expected, new EcdsaEnvelopeSigner(key));
    byte[] Raw<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Wire);

    [Fact]
    public void The_sealed_request_and_full_success_receipt_return_only_verified_typed_values()
    {
        var request = FleetGrantRevokeProof.SealRequest(expected.Confirmation, expected, new EcdsaEnvelopeSigner(key));
        Assert.Equal(expected.Confirmation, FleetGrantRevokeProof.VerifyRequest(
            FleetGrantRevokeProof.ParseRequest(Raw(request)), Point, expected));
        var outcome = FleetGrantRevokeProof.VerifyReceipt(FleetGrantRevokeProof.ParseReceipt(Raw(Seal())), Point, expected);
        Assert.Equal(FleetGrantRevokeState.Revoked, outcome.State);
        Assert.Equal(FleetGrantRevokeProblem.None, outcome.Problem);
        Assert.Equal(expected.Confirmation.Id, outcome.Id);
        Assert.True(outcome.Receipt.GetProperty("tombstone").GetBoolean());
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("notFound")]
    [InlineData("generation")]
    [InlineData("auditIncomplete")]
    [InlineData("writeFailed")]
    [InlineData("personRequired")]
    [InlineData("policy")]
    [InlineData("other")]
    public void Every_closed_refusal_problem_stays_refused_or_revocation_pending_without_fabricated_counters(string problem)
    {
        foreach (var state in new[] { "refused", "unconfirmed" })
        {
            var result = FleetGrantRevokeProof.ParseReceipt(Raw(Seal(Reply(problem, state))));
            var outcome = FleetGrantRevokeProof.VerifyReceipt(result, Point, expected);
            Assert.Equal(state == "unconfirmed" ? FleetGrantRevokeState.RevocationPending : FleetGrantRevokeState.Refused, outcome.State);
            Assert.NotEqual(FleetGrantRevokeState.Revoked, outcome.State);
            Assert.False(outcome.Receipt.TryGetProperty("counters", out _));
        }
    }

    [Theory]
    [InlineData("none", "refused", true, true)]
    [InlineData("none", "unconfirmed", true, true)]
    [InlineData("none", "revoked", false, true)]
    [InlineData("none", "revoked", true, false)]
    [InlineData("unavailable", "refused", true, false)]
    [InlineData("unavailable", "revoked", false, false)]
    [InlineData("UNKNOWN", "unconfirmed", false, false)]
    public void Success_refusal_mixtures_missing_counters_false_tombstones_and_unknown_problems_refuse(
        string problem, string state, bool tombstone, bool counters)
    {
        var obj = Reply(problem, state); if (problem == "none" || tombstone) { obj["tombstone"] = tombstone; }
        if (!counters) { obj.Remove("counters"); }
        Assert.Throws<InvalidDataException>(() => Seal(obj));
    }

    [Fact]
    public void All_request_bindings_receipt_fields_and_nested_counters_are_sealed()
    {
        var request = FleetGrantRevokeProof.SealRequest(expected.Confirmation, expected, new EcdsaEnvelopeSigner(key));
        foreach (var r in new[] { expected.Confirmation with { Challenge = Guid.NewGuid() },
            expected.Confirmation with { Id = Guid.NewGuid() }, expected.Confirmation with { Generation = 2 },
            expected.Confirmation with { Digest = new('b', 64) } })
        { Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyRequest(request with { Request = r }, Point, expected)); }
        var result = Seal(); var original = JsonNode.Parse(result.Receipt.GetRawText())!.AsObject();
        foreach (var obj in new[] { original, original["counters"]!.AsObject() })
        foreach (var f in obj)
        {
            var changed = JsonNode.Parse(original.ToJsonString())!; var target = obj == original ? changed : changed["counters"]!;
            target[f.Key] = f.Value?.GetValueKind() switch
            {
                JsonValueKind.Number => JsonValue.Create(f.Value.GetValue<long>() + 1),
                JsonValueKind.True => JsonValue.Create(false), JsonValueKind.Array => new JsonArray("changed"),
                _ => JsonValue.Create("changed")
            };
            Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyReceipt(result with
            { Receipt = JsonSerializer.SerializeToElement(changed, Wire) }, Point, expected));
        }
    }

    [Fact]
    public void Each_envelope_and_success_or_refusal_branch_is_strict_with_only_explicit_diagnostic_nulls()
    {
        void Check(JsonElement root, bool request)
        {
            void Refuse(string json)
            { Assert.Throws<InvalidDataException>(() => { if (request) { FleetGrantRevokeProof.ParseRequest(Encoding.UTF8.GetBytes(json)); }
                else { FleetGrantRevokeProof.ParseReceipt(Encoding.UTF8.GetBytes(json)); } }); }
            void Visit(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Object) { return; }
                var raw = element.GetRawText();
                void Bad(string value) => Refuse(root.GetRawText().Replace(raw, value));
                Bad(raw[..^1] + ",\"extra\":1}");
                foreach (var f in element.EnumerateObject())
                {
                    Bad(raw.Replace("\"" + f.Name + "\":", "\"" + f.Name.ToUpperInvariant() + "\":"));
                    Bad(raw[..^1] + ",\"" + f.Name + "\":" + f.Value.GetRawText() + "}");
                    var obj = JsonNode.Parse(raw)!.AsObject(); obj.Remove(f.Name); Bad(obj.ToJsonString());
                    obj = JsonNode.Parse(raw)!.AsObject(); obj[f.Name] = null;
                    if (f.Name is not ("lastCaller" or "lastOutcome")) { Bad(obj.ToJsonString()); }
                    Visit(f.Value);
                }
            }
            Visit(root);
        }
        Check(JsonSerializer.SerializeToElement(FleetGrantRevokeProof.SealRequest(expected.Confirmation, expected, new EcdsaEnvelopeSigner(key)), Wire), true);
        Check(JsonSerializer.SerializeToElement(Seal(), Wire), false);
        Check(JsonSerializer.SerializeToElement(Seal(Reply("writeFailed", "unconfirmed")), Wire), false);
    }

    [Fact]
    public void Raw_size_is_checked_before_deserialization_and_framed_oversize_refuses()
    {
        var request = Raw(FleetGrantRevokeProof.SealRequest(expected.Confirmation, expected, new EcdsaEnvelopeSigner(key)));
        var padded = Enumerable.Repeat((byte)' ', 128 * 1024).ToArray(); request.CopyTo(padded, 0);
        Assert.NotNull(FleetGrantRevokeProof.ParseRequest(padded));
        var tooLarge = new byte[128 * 1024 + 1];
        Assert.Null(Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.ParseRequest(tooLarge)).InnerException);
        Assert.Null(Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.ParseReceipt(tooLarge)).InnerException);
        var obj = Reply(); obj["counters"]!["lastOutcome"] = new string('x', 31 * 1024);
        Assert.Throws<InvalidDataException>(() => Seal(obj));
        byte[] invalid = [.. Encoding.UTF8.GetBytes("{\"receipt\":{\"problem\":\""), 0xff, .. Encoding.UTF8.GetBytes("\"},\"proof\":{\"body\":\"a\",\"signature\":\"b\"}}")];
        using var doc = JsonDocument.Parse(invalid); Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.ParseReceipt(invalid));
        Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyReceipt(new(doc.RootElement.GetProperty("receipt"), Seal().Proof), Point, expected));
    }

    [Fact]
    public void Caller_context_approved_tuple_fresh_nonce_trusted_key_and_every_other_domain_refuse_replay()
    {
        var request = FleetGrantRevokeProof.SealRequest(expected.Confirmation, expected, new EcdsaEnvelopeSigner(key));
        var receipt = Seal(); var c = expected.Context; var r = expected.Confirmation;
        foreach (var context in new[] { c with { Account = "other" }, c with { Profile = "other" },
            c with { Receiver = new('f', 32) }, c with { Action = "grant.revoke" }, c with { Request = Guid.NewGuid() } })
        { Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyRequest(request, Point, expected with { Context = context }));
            Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyReceipt(receipt, Point, expected with { Context = context })); }
        foreach (var tuple in new[] { r with { Challenge = Guid.NewGuid() }, r with { Id = Guid.NewGuid() },
            r with { Generation = 2 }, r with { Digest = new('b', 64) } })
        { Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyReceipt(receipt, Point, expected with { Confirmation = tuple })); }
        Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.SealRequest(r with { Challenge = c.Request },
            expected with { Confirmation = r with { Challenge = c.Request } }, new EcdsaEnvelopeSigner(key)));
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256); var q = wrong.ExportParameters(false).Q;
        Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyRequest(request, [4, .. q.X!, .. q.Y!], expected));
        Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyReceipt(receipt, [4, .. q.X!, .. q.Y!], expected));
        foreach (var domain in new[] { FleetGrantRevokeProof.RequestDomain, FleetGrantRevokeProof.ReceiptDomain,
            "agent-bridge fleet read problem v1", "agent-bridge fleet read data v1", "agent-bridge fleet issue receipt v1" })
        {
            var proof = FleetGrantProof.Seal(Framing.F(Framing.Label(domain), Framing.Utf8("replay")), c, new EcdsaEnvelopeSigner(key));
            Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyRequest(request with { Proof = proof }, Point, expected));
            Assert.Throws<InvalidDataException>(() => FleetGrantRevokeProof.VerifyReceipt(receipt with { Proof = proof }, Point, expected));
        }
    }

    [Fact]
    public void Negative_counters_and_noncanonical_duplicate_or_unsorted_inflight_ids_refuse()
    {
        foreach (var name in new[] { "hour", "day", "pending", "live" })
        { var obj = Reply(); obj["counters"]![name] = -1; Assert.Throws<InvalidDataException>(() => Seal(obj)); }
        foreach (var ids in new[] { new[] { Guid.Empty.ToString("D") }, new[] { "00000000000000000000000000000001" },
            new[] { "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000001" },
            new[] { "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000001" } })
        { var obj = Reply(); obj["inFlightRequests"] = JsonSerializer.SerializeToNode(ids); Assert.Throws<InvalidDataException>(() => Seal(obj)); }
    }

    [Fact]
    public void Request_and_pending_refusal_frames_have_independent_fixed_domains_and_bound_nonce()
    {
        var r = expected.Confirmation; var c = expected.Context;
        byte[] N(long n) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, n); return b; }
        var request = FleetGrantRevokeProof.SealRequest(r, expected, new EcdsaEnvelopeSigner(key));
        Assert.Equal(Framing.F(Framing.Label(FleetGrantRevokeProof.RequestDomain), Framing.Utf8(r.Challenge.ToString("D")),
            Framing.Utf8(r.Id.ToString("D")), N(r.Generation), Framing.Utf8(r.Digest)),
            FleetGrantProof.Verify(request.Proof, Point, c));
        var receipt = Seal(Reply("generation", "unconfirmed"));
        Assert.Equal(Framing.F(Framing.Label(FleetGrantRevokeProof.ReceiptDomain), Framing.Utf8(r.Challenge.ToString("D")),
            Framing.Utf8(r.Id.ToString("D")), N(r.Generation), Framing.Utf8(c.Receiver), Framing.Utf8(c.Account),
            Framing.Utf8(c.Profile), Framing.Utf8(c.Action), Framing.Utf8(r.Digest), [3], [2], Framing.F()),
            FleetGrantProof.Verify(receipt.Proof, Point, c));
    }
}
