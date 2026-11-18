using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Roster;
using Microsoft.Extensions.Time.Testing;
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
    public async Task The_host_publication_forwards_its_signer_to_each_completed_record()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        byte[] point = [4, .. q.X!, .. q.Y!];
        var publication = new FleetPublication(new FleetPublisherTests.Source(), "machine", share: true, sign: record =>
            FleetRecordSigning.Sign(record, bytes => P256.FromRaw(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            Assert.True(await publication.PollAsync((record, ct) =>
            {
                Assert.True(FleetRecordSigning.Verify(record, point));
                Assert.Equal(sequence, record.GetProperty("seq").GetInt64());
                return Task.CompletedTask;
            }, force: true));
        }
    }

    [Fact]
    public async Task The_host_publication_with_missing_custody_stays_unsigned_and_creates_nothing()
    {
        var profile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var publication = new FleetPublication(new FleetPublisherTests.Source(), "machine", share: true,
            sign: record => FleetRecordSigning.SignExisting(record, profile));
        Assert.True(await publication.PollAsync((record, ct) =>
        {
            Assert.False(record.TryGetProperty("sig", out _));
            Assert.False(Directory.Exists(profile));
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public async Task The_record_budget_is_checked_after_the_signer_runs()
    {
        var calls = 0;
        var publisher = new FleetPublisher(new FleetPublisherTests.Source(), "machine", sign: record =>
        {
            calls++;
            var signed = JsonNode.Parse(record.GetRawText())!.AsObject();
            signed["sig"] = new string('a', 16 * 1024);
            return Element(signed);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync((record, ct) => throw new InvalidOperationException("oversize record reached transport")));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true, -1, 3)]
    [InlineData(true, 0, 3)]
    [InlineData(true, 1, 2)]
    [InlineData(true, 105, 2)]
    [InlineData(true, 106, 2)]
    [InlineData(false, -1, 3)]
    [InlineData(false, 0, 3)]
    [InlineData(false, 1, 2)]
    public async Task Entries_are_fitted_before_signing_once_at_the_record_and_signature_boundaries(bool signing, int excess, int count)
    {
        var source = new FleetPublisherTests.Source { Binding = _ => new(100, Fixture.T0) };
        var clock = new FakeTimeProvider(Fixture.T0);
        var displayName = new string('\u00e9', 64);
        JsonElement baseline = default;
        await new FleetPublisher(source, "", _ => displayName, clock).PublishAsync((record, _) =>
        {
            baseline = record;
            return Task.CompletedTask;
        });
        var budget = RosterFields.MaxRecordBytes - (signing ? 105 : 0);
        var machine = new string('m', budget + excess - Encoding.UTF8.GetByteCount(baseline.GetRawText()));
        var calls = 0;
        JsonElement Sign(JsonElement record)
        {
            calls++;
            Assert.InRange(Encoding.UTF8.GetByteCount(record.GetRawText()), 1, budget);
            Assert.Equal(count, record.GetProperty("sessions").GetArrayLength());
            var signed = JsonNode.Parse(record.GetRawText())!.AsObject();
            // A maximum-length P-256 DER signature needs 96 base64url bytes plus nine JSON bytes.
            signed["sig"] = new string('a', 96);
            return JsonSerializer.SerializeToElement(signed, ProtocolJson.Options);
        }
        var publisher = new FleetPublisher(source, machine, _ => displayName, clock, signing ? Sign : null);
        Assert.True(await publisher.PublishAsync((record, _) =>
        {
            var bytes = Encoding.UTF8.GetByteCount(record.GetRawText());
            Assert.InRange(bytes, 1, RosterFields.MaxRecordBytes);
            if (excess <= 0) { Assert.Equal(RosterFields.MaxRecordBytes + excess, bytes); }
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(new { t = "registry-put", id = long.MinValue, record },
                ProtocolJson.Options).Length, 1, RosterFields.MaxFrameBytes);
            var entries = record.GetProperty("sessions");
            Assert.Equal(count, entries.GetArrayLength());
            Assert.Equal(source.Sessions.AsEnumerable().Reverse().Take(count).Select(s => s.Id[..16]),
                entries.EnumerateArray().Select(s => s.GetProperty("id").GetString()));
            Assert.Equal(count < source.Sessions.Count, record.GetProperty("truncated").GetBoolean());
            if (count < source.Sessions.Count) { Assert.Equal(source.Sessions.Count, record.GetProperty("total").GetInt32()); }
            return Task.CompletedTask;
        }));
        Assert.Equal(signing ? 1 : 0, calls);
    }

    [Fact]
    public async Task An_irreducible_record_is_rejected_without_using_the_signer()
    {
        var calls = 0;
        var publisher = new FleetPublisher(new FleetPublisherTests.Source(), new string('m', RosterFields.MaxRecordBytes),
            sign: record => { calls++; return record; });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.PublishAsync((_, _) => throw new InvalidOperationException("oversize record reached transport")));
        Assert.Equal(0, calls);
    }
}
