using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Copilot;

public enum CopilotFieldKind { Choice, Boolean, Multi, Text, Number }

// One option as the prompt lists it, in order: Value is the protocol's Option.Value (the
// schema's scalar, as text), Raw the scalar itself, which is what the CLI records.
public sealed record CopilotOption(string Value, JsonElement Raw);

// One field of Copilot's ask_user prompt, as the keys have to drive it. Checked is the rows
// a multi-select list opens with ticked (its schema default).
public sealed record CopilotField(
    string Name,
    CopilotFieldKind Kind,
    IReadOnlyList<CopilotOption> Options,
    IReadOnlyList<int> Checked,
    bool AllowsEmpty);

// The ask_user prompt read from the tool call's own arguments, the way agent-ha-bridge
// reads it (Get-DecisionSchemaFields), rather than from the Question a client saw: the
// keys depend on things a Question does not carry (which rows open ticked, whether an
// empty list is an answer, the older `choices` format).
public sealed record CopilotForm(IReadOnlyList<CopilotField> Fields)
{
    // Null with a reason when the prompt cannot be driven by keys from here.
    public static (CopilotForm? Form, string? Problem) Read(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) { return (null, "the question's arguments could not be read"); }
        if (arguments.TryGetProperty("choices", out var choices) && choices.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            // The older ask_user. agent-ha-bridge never drove it either: its option list
            // takes words only in a box Enter does not submit, and no current build writes it.
            return (null, "this question uses an older Copilot format the host cannot answer; answer it at the machine");
        }
        if (!arguments.TryGetProperty("requestedSchema", out var schema) || schema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (new CopilotForm([]), null);
        }
        if (schema.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(schema.GetString() ?? "");
                return Schema(doc.RootElement.Clone());
            }
            catch (JsonException) { return (null, "the question's form could not be read"); }
        }
        return Schema(schema);
    }

    static (CopilotForm?, string?) Schema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) { return (null, "the question's form could not be read"); }
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return (new CopilotForm([]), null);
        }
        var fields = new List<CopilotField>();
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) { return (null, $"the question's field \"{property.Name}\" could not be read"); }
            var (field, problem) = Field(property.Name, property.Value);
            if (field is null) { return (null, problem); }
            fields.Add(field);
        }
        return (new CopilotForm(fields), null);
    }

    static (CopilotField?, string?) Field(string name, JsonElement p)
    {
        var type = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var isArray = type == "array";
        var options = Choices(p, type);
        var hasDefault = p.TryGetProperty("default", out var def);

        if (options.Count == 0)
        {
            if (isArray) { return (null, $"\"{name}\" is a list with no options to choose from"); }
            var kind = type is "number" or "integer" ? CopilotFieldKind.Number : CopilotFieldKind.Text;
            return (new CopilotField(name, kind, [], [], false), null);
        }

        if (isArray)
        {
            var ticked = new List<int>();
            // A null default names nothing; [null] would name an option that is null.
            if (hasDefault && def.ValueKind != JsonValueKind.Null)
            {
                if (def.ValueKind != JsonValueKind.Array) { return (null, $"\"{name}\" has a default that is not a list"); }
                foreach (var entry in def.EnumerateArray())
                {
                    var at = IndexOf(options, entry);
                    if (at < 0 || ticked.Contains(at)) { return (null, $"\"{name}\" has a default that does not name its options"); }
                    ticked.Add(at);
                }
            }
            ticked.Sort();
            return (new CopilotField(name, CopilotFieldKind.Multi, options, ticked, AllowsEmpty(p)), null);
        }

        // The keys never count from the default (they walk from the top row), but a default
        // that names no option is a prompt nobody has seen drawn, so it is not typed into.
        // A null default is the schema not naming one.
        if (hasDefault && def.ValueKind != JsonValueKind.Null && IndexOf(options, def) < 0)
        {
            return (null, $"\"{name}\" has a default that is not one of its options; answer it at the machine");
        }
        return (new CopilotField(name, type == "boolean" ? CopilotFieldKind.Boolean : CopilotFieldKind.Choice, options, [], false), null);
    }

    // Read as the host's QuestionMapper reads them, so a client's option values are these:
    // `enum`, `oneOf` / `anyOf` entries of `{const, title}`, the same inside `items` for a
    // list, and true / false for a boolean, which the prompt shows as Yes / No.
    static List<CopilotOption> Choices(JsonElement p, string? type)
    {
        var source = type == "array" && p.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object ? items : p;
        var list = new List<CopilotOption>();
        if (source.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in values.EnumerateArray()) { list.Add(new CopilotOption(Scalar(v), v.Clone())); }
            return list;
        }
        var entries = source.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array ? oneOf
            : source.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array ? anyOf : default;
        if (entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) { continue; }
                if (entry.TryGetProperty("const", out var c) && c.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) { list.Add(new CopilotOption(Scalar(c), c.Clone())); }
                else if (entry.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String) { list.Add(new CopilotOption(title.GetString()!, title.Clone())); }
            }
            return list;
        }
        if (type == "boolean")
        {
            list.Add(new CopilotOption("true", Literal(true)));
            list.Add(new CopilotOption("false", Literal(false)));
        }
        return list;
    }

    static JsonElement Literal(bool value) => JsonSerializer.SerializeToElement(value);

    // `minItems` absent is JSON Schema's 0, and the prompt accepts Enter on an untouched
    // list. Anything that is not plainly a whole number is read as no.
    static bool AllowsEmpty(JsonElement p) =>
        !p.TryGetProperty("minItems", out var min) || (min.ValueKind == JsonValueKind.Number && min.TryGetInt64(out var n) && n <= 0);

    static int IndexOf(IReadOnlyList<CopilotOption> options, JsonElement value)
    {
        var found = -1;
        for (var i = 0; i < options.Count; i++)
        {
            if (!SameScalar(options[i].Raw, value)) { continue; }
            if (found >= 0) { return -1; }
            found = i;
        }
        return found;
    }

    // As QuestionMapper writes Option.Value: a string as itself, anything else as its JSON.
    internal static string Scalar(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();

    // Two JSON scalars are the same value: equal strings, the same literal, or equal numbers.
    public static bool SameScalar(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : a.GetRawText() == b.GetRawText();
        }
        if (a.ValueKind != b.ValueKind) { return false; }
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }

    // Whether this is the prompt the client was shown: the same fields, in the same order,
    // of the same kind, with the same options. Two readers of one schema that disagree
    // mean the keys would be planned for a different prompt, so nothing is typed.
    public bool Matches(Question question)
    {
        if (question.Fields.Count != Fields.Count) { return false; }
        for (var i = 0; i < Fields.Count; i++)
        {
            var mine = Fields[i];
            var theirs = question.Fields[i];
            if (mine.Name != theirs.Name) { return false; }
            var expected = mine.Kind switch
            {
                CopilotFieldKind.Choice => FieldType.Choice,
                CopilotFieldKind.Multi => FieldType.Multi,
                CopilotFieldKind.Boolean => FieldType.Boolean,
                CopilotFieldKind.Number => FieldType.Number,
                _ => FieldType.Text,
            };
            if (theirs.Type != expected) { return false; }
            if (mine.Kind is CopilotFieldKind.Choice or CopilotFieldKind.Multi
                && !(theirs.Options ?? []).Select(o => o.Value).SequenceEqual(mine.Options.Select(o => o.Value), StringComparer.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
