using System.Globalization;
using System.Text.Json;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Copilot;

public enum CopilotFieldKind { Choice, Boolean, Multi, Text, Number }

// One option as the prompt lists it, in order: Value is the protocol's Option.Value (the
// schema's scalar, as text), Raw the scalar itself, which is what the CLI records.
public sealed record CopilotOption(string Value, JsonElement Raw);

// One field of Copilot's ask_user prompt, as the keys have to drive it. Checked is the rows
// a multi-select list opens with ticked (its schema default). Prefill is a text or number
// field's schema default, which the prompt opens with already typed into it (#662). Label is
// what the prompt heads the field with while it is the one being answered (`title ?? name`;
// CopilotFormScreen says how each kind draws it), null when its title is something the host
// cannot say how the CLI draws.
public sealed record CopilotField(
    string Name,
    CopilotFieldKind Kind,
    IReadOnlyList<CopilotOption> Options,
    IReadOnlyList<int> Checked,
    bool AllowsEmpty,
    JsonElement? Prefill = null,
    FieldConstraints? Constraints = null,
    string? Label = null);

// The ask_user prompt read from the tool call's own arguments, the way agent-ha-bridge
// reads it (Get-DecisionSchemaFields), rather than from the Question a client saw: the
// keys depend on things a Question does not carry (which rows open ticked, whether an
// empty list is an answer, the older `choices` format).
// Message is the question the prompt shows over its fields, as asked.
public sealed record CopilotForm(IReadOnlyList<CopilotField> Fields, string? Message = null)
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
        var message = arguments.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (!arguments.TryGetProperty("requestedSchema", out var schema) || schema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (new CopilotForm([], message), null);
        }
        if (schema.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(schema.GetString() ?? "");
                return Asked(Schema(doc.RootElement.Clone()), message);
            }
            catch (JsonException) { return (null, "the question's form could not be read"); }
        }
        return Asked(Schema(schema), message);
    }

    static (CopilotForm?, string?) Asked((CopilotForm? Form, string? Problem) read, string? message) =>
        read.Form is { } form ? (form with { Message = message }, null) : read;

    static (CopilotForm?, string?) Schema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) { return (null, "the question's form could not be read"); }
        if (CopilotFieldConstraints.FormUnchecked(schema) is { } keyword)
        {
            return (null, $"the question's form has a limit the host does not check (\"{keyword}\"); answer it at the machine");
        }
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return (new CopilotForm([]), null);
        }
        var fields = new List<CopilotField>();
        var patterns = new PatternBudget();
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) { return (null, $"the question's field \"{property.Name}\" could not be read"); }
            var (field, problem) = Field(property.Name, property.Value, patterns);
            if (field is null) { return (null, problem); }
            fields.Add(field with { Label = Label(property.Name, property.Value) });
        }
        return (new CopilotForm(fields), null);
    }

    // The field's heading as Copilot CLI 1.0.105 draws it (`Uxn`: `title ?? name`; a list's,
    // Yes/No's or multi-select's component adds a colon, a text or number box's does not). A
    // title that is not a string, or an empty one (drawn as nothing at all), is not a heading
    // the host can look for.
    static string? Label(string name, JsonElement p) =>
        !p.TryGetProperty("title", out var title) || title.ValueKind == JsonValueKind.Null ? name
        : title.ValueKind == JsonValueKind.String && title.GetString() is { Length: > 0 } text ? text
        : null;

    static (CopilotField?, string?) Field(string name, JsonElement p, PatternBudget patterns)
    {
        // The input Copilot draws for it, read as the CLI reads it (CopilotFieldSchema): a
        // field the keys would drive as something it is not is not typed into at all.
        var (input, choices, problem) = CopilotFieldSchema.Read(p);
        if (problem is not null) { return (null, $"\"{name}\" {problem}; answer it at the machine"); }
        // Copilot checks none of the field's constraints itself, so the host checks the answer
        // against them before a key goes (#766).
        var (limits, unlimited) = CopilotFieldConstraints.Read(p, patterns);
        if (unlimited is not null) { return (null, $"\"{name}\" {unlimited}; answer it at the machine"); }
        var options = choices.Select(c => new CopilotOption(Scalar(c.Value), c.Value)).ToList();
        var hasDefault = p.TryGetProperty("default", out var def);

        switch (input)
        {
            case CopilotFieldSchema.Input.Text or CopilotFieldSchema.Input.Number:
                {
                    var kind = input == CopilotFieldSchema.Input.Number ? CopilotFieldKind.Number : CopilotFieldKind.Text;
                    // Copilot opens such a field with its default already in it, and keys typed
                    // there are added to it: 90 typed into a field that opened with 90 was
                    // recorded as 9090 (#662). A text box shows `default ?? ""`, so a null or
                    // empty default leaves it empty; a number box shows String(default), so only
                    // an empty one does, and a null one reads "null".
                    var empty = def.ValueKind == JsonValueKind.String && def.GetString()!.Length == 0;
                    var prefilled = hasDefault && !empty && (kind == CopilotFieldKind.Number || def.ValueKind != JsonValueKind.Null);
                    return (new CopilotField(name, kind, [], [], false, prefilled ? def.Clone() : null, limits), null);
                }
            case CopilotFieldSchema.Input.Multi:
                {
                    if (options.Count == 0) { return (null, $"\"{name}\" is a list with no options to choose from"); }
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
                    return (new CopilotField(name, CopilotFieldKind.Multi, options, ticked, AllowsEmpty(p), Constraints: limits), null);
                }
            default:
                // The keys never count from the default (they walk from the top row), but a
                // default that names no option, null included, opens the list on its Other box;
                // CopilotFieldSchema refuses those, and this stays as a second guard.
                if (hasDefault && def.ValueKind == JsonValueKind.Null && input == CopilotFieldSchema.Input.Choice)
                {
                    return (null, $"\"{name}\" opens on its Other box (its default is null); answer it at the machine");
                }
                if (hasDefault && def.ValueKind != JsonValueKind.Null && IndexOf(options, def) < 0)
                {
                    return (null, $"\"{name}\" has a default that is not one of its options; answer it at the machine");
                }
                return (new CopilotField(name, input == CopilotFieldSchema.Input.Boolean ? CopilotFieldKind.Boolean : CopilotFieldKind.Choice, options, [], false), null);
        }
    }

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

    // Whether an answer is what a field that opens with its default records when its Enter keeps
    // it. Anything that cannot be compared exactly is not, and is refused before a key goes: read
    // as raw text, a default and an answer both 1e99999999999 passed for the same number though
    // neither could be compared (#700).
    public static bool Keeps(CopilotField field, JsonElement value)
    {
        if (field.Prefill is not { } prefill) { return false; }
        if (field.Kind == CopilotFieldKind.Number)
        {
            return Recorded(prefill) is { } kept && Recorded(value) is { } given && SameScalar(kept, given);
        }
        // A text box records its default as it is; only a string default is the answer's words.
        return prefill.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String
            && prefill.GetString() == value.GetString();
    }

    // The number a number box records for this value, as a JSON number, or null when that is not
    // certain. Copilot 1.0.105's form (`Uxn`, its accept step) turns a number field's string into
    // Number(text) when that is finite and keeps it a string otherwise, so a default of "3" is
    // recorded as 3. Only a string that is plainly a JSON number is read so (Number() also takes
    // hex, padding and "Infinity"), and only one the CLI holds exactly as written
    // (CopilotFieldSchema.HeldExactly): "9007199254740993" would be recorded as
    // 9007199254740992, and "1e-400" as 0, so neither is the answer it reads as.
    public static JsonElement? Recorded(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString()!,
            _ => null,
        };
        if (text is null || CopilotFieldSchema.HeldExactly(text) is null) { return null; }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // Compared as CopilotFieldSchema compares them, so the list and its default read alike.
    public static bool SameScalar(JsonElement a, JsonElement b) => CopilotFieldSchema.SameScalar(a, b);

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
