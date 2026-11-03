using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentBridge.Protocol;

// Builds the vector files from the records: every valid vector is a record serialized with
// ProtocolJson.Options, so a field renamed or a limit moved here moves the vectors too, and the
// other implementations' replays fail until they follow. A refused vector carries why: "parse"
// when the record cannot be read at all, or a check's own words (VectorChecks).
public sealed class VectorCatalog
{
    readonly List<VectorFileBuilder> files = [];
    readonly List<KnownDifference> known = [];

    public VectorFileBuilder File(string name, string spec, string about)
    {
        var f = new VectorFileBuilder(name, spec, about);
        files.Add(f);
        return f;
    }

    public void Differs(KnownDifference d) => known.Add(d);

    public IReadOnlyDictionary<string, JsonObject> Build()
    {
        var built = files.ToDictionary(f => f.Name, f => f.Build(), StringComparer.Ordinal);
        foreach (var d in known)
        {
            var record = d.Vector.Split('/')[0];
            var file = files.SingleOrDefault(f => f.Has(d.Vector))
                ?? throw new InvalidOperationException($"the known difference names {d.Vector}, which no file has (is {record} spelled right?)");
            var list = built[file.Name]["knownDifferences"]!.AsArray();
            list.Add(JsonSerializer.SerializeToNode(d, ProtocolJson.Options));
        }
        return built;
    }
}

// Where an implementation does not do what a vector says, and the issue that decides which side
// moves. The vector's expectation is what the C# records do, since they generate it; that does not
// make C# right. differs names the implementation that departs from it today, and step what it
// does with the record there: read it, check it, build it or sign it.
public sealed record KnownDifference(string Vector, int Issue, string Differs, string Step, string Csharp, string Pwa);

public sealed class VectorFileBuilder(string name, string spec, string about)
{
    readonly List<RecordBuilder> records = [];
    readonly Dictionary<string, JsonNode> contexts = new(StringComparer.Ordinal);
    public string Name { get; } = name;

    // What a check needs beside the record (the question an answer answers, say), named once and
    // referred to by name from each vector that needs it.
    public VectorFileBuilder Context(string name, object value)
    {
        contexts.Add(name, JsonSerializer.SerializeToNode(value, ProtocolJson.Options)!);
        return this;
    }

    public JsonNode Context(string name) =>
        contexts.TryGetValue(name, out var c) ? c : throw new InvalidOperationException($"{Name} has no context {name}");

    public RecordBuilder<T> Record<T>(string direction, string? check = null)
    {
        if (records.Any(r => r.Type == typeof(T))) { throw new InvalidOperationException($"{typeof(T).Name} is in {Name} twice"); }
        var r = new RecordBuilder<T>(this, direction, check);
        records.Add(r);
        return r;
    }

    public bool Has(string vector) => records.Any(r => r.Has(vector));

    public JsonObject Build()
    {
        var o = new JsonObject
        {
            ["about"] = about,
            ["spec"] = spec,
            ["generatedBy"] = ProtocolVectors.Generator,
            ["contexts"] = new JsonObject(contexts.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value.DeepClone()))),
            ["records"] = new JsonObject(records.Select(r => KeyValuePair.Create(r.Type.Name, (JsonNode?)r.Build()))),
            ["knownDifferences"] = new JsonArray(),
        };
        return o;
    }
}

public abstract class RecordBuilder
{
    public abstract Type Type { get; }
    public abstract bool Has(string vector);
    public abstract JsonObject Build();
}

public sealed class RecordBuilder<T>(VectorFileBuilder file, string direction, string? check) : RecordBuilder
{
    readonly List<JsonObject> valid = [];
    readonly List<JsonObject> refused = [];
    readonly HashSet<string> names = new(StringComparer.Ordinal);
    bool built;

    public override Type Type => typeof(T);

    public override bool Has(string vector) => vector.StartsWith(typeof(T).Name + "/", StringComparison.Ordinal) && names.Contains(vector[(typeof(T).Name.Length + 1)..]);

    static JsonNode Json(object? value) => JsonSerializer.SerializeToNode(value, ProtocolJson.Options)!;

    // A record as a host or client writes it.
    public RecordBuilder<T> Valid(string name, T value, string? context = null) => AddValid(name, Json(value), context);

    // JSON that is not what a writer sends, which a reader must still take: an enum value or a
    // field from a newer version. reads is what the record holds, written back.
    public RecordBuilder<T> Reads(string name, string json, string? context = null) => AddValid(name, JsonNode.Parse(json)!, context);

    public RecordBuilder<T> Refused(string name, T value, string? context = null) => AddRefused(name, Json(value), context);

