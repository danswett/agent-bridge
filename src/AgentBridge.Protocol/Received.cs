using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace AgentBridge.Protocol;

// Why a field is never read leniently (docs/protocol/v1-sessions.md, "Malformed records").
// Identity: what the record is filed and acted on under (an id, a field's name). Security: what a
// person decides on or what proves who asked (an approval's text and choices, a folder to trust,
// a permission mode, a signature).
public enum Strictness { Identity, Security }

// A field a reader refuses rather than defaults: missing (where the record requires it) or of the
// wrong JSON type, the record holding it is dropped, never filled in with a guess.
[AttributeUsage(AttributeTargets.Property)]
public sealed class StrictAttribute(Strictness why) : Attribute
{
    public Strictness Why { get; } = why;
}

// A record that could not be read under the rule; Path says where (JSONPath, "$.question.id").
public sealed class MalformedRecordException(string path, string why)
    : JsonException($"{path}: {why}", path, null, null);

// A host record as a client reads it (docs/protocol/v1-sessions.md, "Malformed records"). A host
// that is broken, older than its spec or hostile may send a number where a string belongs or leave
// a required field out. ProtocolJson.Options refuses the whole message then, which the PWA never
// did and which ended the Windows app's event pump (#446). Here instead, before the strict read:
//   - a [Strict] field missing (where required) or of the wrong type drops the record holding it;
//   - any other field of the wrong type, or null, reads as absent, and a required one missing
//     takes its type's default ("", 0, false, [], the enum's [UnknownAs] value, an empty record);
//   - an entry of a list or map that cannot be read is left out, unless the list is [Strict],
//     when the record holding it is dropped;
//   - a record a field of another holds that cannot be read is absent, as that field would be.
// Only a top-level record that cannot be read is refused, as a MalformedRecordException.
public static class Received
{
    public static T Read<T>(JsonElement data) where T : class => (T)Read(data, typeof(T));

    public static object Read(JsonElement data, Type type) => Read(data, type, out _);

    // exact: nothing was left out or filled in. A record read past a field it could not read is
    // still what the spec says to show, but it is no evidence: the app's updater does not take a
    // session whose status was filled in as idle as proof that its host may be restarted.
    public static T Read<T>(JsonElement data, out bool exact) where T : class => (T)Read(data, typeof(T), out exact);

    public static object Read(JsonElement data, Type type, out bool exact)
    {
        lossy = false;
        var read = Strictly(Clean(data, type, "$", strict: false), type, "$") ?? throw new MalformedRecordException("$", "no record");
        exact = !lossy;
        return read;
    }

    // Set while cleaning when anything is left out or defaulted. The cleaning is synchronous and
    // never re-enters Read, so one flag per thread is the whole read's.
    [ThreadStatic] static bool lossy;

    // The strict read, with every way it can fail made the one way a reader handles: a converter
    // that cannot decode its text throws InvalidOperationException or FormatException, which a
    // caller catching JsonException (the launch page) never saw.
    static object? Strictly(JsonNode? node, Type type, string path)
    {
        try { return node.Deserialize(type, ProtocolJson.Options); }
        catch (Exception x) when (x is InvalidOperationException or FormatException or NotSupportedException or ArgumentException) { throw Bad(path, "unreadable"); }
    }

    // A string's text. One holding half a surrogate pair ("\uD800") is valid JSON that .NET
    // cannot decode (GetString throws InvalidOperationException): it is unreadable, like a number
    // where text belongs.
    static string Text(JsonElement e, string path)
    {
        if (e.ValueKind != JsonValueKind.String) { throw Bad(path, "not a string"); }
        try { return e.GetString()!; }
        catch (InvalidOperationException) { throw Bad(path, "not text"); }
    }

