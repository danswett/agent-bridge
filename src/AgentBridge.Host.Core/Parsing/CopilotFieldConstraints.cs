using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// The constraints of one field of Copilot's ask_user form, for the input CopilotFieldSchema says
// the CLI draws (#766). Copilot CLI 1.0.105 draws that form with enforceSchemaConstraints off
// (`Uxn`: its elicitation has no source, so `elicitationSource !== JD` is false), so it checks
// none of them and records whatever is typed: the host is the only check. Read here for the
// question a client is shown (QuestionMapper) and for the keys (CopilotForm), alike.
//
// Each applies to the input its keyword is for, as JSON Schema means it: bounds, `integer` and
// `multipleOf` to a number box; length, `pattern` and `format` to a text box; `minItems` and
// `maxItems` to a checkbox list. Problem is set when a keyword is there but cannot be checked
// exactly (a bound that is not a number JavaScript holds exactly, a length that is not a whole
// number, a pattern that cannot be read as JavaScript reads it), or when the default a field
// opens with breaks them: such a field is answered at the machine. Patterns share the budget of
// the whole ask_user they are read for (PatternBudget), so an agent's slow patterns cost at most
// that much however many fields carry them.
public static class CopilotFieldConstraints
{
    public static (FieldConstraints? Constraints, string? Problem) Read(JsonElement p, PatternBudget budget)
    {
        if (p.ValueKind != JsonValueKind.Object) { return (null, null); }
        var (input, _, unread) = CopilotFieldSchema.Read(p);
        if (unread is not null) { return (null, null); }
        if (Unchecked(p, input) is { } keyword) { return (null, $"has a limit the host does not check (\"{keyword}\")"); }
        FieldConstraints c;
        string? problem = null;
        switch (input)
        {
            case CopilotFieldSchema.Input.Number:
                c = new FieldConstraints
                {
                    Minimum = Number(p, "minimum", ref problem),
                    Maximum = Number(p, "maximum", ref problem),
                    ExclusiveMinimum = Number(p, "exclusiveMinimum", ref problem),
                    ExclusiveMaximum = Number(p, "exclusiveMaximum", ref problem),
                    MultipleOf = Number(p, "multipleOf", ref problem),
                    Integer = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "integer" ? true : null,
                };
                if (c.MultipleOf is { } step && ExactDecimal.Parse(step.GetRawText()) is not { Sign: > 0 }) { problem ??= Uncheckable; }
                break;
            case CopilotFieldSchema.Input.Text:
                c = new FieldConstraints
                {
                    MinLength = Count(p, "minLength", ref problem),
                    MaxLength = Count(p, "maxLength", ref problem),
                    Pattern = Pattern(p, budget, ref problem),
                    Format = Format(p, ref problem),
                };
                break;
            case CopilotFieldSchema.Input.Multi:
                c = new FieldConstraints { MinItems = Count(p, "minItems", ref problem), MaxItems = Count(p, "maxItems", ref problem) };
                break;
            default:
                return (null, null);
        }
        if (problem is not null) { return (null, problem); }
        if (c == new FieldConstraints()) { return (null, null); }
        return Kept(p, input, c, budget) switch
        {
            null => (c, null),
            FieldConstraints.Unchecked => (null, Uncheckable),
            var broken => (null, $"has a default outside its own limits ({broken})"),
        };
    }

