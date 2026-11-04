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
// field's schema default, which the prompt opens with already typed into it (#662).
public sealed record CopilotField(
    string Name,
    CopilotFieldKind Kind,
    IReadOnlyList<CopilotOption> Options,
    IReadOnlyList<int> Checked,
    bool AllowsEmpty,
    JsonElement? Prefill = null);

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
        // The input Copilot draws for it, read as the CLI reads it (CopilotFieldSchema): a
        // field the keys would drive as something it is not is not typed into at all.
        var (input, choices, problem) = CopilotFieldSchema.Read(p);
        if (problem is not null) { return (null, $"\"{name}\" {problem}; answer it at the machine"); }
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
                    return (new CopilotField(name, kind, [], [], false, prefilled ? def.Clone() : null), null);
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
                    return (new CopilotField(name, CopilotFieldKind.Multi, options, ticked, AllowsEmpty(p)), null);
                }
            default:
                // The keys never count from the default (they walk from the top row), but a
                // default that names no option opens the list on its Other box with that text
                // already typed in, so it is not typed into. A null default names none.
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

    // Whether an answer is what a field opens with, as the prompt shows it: the same scalar, or a
    // string and a number that read the same. Anything else (an object, a list) never is.
    public static bool Keeps(JsonElement prefill, JsonElement value) =>
        SameScalar(prefill, value)
        || (prefill.ValueKind is JsonValueKind.String or JsonValueKind.Number
            && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            && Scalar(prefill) == Scalar(value));

    // Two JSON scalars are the same value: equal strings, the same literal, or equal numbers,
    // compared exactly. Read through decimal, 1e-29 was 0 and 3.0000000000000000000000000000001
    // was 3, so an answer that is not the default could pass for it (#700).
    public static bool SameScalar(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            return ExactNumber(a.GetRawText()) is { } x && x == ExactNumber(b.GetRawText());
        }
        if (a.ValueKind != b.ValueKind) { return false; }
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }

    // A JSON number's exact value as text: its significant digits and the power of ten that
    // scales them, so 2.50, 2.5 and 25e-1 read alike and nothing is rounded. Null for one whose
    // exponent is too large to hold, which is then the same as nothing.
    static string? ExactNumber(string raw)
    {
        var s = raw;
        var negative = s.StartsWith('-');
        if (negative) { s = s[1..]; }
        long exponent = 0;
        var e = s.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            if (!long.TryParse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || Math.Abs(exponent) > 1_000_000_000) { return null; }
            s = s[..e];
        }
        var dot = s.IndexOf('.');
        if (dot >= 0)
        {
            exponent -= s.Length - dot - 1;
            s = s.Remove(dot, 1);
        }
        var digits = s.TrimStart('0');
        if (digits.Length == 0) { return "0"; }
        var significant = digits.TrimEnd('0');
        exponent += digits.Length - significant.Length;
        return $"{(negative ? "-" : "")}{significant}e{exponent.ToString(CultureInfo.InvariantCulture)}";
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