    static JsonNode? Clean(JsonElement e, Type type, string path, bool strict)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(JsonElement)) { return Decodable(e, path); }
        if (t == typeof(string)) { return JsonValue.Create(Text(e, path)); }
        if (t == typeof(bool)) { return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValue.Create(e.GetBoolean()) : throw Bad(path, "not a boolean"); }
        // An enum string the reader does not know is the converter's to read ([UnknownAs], as the
        // spec says); anything but a string is not an enum value at all.
        if (t.IsEnum) { return JsonValue.Create(Text(e, path)); }
        if (t == typeof(DateTimeOffset))
        {
            var s = Text(e, path);
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _) ? JsonValue.Create(s) : throw Bad(path, "not a time");
        }
        if (Numbers.TryGetValue(t, out var fits)) { return e.ValueKind == JsonValueKind.Number && fits(e) ? JsonNode.Parse(e.GetRawText()) : throw Bad(path, $"not a {t.Name}"); }
        var info = Meta.GetTypeInfo(t);
        return info.Kind switch
        {
            JsonTypeInfoKind.Object => CleanObject(e, info, path),
            JsonTypeInfoKind.Enumerable => CleanList(e, info.ElementType!, path, strict),
            JsonTypeInfoKind.Dictionary => CleanMap(e, info.ElementType!, path, strict),
            // A type with a converter of its own reads itself, strictly, here: what it cannot read
            // is this field unreadable, not the whole message.
            _ => Converted(e, t, path),
        };
    }

    static JsonNode? Converted(JsonElement e, Type t, string path)
    {
        try { _ = e.Deserialize(t, ProtocolJson.Options); }
        catch (Exception x) when (x is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException) { throw Bad(path, "unreadable"); }
        return JsonNode.Parse(e.GetRawText());
    }

    // The records' shapes as ProtocolJson.Options reads them, from a copy of it, so asking for them
    // never freezes the shared options before their first use.
    // Read-only, so the shapes it builds are cached rather than rebuilt for every record read.
    static readonly JsonSerializerOptions Meta = Frozen(new(ProtocolJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });

    static JsonSerializerOptions Frozen(JsonSerializerOptions o)
    {
        o.MakeReadOnly();
        return o;
    }

    static readonly Dictionary<Type, Func<JsonElement, bool>> Numbers = new()
    {
        [typeof(int)] = e => e.TryGetInt32(out _),
        [typeof(long)] = e => e.TryGetInt64(out _),
        [typeof(short)] = e => e.TryGetInt16(out _),
        [typeof(byte)] = e => e.TryGetByte(out _),
        [typeof(uint)] = e => e.TryGetUInt32(out _),
        [typeof(ulong)] = e => e.TryGetUInt64(out _),
        [typeof(double)] = e => e.TryGetDouble(out var d) && double.IsFinite(d),
        [typeof(float)] = e => e.TryGetSingle(out var f) && float.IsFinite(f),
        [typeof(decimal)] = e => e.TryGetDecimal(out _),
    };

    static JsonObject CleanObject(JsonElement e, JsonTypeInfo info, string path)
    {
        if (e.ValueKind != JsonValueKind.Object) { throw Bad(path, "not an object"); }
        // Matched as ProtocolJson.Options reads them: case-insensitively, the last of a repeated
        // name winning.
        var given = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in e.EnumerateObject())
        {
            // A name .NET cannot decode is no field of the record's.
            if (Name(p) is { } name) { given[name] = p.Value; }
        }
        var o = new JsonObject();
        foreach (var f in Shape(info))
        {
            var at = $"{path}.{f.Name}";
            if (given.TryGetValue(f.Name, out var v) && v.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    o[f.Name] = Clean(v, f.Type, at, f.Strict);
                    continue;
                }
                catch (MalformedRecordException) when (!f.Strict) { lossy = true; }
            }
            if (f.Strict && f.Required) { throw Bad(at, "missing"); }
            if (f.Required)
            {
                lossy = true;
                o[f.Name] = Default(f.Type, at);
            }
        }
        return o;
    }

    static JsonArray CleanList(JsonElement e, Type item, string path, bool strict)
    {
        if (e.ValueKind != JsonValueKind.Array) { throw Bad(path, "not a list"); }
        var a = new JsonArray();
        var i = 0;
        foreach (var x in e.EnumerateArray())
        {
            var at = $"{path}[{i++}]";
            try { a.Add(x.ValueKind == JsonValueKind.Null ? throw Bad(at, "null") : Clean(x, item, at, strict: false)); }
            catch (MalformedRecordException) when (!strict) { lossy = true; }
        }
        return a;
    }

    static JsonObject CleanMap(JsonElement e, Type value, string path, bool strict)
    {
        if (e.ValueKind != JsonValueKind.Object) { throw Bad(path, "not an object"); }
        var o = new JsonObject();
        foreach (var p in e.EnumerateObject())
        {
            try
            {
                // A map's keys are the host's data, so they stay out of the path, which is logged.
                var at = $"{path}.*";
                var key = Name(p) ?? throw Bad(at, "a key that is not text");
                o[key] = p.Value.ValueKind == JsonValueKind.Null ? throw Bad(at, "null") : Clean(p.Value, value, at, strict: false);
            }
            catch (MalformedRecordException) when (!strict) { lossy = true; }
        }
        return o;
    }

    // JSON of any shape (a field's default), checked for text .NET cannot decode, which otherwise
    // throws only when the strict read writes it out, refusing the whole message for one field.
    static JsonNode? Decodable(JsonElement e, string path)
    {
        Check(e, path);
        return JsonNode.Parse(e.GetRawText());

        static void Check(JsonElement e, string path)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: _ = Text(e, path); break;
                case JsonValueKind.Array:
                    foreach (var x in e.EnumerateArray()) { Check(x, path); }
                    break;
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (Name(p) is null) { throw Bad(path, "a key that is not text"); }
                        Check(p.Value, path);
                    }
                    break;
            }
        }
    }

    static string? Name(JsonProperty p)
    {
        try { return p.Name; }
        catch (InvalidOperationException) { return null; }
    }

    // What a required field reads as when it is missing or unreadable.
    static JsonNode? Default(Type type, string path)
    {
        if (Nullable.GetUnderlyingType(type) is not null) { return null; }
        if (type == typeof(string)) { return ""; }
        if (type == typeof(bool)) { return false; }
        if (Numbers.ContainsKey(type)) { return 0; }
        if (type.IsEnum)
        {
            var unknown = type.GetCustomAttribute<UnknownAsAttribute>()?.Value ?? Activator.CreateInstance(type)!;
            return JsonNamingPolicy.CamelCase.ConvertName(unknown.ToString()!);
        }
        if (type == typeof(DateTimeOffset)) { return "0001-01-01T00:00:00.000Z"; }
        if (type == typeof(JsonElement)) { return null; }
        var info = Meta.GetTypeInfo(type);
        return info.Kind switch
        {
            JsonTypeInfoKind.Enumerable => new JsonArray(),
            JsonTypeInfoKind.Dictionary => new JsonObject(),
            // A record of nothing but defaults, if it can be one: one with a [Strict] field of its
            // own cannot, and then neither can the record that holds it.
            JsonTypeInfoKind.Object => CleanObject(EmptyObject, info, path),
            _ => throw Bad(path, "missing"),
        };
    }

    static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    static MalformedRecordException Bad(string path, string why) => new(path, why);

    sealed record FieldShape(string Name, Type Type, bool Required, bool Strict);

    static readonly ConcurrentDictionary<Type, FieldShape[]> Shapes = new();

    // The fields a record is read from: those the strict read sets, through a setter or its
    // constructor. Required: a `required` member, or a constructor parameter with no default.
    static FieldShape[] Shape(JsonTypeInfo info) => Shapes.GetOrAdd(info.Type, _ =>
    [
        .. info.Properties
            .Where(p => p.Set is not null || p.AssociatedParameter is not null)
            .Select(p => new FieldShape(
                p.Name,
                p.PropertyType,
                p.IsRequired || p.AssociatedParameter is { HasDefaultValue: false },
                (p.AttributeProvider as MemberInfo)?.GetCustomAttribute<StrictAttribute>() is not null)),
    ]);
}
