using System.Text.Json;

namespace AgentBridge.Host.Core.Parsing;

// One field of Copilot's ask_user form, read the way Copilot CLI itself reads it, so the
// question a client is shown (QuestionMapper) and the keys the host plans (CopilotForm) are
// for the input the CLI actually draws (#662).
//
// From Copilot CLI 1.0.105's cli-main.js (minified names; recheck them on a CLI update):
//   * `tle` picks the input by `type` alone: "array" a checkbox list; "string" an option list
//     when it has `enum` or `oneOf`, otherwise a text box; "number" / "integer" a number box,
//     whatever `enum` or `oneOf` it has; "boolean" a Yes / No list; anything else, or none, a
//     text box. `anyOf` on a string is not read at all.
//   * `h1e` and the option list (`Dxn`) take a string list's options from `oneOf` (each
//     entry's `const`) when it has one, otherwise from `enum`; a checkbox list's from its
//     `items.anyOf` (each `const`), otherwise `items.enum`.
//   * `Y_n` opens every field with its `default`; the text box (`Fxn`) shows `default ?? ""`,
//     and the number box (`Lxn`) `String(default)`, so a null default there reads "null".
//     Up and Down in a number box add or take one from it; typed keys are added to it.
//
// Problem is set for a field whose input cannot be planned with certainty (keywords the CLI
// reads differently, or not at all); Input is then what the CLI would draw.
public static class CopilotFieldSchema
{
    public enum Input { Text, Number, Boolean, Choice, Multi }

    public sealed record Choice(JsonElement Value, string? Label);

    public static (Input Input, IReadOnlyList<Choice> Choices, string? Problem) Read(JsonElement p)
    {
        const string Unknown = "is a kind of field the host cannot tell how Copilot draws";
        if (p.ValueKind != JsonValueKind.Object) { return (Input.Text, [], Unknown); }
        var typed = p.TryGetProperty("type", out var t) && t.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (typed && t.ValueKind != JsonValueKind.String) { return (Input.Text, [], Unknown); }
        var type = typed ? t.GetString() : null;
        switch (type)
        {
            case "array":
                {
                    var items = p.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                    if (items.ValueKind != JsonValueKind.Object) { return (Input.Multi, [], null); }
                    var (anyOf, badAnyOf) = List(items, "anyOf");
                    var (values, badEnum) = List(items, "enum");
                    if (badAnyOf || badEnum) { return (Input.Multi, [], Unknown); }
                    var (choices, problem) = anyOf is { } a ? Consts(a) : values is { } v ? Values(v, null) : ([], null);
                    return (Input.Multi, choices, problem);
                }
            case "string":
                {
                    var (oneOf, badOneOf) = List(p, "oneOf");
                    var (values, badEnum) = List(p, "enum");
                    if (badOneOf || badEnum) { return (Input.Choice, [], Unknown); }
                    if (oneOf is null && values is null) { return (Input.Text, [], null); }
                    var (labels, _) = List(p, "enumNames");
                    var (choices, problem) = oneOf is { } o ? Consts(o) : Values(values!.Value, labels);
                    return (Input.Choice, choices, problem ?? (choices.Count == 0 ? "is a list with no options to choose from" : null));
                }
            case "number" or "integer":
                return (Input.Number, [], null);
            case "boolean":
                return (Input.Boolean, [new(Literal(true), null), new(Literal(false), null)], null);
            case null when !Has(p, "enum") && !Has(p, "oneOf") && !Has(p, "anyOf"):
                return (Input.Text, [], null);
            default:
                // Untyped options, or a type the CLI does not know: it draws a text box, which
                // is not what the schema means. Nothing is planned for it.
                return (Input.Text, [], Unknown);
        }
    }

    // A keyword's list: absent (or null, which the CLI reads as absent), the list, or something
    // else, which it would read some other way.
    static (JsonElement? List, bool Bad) List(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { return (null, false); }
        return v.ValueKind == JsonValueKind.Array ? (v, false) : (null, true);
    }

    static bool Has(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // `oneOf` / `anyOf` entries: the CLI's value is each entry's `const`, and nothing else.
    static (IReadOnlyList<Choice>, string?) Consts(JsonElement entries)
    {
        var list = new List<Choice>();
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("const", out var c) || !Scalar(c))
            {
                return ([], "has an option without a value Copilot can record");
            }
            list.Add(new(c.Clone(), e.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null));
        }
        return (list, null);
    }

    static (IReadOnlyList<Choice>, string?) Values(JsonElement values, JsonElement? labels)
    {
        var names = labels is { } l ? l.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList() : [];
        var list = new List<Choice>();
        var i = 0;
        foreach (var v in values.EnumerateArray())
        {
            if (!Scalar(v)) { return ([], "has an option without a value Copilot can record"); }
            list.Add(new(v.Clone(), i < names.Count && !string.IsNullOrEmpty(names[i]) ? names[i] : null));
            i++;
        }
        return (list, null);
    }

    static bool Scalar(JsonElement v) => v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    static JsonElement Literal(bool value) => JsonSerializer.SerializeToElement(value);
}
