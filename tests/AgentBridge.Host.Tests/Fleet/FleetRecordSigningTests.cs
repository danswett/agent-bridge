using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Fleet;
using AgentBridge.Relay;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetRecordSigningTests
{
    static readonly JsonObject Vectors = VectorFiles.Read("fleet-registry-signing.json");
    static readonly byte[] Point = Codec.FromBase64Url((string)Vectors["hostSign"]!, "hostSign");
    static JsonObject Good() => Vectors["record"]!.DeepClone().AsObject();
    static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);

    [Fact]
    public void Canonical_and_framed_bytes_match_the_shared_oracle_despite_order_escaping_and_sig()
    {
        var expected = Encoding.UTF8.GetBytes((string)Vectors["canonical"]!);
        Assert.Equal(expected, FleetRecordSigning.CanonicalBytes(Element(Good())));
        Assert.Equal(expected, FleetRecordSigning.CanonicalBytes(Element(Vectors["equivalent"]!)));
        Assert.Equal(Convert.FromHexString((string)Vectors["signedHex"]!), FleetRecordSigning.SignedBytes(Element(Good())));
        Assert.True(FleetRecordSigning.Verify(Element(Good()), Point));
    }

    public static IEnumerable<object[]> Mutations => Vectors["mutations"]!.AsArray().Select(c =>
        new object[] { (string)c!["name"]!, (string)c["path"]!, c["value"]!.ToJsonString() });

    [Theory, MemberData(nameof(Mutations))]
    public void Every_signed_field_changes_the_bytes_and_refuses_the_original_signature(string name, string path, string json)
    {
        var changed = Good();
        var parts = path.Split('.');
        JsonNode parent = changed;
        foreach (var part in parts[..^1])
        {
            parent = parent is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : parent[part]!;
        }
        parent[parts[^1]] = JsonNode.Parse(json);
        Assert.NotEqual(FleetRecordSigning.CanonicalBytes(Element(Good())), FleetRecordSigning.CanonicalBytes(Element(changed)));
        Assert.False(FleetRecordSigning.Verify(Element(changed), Point), name);
    }

    [Fact]
    public void Forged_wrong_key_unsigned_and_cross_context_records_are_not_verified()
    {
        foreach (var signature in Vectors["refusedSignatures"]!.AsArray())
        {
            var changed = Good();
            changed["sig"] = signature!.DeepClone();
            Assert.False(FleetRecordSigning.Verify(Element(changed), Point));
        }
        Assert.False(FleetRecordSigning.Verify(Element(Good()), Codec.FromBase64Url((string)Vectors["wrongKey"]!, "key")));
        var unsigned = Good();
        unsigned.Remove("sig");
        Assert.False(FleetRecordSigning.Verify(Element(unsigned), Point));
        var sig = Codec.FromBase64Url((string)Good()["sig"]!, "sig");
        foreach (var label in Vectors["otherContexts"]!.AsArray())
        {
            Assert.False(P256.Verify(Point, Framing.F(Framing.Label((string)label!), FleetRecordSigning.CanonicalBytes(Element(unsigned))), sig));
        }
    }

    [Fact]
    public void An_authentic_old_sequence_is_still_refused_by_ordering()
    {
        var old = Good();
        old["seq"] = 1;
        old["sig"] = Vectors["oldSig"]!.DeepClone();
        Assert.True(FleetRecordSigning.Verify(Element(old), Point));
        var registry = new SessionRegistry(TimeProvider.System);
        Assert.Null(registry.Put("test", "host-test", Element(Good())));
        Assert.Equal("stale", registry.Put("test", "host-test", Element(old)));
    }

    [Theory]
    [InlineData("\"seq\":2", "\"seq\":2,\"seq\":2")]
    [InlineData("\"state\":\"working\"", "\"state\":\"working\",\"state\":\"working\"")]
    public void Duplicate_members_cannot_be_hidden_by_canonicalization(string from, string to)
    {
        using var doc = JsonDocument.Parse(Good().ToJsonString().Replace(from, to, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => FleetRecordSigning.CanonicalBytes(doc.RootElement));
        Assert.False(FleetRecordSigning.Verify(doc.RootElement, Point));
    }

    [Fact]
    public async Task The_publisher_signs_the_completed_record_with_the_supplied_signer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        byte[] point = [4, .. q.X!, .. q.Y!];
        var publisher = new FleetPublisher(new FleetPublisherTests.Source(), "machine", sign: record =>
            FleetRecordSigning.Sign(record, bytes => P256.FromRaw(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        Assert.True(await publisher.PublishAsync((record, ct) =>
        {
            Assert.True(FleetRecordSigning.Verify(record, point));
            Assert.Null(RosterFields.Check(record, "machine", out _, out _, out _));
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public void Missing_custody_stays_unsigned_and_creates_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var unsigned = Good();
        unsigned.Remove("sig");
        var result = FleetRecordSigning.SignExisting(Element(unsigned), path);
        Assert.False(result.TryGetProperty("sig", out _));
        Assert.False(FleetRecordSigning.Verify(result, Point));
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task The_record_budget_is_checked_after_the_signer_runs()
    {
        var publisher = new FleetPublisher(new FleetPublisherTests.Source(), "machine", sign: record =>
        {
            var signed = JsonNode.Parse(record.GetRawText())!.AsObject();
            signed["sig"] = new string('a', 16 * 1024);
            return Element(signed);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync((record, ct) => throw new InvalidOperationException("oversize record reached transport")));
    }
}
