using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

// The shared protocol vectors (docs/protocol/vectors), replayed through the C# records: the PWA
// replays the same files (pwa/test/unit/protocol-vectors.test.mjs), so a field renamed or a limit
// moved in one implementation fails in the other.
public class ProtocolVectorTests
{
    static readonly string[] Names = ["sessions.json", "attachments.json", "push.json", "launch.json", "transfer.json", "update.json", "usage.json"];

    static IEnumerable<(string File, string Record, JsonObject Entry, JsonObject Vector)> All(string kind) =>
        from file in Names
        let json = VectorFiles.Read(file)
        from record in json["records"]!.AsObject()
        from vector in record.Value![kind]!.AsArray()
        select (file, record.Key, record.Value!.AsObject(), vector!.AsObject());

    public static TheoryData<string, string, string> Valid() => Rows("valid");
    public static TheoryData<string, string, string> Refused() => Rows("refused");

    static TheoryData<string, string, string> Rows(string kind)
    {
        var rows = new TheoryData<string, string, string>();
        foreach (var (file, record, _, vector) in All(kind)) { rows.Add(file, record, vector["name"]!.GetValue<string>()); }
        return rows;
    }

    static (JsonObject Entry, JsonObject Vector, JsonNode? Context) Find(string file, string record, string kind, string name)
    {
        var json = VectorFiles.Read(file);
        var entry = json["records"]![record]!.AsObject();
        var vector = entry[kind]!.AsArray().Single(v => v!["name"]!.GetValue<string>() == name)!.AsObject();
        // A vector names its context; the file holds it once.
        var context = vector["context"]?.GetValue<string>() is { } c ? json["contexts"]![c] ?? throw new InvalidOperationException($"{file} has no context {c}") : null;
        return (entry, vector, context);
    }

    static Type TypeOf(string record) =>
        typeof(Session).Assembly.GetType($"AgentBridge.Protocol.{record}") ?? throw new InvalidOperationException($"no record is called {record}");

    [Fact]
    public void The_committed_vectors_are_the_ones_the_records_generate()
    {
        var stale = VectorFiles.Stale(ProtocolVectors.Files());
        Assert.True(stale.Count == 0, $"docs/protocol/vectors/{string.Join(", ", stale)} differ from what {ProtocolVectors.Generator} generates. " +
            $"If the change is meant, regenerate them ({VectorFiles.UpdateVariable}=1, docs/protocol/vectors/README.md) and commit them with it.");
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void A_valid_vector_reads_into_its_record_and_writes_back_as_it_reads(string file, string record, string name)
    {
        var (entry, vector, context) = Find(file, record, "valid", name);
        var value = vector["json"]!.Deserialize(TypeOf(record), ProtocolJson.Options)!;

        var back = JsonSerializer.SerializeToNode(value, TypeOf(record), ProtocolJson.Options);
        var expected = vector["reads"] ?? vector["json"];
        Assert.True(JsonNode.DeepEquals(expected, back), $"{record}/{name} reads back as {back?.ToJsonString()}");
        if (entry["check"]?.GetValue<string>() is { } check) { Assert.Null(VectorChecks.Run(check, value, context)); }
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_refused_vector_is_refused_for_the_reason_it_gives(string file, string record, string name)
    {
        var (_, vector, context) = Find(file, record, "refused", name);
        var json = vector["json"]!.ToJsonString();
        var by = vector["refusedBy"]!.GetValue<string>();

        if (by == "parse")
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, TypeOf(record), ProtocolJson.Options));
            return;
        }
        var value = JsonSerializer.Deserialize(json, TypeOf(record), ProtocolJson.Options)!;
        Assert.Equal(vector["reason"]!.GetValue<string>(), VectorChecks.Run(by, value, context));
    }

    [Fact]
    public void Every_record_the_protocol_defines_has_valid_vectors()
    {
        var covered = Names.SelectMany(f => VectorFiles.Read(f)["records"]!.AsObject()
            .Where(r => r.Value!["valid"]!.AsArray().Count > 0).Select(r => r.Key)).ToHashSet();
        // A record is a class the compiler gave a <Clone>$, which only records have.
        var records = typeof(Session).Assembly.GetExportedTypes()
            .Where(t => t.GetMethod("<Clone>$") is not null)
            .Select(t => t.Name).Order().ToList();

        var missing = records.Where(r => !covered.Contains(r)).ToList();
        Assert.True(missing.Count == 0, $"no valid vectors for {string.Join(", ", missing)}: add them to {ProtocolVectors.Generator}");
    }

    [Fact]
    public void Every_known_difference_names_a_vector_of_its_file_and_an_issue()
    {
        foreach (var file in Names)
        {
            var json = VectorFiles.Read(file);
            foreach (var d in json["knownDifferences"]!.AsArray())
            {
                var key = d!["vector"]!.GetValue<string>().Split('/');
                var record = json["records"]![key[0]];
                Assert.True(record is not null && new[] { "valid", "refused" }.Any(k => record[k]!.AsArray().Any(v => v!["name"]!.GetValue<string>() == key[1])), $"{file} has no vector {d["vector"]}");
                Assert.True(d["issue"]!.GetValue<int>() > 0);
                Assert.Contains(d["differs"]!.GetValue<string>(), new[] { "csharp", "pwa" });
                Assert.Contains(d["step"]!.GetValue<string>(), new[] { "read", "check", "build", "sign" });
            }
        }
    }
}