    // What a field may carry and still be answered from another device is decided from an
    // allow-list, never keyword by keyword: each list of keywords to refuse missed one, so a Yes/No
    // with `const: true` took No, a number's `anyOf` of 1 and 2 took 5, and a text box's
    // `allOf: [{ maxLength: 2 }]` was not measured (review of #844). Besides annotations, a field
    // carries only the keywords the host checks for the input the CLI draws; a list of choices only
    // the options it draws, each of its own `type`. Anything else, `anyOf`, `allOf`, `not`, `const`,
    // `if`, `dependent*` or an unknown keyword alike, is answered at the machine. A list's text or
    // number limits are among them: its options and Other words are not checked against them.
    static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "title", "description", "default", "examples", "$comment", "$schema", "$id", "$anchor",
        "deprecated", "readOnly", "writeOnly", "enumNames",
    };

    static readonly string[] TextKeywords = ["minLength", "maxLength", "pattern", "format"];
    static readonly string[] NumberKeywords = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf"];

    // The first keyword the host does not check, or null.
    static string? Unchecked(JsonElement p, CopilotFieldSchema.Input input)
    {
        string[] own = input switch
        {
            CopilotFieldSchema.Input.Text => TextKeywords,
            CopilotFieldSchema.Input.Number => NumberKeywords,
            CopilotFieldSchema.Input.Choice => ["enum", "oneOf"],
            CopilotFieldSchema.Input.Multi => ["items", "minItems", "maxItems", "uniqueItems"],
            _ => [],
        };
        if (Other(p, own) is { } keyword) { return keyword; }
        switch (input)
        {
            case CopilotFieldSchema.Input.Choice:
                // A string list records its option as it is, so one that is not a string breaks
                // its own `type`.
                if (Entries(p, "oneOf") is { } entry) { return entry; }
                if (!Within(p, "oneOf", "enum")) { return "enum"; }
                return Options(p, "oneOf", "enum").All(o => o.ValueKind == JsonValueKind.String) ? null : "type";
            case CopilotFieldSchema.Input.Multi when p.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object:
                if (Other(items, ["enum", "anyOf"]) is { } inner) { return $"items.{inner}"; }
                if (Entries(items, "anyOf") is { } itemEntry) { return $"items.{itemEntry}"; }
                if (!Within(items, "anyOf", "enum")) { return "items.enum"; }
                if (Has(items, "type", out var type) && !Options(items, "anyOf", "enum").All(o => OfType(o, type))) { return "items.type"; }
                // Two options of one value can both be ticked, which `uniqueItems` forbids.
                if (Has(p, "uniqueItems", out var unique) && unique.ValueKind != JsonValueKind.False)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    if (unique.ValueKind != JsonValueKind.True || Options(items, "anyOf", "enum").Any(o => Key(o) is { } k && !seen.Add(k))) { return "uniqueItems"; }
                }
                return null;
            default:
                return null;
        }
    }

    // A keyword that is neither `type`, an annotation nor one of these; or any but an annotation
    // set to null. The CLI reads a null keyword as absent, but to the schema `const: null` allows
    // only null, and a null elsewhere is no schema at all, so reading it as absent too let a Yes/No
    // with `const: null` take Yes (Copilot review of #844). An annotation may be null (a null
    // default opens the Other box, #700).
    static string? Other(JsonElement o, string[] own)
    {
        foreach (var k in o.EnumerateObject())
        {
            if (Annotations.Contains(k.Name)) { continue; }
            if (k.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { return k.Name; }
            if (k.Name == "type" || own.Contains(k.Name)) { continue; }
            return k.Name;
        }
        return null;
    }

    // An option entry (`{ const, title }`) carrying anything else. Its own `type` is a limit on its
    // `const` like any other: `{ const: "a", type: "number" }` is a branch nothing matches, so "a"
    // was offered and recorded against the schema (Quality's review of #844). One that names the
    // const's own JSON type says nothing more and is kept; any other sends the field to the machine.
    static string? Entries(JsonElement o, string name)
    {
        if (!Has(o, name, out var list) || list.ValueKind != JsonValueKind.Array) { return null; }
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) { continue; }
            if (Other(e, ["const"]) is { } k) { return $"{name}.{k}"; }
            if (e.TryGetProperty("type", out var type) && !(e.TryGetProperty("const", out var value) && OfType(value, type))) { return $"{name}.type"; }
        }
        return null;
    }

    // With both lists, the CLI draws the first and ignores the second, so each option drawn must be
    // one the second allows.
    // Matched by key, not pair by pair: a question is read on every refresh of every session, and
    // tens of thousands of options compared each with each held that up.
    static bool Within(JsonElement o, string consts, string values)
    {
        if (!(Has(o, consts, out _) && Has(o, values, out var v) && v.ValueKind == JsonValueKind.Array)) { return true; }
        var allowed = v.EnumerateArray().Select(Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return Options(o, consts, values).All(c => Key(c) is { } k && allowed.Contains(k));
    }

    // A scalar as CopilotFieldSchema.SameScalar compares it: equal keys exactly when SameScalar
    // holds. Null for one equal to nothing, as a number it cannot read exactly is.
    static string? Key(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => "s" + v.GetString(),
        JsonValueKind.Number => CopilotFieldSchema.ExactNumber(v.GetRawText()) is { } n ? "n" + n : null,
        JsonValueKind.True => "t",
        JsonValueKind.False => "f",
        JsonValueKind.Null => "z",
        _ => null,
    };

    // What the form itself carries besides its fields: anything but annotations, "required" and
    // "additionalProperties" (answers name only its fields) limits them in a way the host does not
    // check, such as an `allOf` or `if` across fields, so the question is answered at the machine.
    public static string? FormUnchecked(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) { return null; }
        if (Has(schema, "type", out var type) && !(type.ValueKind == JsonValueKind.String && type.GetString() == "object")) { return "type"; }
        if (Other(schema, ["properties", "required", "additionalProperties"]) is { } keyword) { return keyword; }
        // Every field drawn once, as an object, and `required` naming only those: a name listed
        // twice, a field that is not an object, or a required one that is not drawn would leave an
        // answer the host accepts that the form forbids (Copilot review of #844).
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        if (Has(schema, "properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object) { return "properties"; }
            foreach (var p in properties.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object || !drawn.Add(p.Name)) { return "properties"; }
            }
        }
        if (Has(schema, "required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array) { return "required"; }
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String || !drawn.Contains(name.GetString()!)) { return "required"; }
            }
        }
        return null;
    }

    // The options as CopilotFieldSchema takes them: the first list's `const`s, else the second.
    static IEnumerable<JsonElement> Options(JsonElement o, string consts, string values)
    {
        if (Has(o, consts, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("const", out _)).Select(e => e.GetProperty("const"));
        }
        return Has(o, values, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    }

    static bool OfType(JsonElement value, JsonElement type) => type.ValueKind == JsonValueKind.String && type.GetString() switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && ExactDecimal.Parse(value.GetRawText()) is { IsInteger: true },
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };

    const string Uncheckable = "has limits the host cannot check exactly";

    // The default a text or number box opens with, which is the only answer it can be given from
    // another device (#662), checked as what it would be recorded as: a number box's as the number
    // the CLI holds, a text box's as the words in it. One the host cannot read exactly is refused
    // elsewhere (CopilotForm.Keeps), so it is not judged here.
    static string? Kept(JsonElement p, CopilotFieldSchema.Input input, FieldConstraints c, PatternBudget budget)
    {
        if (!p.TryGetProperty("default", out var def) || def.ValueKind == JsonValueKind.Null) { return null; }
        if (def.ValueKind == JsonValueKind.String && def.GetString()!.Length == 0) { return null; }
        if (input == CopilotFieldSchema.Input.Number)
        {
            var text = def.ValueKind switch { JsonValueKind.Number => def.GetRawText(), JsonValueKind.String => def.GetString(), _ => null };
            return text is not null && CopilotFieldSchema.HeldExactly(text) is not null ? c.NumberProblem(text) : null;
        }
        if (input != CopilotFieldSchema.Input.Text) { return null; }
        var words = def.ValueKind == JsonValueKind.String ? def.GetString()! : def.GetRawText();
        // Whitespace is words too: the CLI records a default of spaces as it is (review of #844).
        return words.Length == 0 ? null : c.TextProblem(words, budget);
    }

    static bool Has(JsonElement p, string name, out JsonElement value) =>
        p.TryGetProperty(name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // A bound, as written, when JavaScript holds it exactly: what a client compares is a double.
    static JsonElement? Number(JsonElement p, string name, ref string? problem)
    {
        if (!Has(p, name, out var v)) { return null; }
        if (v.ValueKind != JsonValueKind.Number || CopilotFieldSchema.HeldExactly(v.GetRawText()) is null) { problem ??= Uncheckable; return null; }
        return v.Clone();
    }

    // A non-negative whole number. One past int.MaxValue bounds nothing text or a list can reach,
    // so it is held as int.MaxValue, which means the same.
    static int? Count(JsonElement p, string name, ref string? problem)
    {
        if (!Has(p, name, out var v)) { return null; }
        if (v.ValueKind != JsonValueKind.Number || ExactDecimal.Parse(v.GetRawText()) is not { IsInteger: true, Sign: >= 0 } n)
        {
            problem ??= Uncheckable;
            return null;
        }
        return ExactDecimal.Compare(n, new ExactDecimal(int.MaxValue, 0)) > 0 ? int.MaxValue : (int)(n.Mantissa * System.Numerics.BigInteger.Pow(10, (int)n.Exponent));
    }

    static string? Pattern(JsonElement p, PatternBudget budget, ref string? problem)
    {
        if (!Has(p, "pattern", out var v)) { return null; }
        if (v.ValueKind != JsonValueKind.String || !FieldConstraints.Readable(v.GetString()!, budget)) { problem ??= Uncheckable; return null; }
        return v.GetString();
    }

    // The four formats the CLI knows; any other it would not check either, so neither does this.
    static string? Format(JsonElement p, ref string? problem)
    {
        if (!Has(p, "format", out var v)) { return null; }
        if (v.ValueKind != JsonValueKind.String) { problem ??= Uncheckable; return null; }
        return FieldConstraints.Formats.Contains(v.GetString()) ? v.GetString() : null;
    }
}