    public RecordBuilder<T> Refused(string name, string json, string? context = null) => AddRefused(name, JsonNode.Parse(json)!, context);

    // The first valid vector, changed.
    public RecordBuilder<T> Refused(string name, Action<JsonObject> change, string? context = null)
    {
        var json = valid[0]["json"]!.DeepClone().AsObject();
        change(json);
        return AddRefused(name, json, context);
    }

    void Name(string name)
    {
        if (!names.Add(name)) { throw new InvalidOperationException($"{typeof(T).Name}/{name} twice"); }
    }

    RecordBuilder<T> AddValid(string name, JsonNode json, string? context)
    {
        Name(name);
        var ctx = context is null ? null : file.Context(context);
        var record = JsonSerializer.Deserialize<T>(json.ToJsonString(), ProtocolJson.Options)!;
        if (check is not null && VectorChecks.Run(check, record, ctx) is { } problem)
        {
            throw new InvalidOperationException($"{typeof(T).Name}/{name} is meant to be valid, but {check} says {problem}");
        }
        var entry = new JsonObject { ["name"] = name, ["json"] = json };
        if (context is not null) { entry["context"] = context; }
        var reads = Json(record);
        if (!JsonNode.DeepEquals(reads, json)) { entry["reads"] = reads; }
        valid.Add(entry);
        return this;
    }

    RecordBuilder<T> AddRefused(string name, JsonNode json, string? context)
    {
        Name(name);
        var ctx = context is null ? null : file.Context(context);
        var entry = new JsonObject { ["name"] = name, ["json"] = json };
        if (context is not null) { entry["context"] = context; }
        T record;
        try { record = JsonSerializer.Deserialize<T>(json.ToJsonString(), ProtocolJson.Options)!; }
        catch (JsonException)
        {
            entry["refusedBy"] = "parse";
            refused.Add(entry);
            return this;
        }
        var reason = check is null ? null : VectorChecks.Run(check, record!, ctx);
        entry["refusedBy"] = check;
        entry["reason"] = reason ?? throw new InvalidOperationException($"{typeof(T).Name}/{name} is meant to be refused, but {check ?? "reading it"} takes it");
        refused.Add(entry);
        return this;
    }

    static string JsonName(PropertyInfo p) =>
        p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);

    static IEnumerable<PropertyInfo> WireProperties() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null && p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract");

    // The refusals every record gets from its first valid vector: each required field left out,
    // and one field of the wrong JSON type.
    void Mechanical()
    {
        if (valid.Count == 0) { throw new InvalidOperationException($"{typeof(T).Name} has no valid vector"); }
        var first = valid[0]["json"]!.AsObject();
        foreach (var p in WireProperties().Where(p => p.GetCustomAttribute<RequiredMemberAttribute>() is not null))
        {
            var key = JsonName(p);
            if (!first.ContainsKey(key)) { throw new InvalidOperationException($"{typeof(T).Name}'s first vector lacks required {key}"); }
            Refused($"missing-{key}", j => j.Remove(key));
        }
        var wire = WireProperties().ToList();
        var present = wire.Where(p => first.ContainsKey(JsonName(p))).ToList();
        static Type Bare(PropertyInfo p) => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        // A string field by preference, present in the first vector or else added to it, since a
        // minimal first vector would otherwise leave a record with none. Never an enum, which reads
        // anything it does not know as its fallback, nor JSON of any kind.
        var wrong = present.FirstOrDefault(p => p.PropertyType == typeof(string))
            ?? wire.FirstOrDefault(p => p.PropertyType == typeof(string))
            ?? present.FirstOrDefault(p => Bare(p) is var t && !t.IsEnum && t != typeof(JsonElement) && t != typeof(DateTimeOffset));
        if (wrong is null) { return; }
        var type = Bare(wrong);
        JsonNode value = type == typeof(string) ? JsonValue.Create(42)
            : type == typeof(bool) ? JsonValue.Create("yes")
            : type.IsPrimitive ? JsonValue.Create("many")
            : JsonValue.Create("wrong");
        var name = JsonName(wrong);
        Refused($"wrong-type-{name}", j => j[name] = value.DeepClone());
    }

    public override JsonObject Build()
    {
        if (!built)
        {
            Mechanical();
            built = true;
        }
        var o = new JsonObject { ["direction"] = direction };
        if (check is not null) { o["check"] = check; }
        o["valid"] = new JsonArray([.. valid.Select(v => v.DeepClone())]);
        o["refused"] = new JsonArray([.. refused.Select(v => v.DeepClone())]);
        return o;
    }
}
