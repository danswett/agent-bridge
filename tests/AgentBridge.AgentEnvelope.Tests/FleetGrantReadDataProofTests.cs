using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetGrantReadDataProofTests : IDisposable
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    static readonly DateTimeOffset At = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FleetGrantReadDataContext context = new(new("account", "default",
        "0123456789abcdef0123456789abcdef", "grant.read", Guid.NewGuid()), 0, 0, -1);
    byte[] Point { get { var q = key.ExportParameters(false).Q; return [4, .. q.X!, .. q.Y!]; } }
    public void Dispose() => key.Dispose();
    FleetGrantReadDataRow Row(int id = 1)
    {
        var subject = new { Account = "account", SourceHost = context.Context.Receiver,
            Session = Guid.NewGuid().ToString("D"), Credential = "AQ",
            ProcessInstance = new { Pid = 42, StartedAt = At } };
        var doc = JsonSerializer.SerializeToElement(new
        {
            Version = 1, Id = Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"), Account = "account",
            Issuer = new { Account = "account", Device = context.Context.Receiver, PersonProof = "AQ", IssuedAt = At, Digest = new string('a', 64) },
            Audience = new { Device = context.Context.Receiver, Profile = "default" }, Subjects = new[] { subject },
            Actions = new[] { "launch" }, Scope = new { Machines = new[] { context.Context.Receiver },
                Workspaces = new[] { "repo" }, Agents = new[] { "agency" }, Models = new[] { "gpt-6.1-sol" },
                Profiles = new[] { "default" }, Efforts = new[] { "high" }, Contexts = new[] { "default" }, Permissions = new[] { "ask" } },
            Caps = new { Hourly = 1, Daily = 2, Live = 1 }, NotBefore = At, Generation = 1, ExpiresAt = At.AddHours(1),
            Roles = new[] { new { Name = "chief", Subject = subject } },
            StopTargets = new[] { new { subject.Session, subject.ProcessInstance } }, AllowAllLaunch = false
        }, Wire);
        return new(doc, JsonSerializer.SerializeToElement(new { Hour = 0, Day = 0, Pending = 0, Live = 0,
            Requests = new[] { Guid.NewGuid().ToString("D") }, LastCaller = "caller", LastOutcome = "ok",
            ObservedAt = At, PendingRequests = Array.Empty<string>(), LiveSessions = Array.Empty<string>() }, Wire), false);
    }
    FleetGrantReadDataPage Page() => new(0, 0, 11, At, [Row()], -1, 1);
    FleetGrantReadDataResult Seal() => FleetGrantReadDataProof.Seal(Page(), context, new EcdsaEnvelopeSigner(key));

    [Fact]
    public void Initial_short_continuation_empty_final_and_sixteen_row_pages_round_trip_with_retained_selectors()
    {
        var result = Seal();
        var parsed = FleetGrantReadDataProof.Parse(JsonSerializer.SerializeToElement(result, Wire));
        Assert.Equal(11, FleetGrantReadDataProof.Verify(parsed, Point, context).Generation);
        foreach (var (offset, count, total) in new[] { (0, 0, 0), (0, 1, 3), (1, 1, 3), (3, 0, 3), (0, 16, 16) })
        {
            var expected = context with { Offset = offset, Generation = offset == 0 ? 0 : 11, Total = offset == 0 ? -1 : total };
            var p = new FleetGrantReadDataPage(offset, expected.Generation, 11, At,
                [.. Enumerable.Range(1, count).Select(i => Row(i))], offset + count == total ? -1 : offset + count, total);
            Assert.Equal(count, FleetGrantReadDataProof.Verify(FleetGrantReadDataProof.Seal(p, expected,
                new EcdsaEnvelopeSigner(key)), Point, expected).Grants.Length);
        }
    }

    [Theory]
    [InlineData(0, 1, 2, -1)]
    [InlineData(0, 1, 1, 1)]
    [InlineData(0, 2, 1, -1)]
    [InlineData(1, 0, 3, 1)]
    [InlineData(1, 1, 3, 1)]
    [InlineData(1, 1, 3, 257)]
    [InlineData(1, 1, 3, 3)]
    [InlineData(0, 17, 17, -1)]
    [InlineData(0, 0, 257, -1)]
    public void Invalid_page_bounds_and_false_end_markers_refuse(int offset, int count, int total, int next)
    {
        var expected = context with { Offset = offset, Generation = offset == 0 ? 0 : 11, Total = offset == 0 ? -1 : total };
        var p = new FleetGrantReadDataPage(offset, expected.Generation, 11, At,
            [.. Enumerable.Range(1, count).Select(i => Row(i))], next, total);
        Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Seal(p, expected, new EcdsaEnvelopeSigner(key)));
    }

    [Fact]
    public void Zero_generation_changed_snapshot_total_duplicate_and_unsorted_rows_refuse()
    {
        var p = Page();
        foreach (var bad in new[] { p with { Generation = 0 }, p with { Grants = [Row(1), Row(1)], Total = 2 },
            p with { Grants = [Row(2), Row(1)], Total = 2 }, p with { Grants = default } })
        { Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Seal(bad, context, new EcdsaEnvelopeSigner(key))); }
        var expected = context with { Offset = 1, Generation = 11, Total = 3 };
        foreach (var bad in new[] { p with { Offset = 1, RequestedGeneration = 11, Generation = 12, Total = 3, Next = 2 },
            p with { Offset = 1, RequestedGeneration = 11, Total = 4, Next = 2 } })
        { Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Seal(bad, expected, new EcdsaEnvelopeSigner(key))); }
    }

    [Fact]
    public void Every_page_row_document_and_counter_field_is_bound_in_the_seal()
    {
        var result = Seal(); var original = JsonSerializer.SerializeToNode(result.Page, Wire)!;
        void Check(JsonNode? node, string[] path)
        {
            if (node is JsonObject obj) { foreach (var f in obj) { Check(f.Value, [.. path, f.Key]); } return; }
            if (node is JsonArray arr)
            { for (var i = 0; i < arr.Count; i++) { Check(arr[i], [.. path, i.ToString()]); } }
            var changed = JsonNode.Parse(original.ToJsonString())!; var parent = changed;
            foreach (var part in path[..^1]) { parent = parent is JsonArray a ? a[int.Parse(part)]! : parent[part]!; }
            JsonNode? value = node is JsonArray array ? (array.Count == 0 ? new JsonArray("changed") : new JsonArray())
                : node?.GetValueKind() switch
                {
                    JsonValueKind.Number => JsonValue.Create(node.GetValue<long>() + 1),
                    JsonValueKind.True => JsonValue.Create(false), JsonValueKind.False => JsonValue.Create(true),
                    JsonValueKind.String => JsonValue.Create(DateTimeOffset.TryParse(node.GetValue<string>(), out var time)
                        ? time.AddMinutes(1).ToString("O") : Guid.TryParse(node.GetValue<string>(), out _)
                        ? Guid.NewGuid().ToString("D") : node.GetValue<string>() is { Length: 64 }
                        ? new string('b', 64) : node.GetValue<string>() + "-changed"),
                    _ => JsonValue.Create("changed")
                };
            if (parent is JsonArray indexed) { indexed[int.Parse(path[^1])] = value; } else { parent[path[^1]] = value; }
            Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Verify(
                result with { Page = changed.Deserialize<FleetGrantReadDataPage>(Wire)! }, Point, context));
        }
        Check(original, []);
    }

    [Fact]
    public void Missing_extra_duplicate_wrong_case_and_null_fields_refuse_in_the_entire_reply()
    {
        var result = Seal(); var root = JsonSerializer.SerializeToElement(result, Wire);
        void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array) { foreach (var item in element.EnumerateArray()) { Check(item); } return; }
            if (element.ValueKind != JsonValueKind.Object) { return; }
            var raw = element.GetRawText();
            void Refuse(string bad)
            { using var doc = JsonDocument.Parse(root.GetRawText().Replace(raw, bad));
                Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Parse(doc.RootElement)); }
            Refuse(raw[..^1] + ",\"extra\":1}");
            foreach (var f in element.EnumerateObject())
            {
                Refuse(raw[..^1] + ",\"" + f.Name + "\":" + f.Value.GetRawText() + "}");
                var obj = JsonNode.Parse(raw)!.AsObject(); obj.Remove(f.Name); Refuse(obj.ToJsonString());
                obj = JsonNode.Parse(raw)!.AsObject(); obj[f.Name] = null;
                if (f.Name is not ("lastCaller" or "lastOutcome")) { Refuse(obj.ToJsonString()); }
                obj = JsonNode.Parse(raw)!.AsObject(); var value = obj[f.Name]; obj.Remove(f.Name);
                obj[char.ToUpperInvariant(f.Name[0]) + f.Name[1..]] = value; Refuse(obj.ToJsonString());
                Check(f.Value);
            }
        }
        Check(root);
        Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Parse(default));
    }

    [Fact]
    public void Only_explicit_diagnostic_nulls_are_allowed_and_input_documents_are_snapshotted()
    {
        var row = Row(); var counters = JsonNode.Parse(row.Counters.GetRawText())!;
        counters["lastCaller"] = null; counters["lastOutcome"] = null;
        FleetGrantReadDataResult result;
        using (var doc = JsonDocument.Parse(counters.ToJsonString()))
        { result = FleetGrantReadDataProof.Seal(Page() with { Grants = [row with { Counters = doc.RootElement }] },
            context, new EcdsaEnvelopeSigner(key)); }
        var parsed = FleetGrantReadDataProof.Parse(JsonSerializer.SerializeToElement(result, Wire));
        Assert.Equal(JsonValueKind.Null, FleetGrantReadDataProof.Verify(parsed, Point, context).Grants[0].Counters.GetProperty("lastCaller").ValueKind);
    }

    [Fact]
    public void Wrong_caller_selectors_context_key_action_problem_domain_or_oversized_data_refuse()
    {
        var result = Seal();
        foreach (var expected in new[] { context with { Offset = 1 }, context with { Generation = 12 }, context with { Total = 1 },
            context with { Context = context.Context with { Account = "other" } }, context with { Context = context.Context with { Profile = "other" } },
            context with { Context = context.Context with { Receiver = "fedcba9876543210fedcba9876543210" } },
            context with { Context = context.Context with { Action = "grant.prepareIssue" } },
            context with { Context = context.Context with { Request = Guid.NewGuid() } } })
        { Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Verify(result, Point, expected)); }
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256); var q = other.ExportParameters(false).Q;
        Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Verify(result, [4, .. q.X!, .. q.Y!], context));
        var problem = FleetGrantReadProof.Seal(FleetGrantReadProof.Reply(FleetGrantReadProblem.None),
            context.Context, new EcdsaEnvelopeSigner(key));
        Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Verify(result with { Proof = problem.Proof }, Point, context));
        Assert.Throws<InvalidDataException>(() => FleetGrantReadProof.Verify(new(FleetGrantReadProof.Reply(FleetGrantReadProblem.None),
            result.Proof), Point, context.Context));
        foreach (var domain in new[] { FleetGrantPrepareIssueProof.Domain, FleetGrantReceiptProof.Domain })
        {
            var proof = FleetGrantProof.Seal(Framing.F(Framing.Label(domain), [1]), context.Context, new EcdsaEnvelopeSigner(key));
            Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Verify(result with { Proof = proof }, Point, context));
        }
        Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Verify(null!, Point, context));
        Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Verify(result with { Proof = null! }, Point, context));
        var counters = JsonNode.Parse(result.Page.Grants[0].Counters.GetRawText())!; counters["lastOutcome"] = new string('x', 31 * 1024);
        Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Seal(result.Page with
        { Grants = [result.Page.Grants[0] with { Counters = JsonSerializer.SerializeToElement(counters, Wire) }] }, context, new EcdsaEnvelopeSigner(key)));
    }

    [Fact]
    public void The_data_frame_has_its_own_domain_fixed_page_order_and_complete_canonical_row_subframes()
    {
        var result = Seal(); var p = result.Page; var r = p.Grants[0];
        byte[] N(long value) { var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return bytes; }
        var row = Framing.F(Framing.Utf8(r.Document.GetRawText()), Framing.Utf8(r.Counters.GetRawText()), [0]);
        Assert.Equal(Framing.F(Framing.Label("agent-bridge fleet read data v1"), N(0), N(0), N(11),
            Framing.Utf8(At.ToString("O")), N(1), Framing.F(row), N(-1)), FleetGrantProof.Verify(result.Proof, Point, context.Context));
        var body = Codec.FromBase64Url(result.Proof.Body, "body"); body[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => FleetGrantReadDataProof.Verify(result with
        { Proof = result.Proof with { Body = Codec.Base64Url(body) } }, Point, context));
    }
}
