using System.Globalization;
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
//   * The option list (`Dxn`) opens on its Other box whenever its value is set and is not one
//     of its options: `M = r !== void 0 && r !== "" && !R.includes(r) ? r : void 0`, then
//     `useState(M !== void 0)`. `Y_n` copies a default in whenever it is `!== undefined`, so a
//     null default opens Other too, with the caret on it; Up there is the Other box's `onExit`,
//     which puts the caret on the LAST option, so a walk counted from the top row ends a row off.
//     Only an absent default, or one naming an option, leaves Other closed (#700).
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
                    return (Input.Multi, choices, problem ?? (choices.Count == 0 ? "is a list with no options to choose from" : Ticks(p, choices)));
                }
            case "string":
                {
                    var (oneOf, badOneOf) = List(p, "oneOf");
                    var (values, badEnum) = List(p, "enum");
                    if (badOneOf || badEnum) { return (Input.Choice, [], Unknown); }
                    if (oneOf is null && values is null) { return (Input.Text, [], null); }
                    var (labels, _) = List(p, "enumNames");
                    var (choices, problem) = oneOf is { } o ? Consts(o) : Values(values!.Value, labels);
                    return (Input.Choice, choices, problem ?? (choices.Count == 0 ? "is a list with no options to choose from" : Opens(p, choices)));
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

    // A list's default, as the option list opens on it: absent, or one of its options. Anything
    // else, null included, opens the Other box, which the keys do not walk.
    static string? Opens(JsonElement p, IReadOnlyList<Choice> choices)
    {
        if (!p.TryGetProperty("default", out var def)) { return null; }
        if (def.ValueKind == JsonValueKind.Null) { return "opens on its Other box (its default is null)"; }
        return choices.Count(c => SameScalar(c.Value, def)) == 1 ? null : "has a default that is not one of its options";
    }

    // A checkbox list's default: absent or null (nothing ticked), or a list naming its options,
    // each once.
    static string? Ticks(JsonElement p, IReadOnlyList<Choice> choices)
    {
        if (!p.TryGetProperty("default", out var def) || def.ValueKind == JsonValueKind.Null) { return null; }
        if (def.ValueKind != JsonValueKind.Array) { return "has a default that is not a list"; }
        var named = new HashSet<int>();
        foreach (var entry in def.EnumerateArray())
        {
            var at = -1;
            for (var i = 0; i < choices.Count; i++)
            {
                if (!SameScalar(choices[i].Value, entry)) { continue; }
                if (at >= 0) { return "has a default that does not name its options"; }
                at = i;
            }
            if (at < 0 || !named.Add(at)) { return "has a default that does not name its options"; }
        }
        return null;
    }

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
    public static string? ExactNumber(string raw)
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

    static bool Scalar(JsonElement v) => v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    static JsonElement Literal(bool value) => JsonSerializer.SerializeToElement(value);
}
