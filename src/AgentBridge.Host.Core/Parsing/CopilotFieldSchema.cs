using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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
public static partial class CopilotFieldSchema
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
                    if (items.ValueKind != JsonValueKind.Object) { return (Input.Multi, [], NoOptions); }
                    var (anyOf, badAnyOf) = List(items, "anyOf");
                    var (values, badEnum) = List(items, "enum");
                    if (badAnyOf || badEnum) { return (Input.Multi, [], Unknown); }
                    var (choices, problem) = anyOf is { } a ? Consts(a) : values is { } v ? Values(v, null) : ([], null);
                    return (Input.Multi, choices, problem ?? (choices.Count == 0 ? NoOptions : Ticks(p, choices)));
                }
            case "string":
                {
                    var (oneOf, badOneOf) = List(p, "oneOf");
                    var (values, badEnum) = List(p, "enum");
                    if (badOneOf || badEnum) { return (Input.Choice, [], Unknown); }
                    if (oneOf is null && values is null) { return (Input.Text, [], null); }
                    var (labels, _) = List(p, "enumNames");
                    var (choices, problem) = oneOf is { } o ? Consts(o) : Values(values!.Value, labels);
                    return (Input.Choice, choices, problem ?? (choices.Count == 0 ? NoOptions : Opens(p, choices)));
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
            if (Unheld(c)) { return ([], HeldDifferently); }
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
            if (Unheld(v)) { return ([], HeldDifferently); }
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

    // A checkbox list without options (no `items` object, or one naming none): nothing to tick,
    // and nothing the keys could walk.
    const string NoOptions = "is a list with no options to choose from";

    const string HeldDifferently = "has a number option Copilot would hold as a different number";

    // A number option the CLI would not hold exactly as written: two such options can become
    // the same double, and the one recorded is not the one given.
    static bool Unheld(JsonElement v) => v.ValueKind == JsonValueKind.Number && HeldExactly(v.GetRawText()) is null;

    // Bounds on a written exponent. Checked against both signed bounds, never through Math.Abs:
    // long.MinValue (1e-9223372036854775808) has no positive counterpart, and Math.Abs threw
    // while a question was being read (#700).
    const long ExponentBound = 1_000_000_000;

    // A JSON number's exact value as text: its significant digits and the power of ten that
    // scales them, so 2.50, 2.5 and 25e-1 read alike and nothing is rounded. Null, never a
    // throw, for text that is not a JSON number, or whose exponent is too large to hold, which
    // is then the same as nothing.
    public static string? ExactNumber(string raw)
    {
        if (raw is null || raw.Length > 4096 || !JsonNumber().IsMatch(raw)) { return null; }
        var s = raw;
        var negative = s.StartsWith('-');
        if (negative) { s = s[1..]; }
        long exponent = 0;
        var e = s.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            if (!long.TryParse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent > ExponentBound || exponent < -ExponentBound) { return null; }
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

    // The number Copilot CLI holds for this JSON number, in ExactNumber's form, when that is
    // exactly the number as written; null otherwise. The CLI is JavaScript: a schema's numbers,
    // and a number box's text (Number(text)), become the nearest double, and what it records is
    // that double, written as the shortest text that reads back as it. 9007199254740993 is
    // recorded as 9007199254740992, 1e-400 as 0, and 1e400 is not finite at all (#700). .NET
    // parses to the same nearest double, and "R" writes the same shortest text, so a number
    // whose shortest text is not exactly it is one the CLI would change.
    public static string? HeldExactly(string text)
    {
        if (ExactNumber(text) is not { } exact) { return null; }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) { return null; }
        return ExactNumber(d.ToString("R", CultureInfo.InvariantCulture)) == exact ? exact : null;
    }

    // ExactNumber's form written out without an exponent, which is how a number is typed: the
    // number box drops a key that leaves its text not a number (`Lxn`'s inputFilter,
    // !isNaN(Number(text))), and "1e" is not one, so an exponent typed key by key could lose its
    // "e". Every prefix of plain decimal ("-", "-1", "-1.") passes. Null when it would be longer
    // than max.
    public static string? PlainDecimal(string exact, int max)
    {
        if (exact == "0") { return "0"; }
        var e = exact.IndexOf('e');
        if (e < 0 || !long.TryParse(exact[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent)) { return null; }
        var negative = exact[0] == '-';
        var digits = exact[(negative ? 1 : 0)..e];
        string plain;
        if (exponent >= 0)
        {
            if (digits.Length + exponent > max) { return null; }
            plain = digits + new string('0', (int)exponent);
        }
        else
        {
            var point = digits.Length + exponent;
            if (point > 0) { plain = $"{digits[..(int)point]}.{digits[(int)point..]}"; }
            else
            {
                if (2 - point + digits.Length > max) { return null; }
                plain = $"0.{new string('0', (int)-point)}{digits}";
            }
        }
        plain = (negative ? "-" : "") + plain;
        return plain.Length > max ? null : plain;
    }

    [GeneratedRegex(@"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumber();

    static bool Scalar(JsonElement v) => v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    static JsonElement Literal(bool value) => JsonSerializer.SerializeToElement(value);
}
