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
//   * ask_user's form is drawn with `enforceSchemaConstraints` false (its elicitation has no
//     source, so `elicitationSource !== JD` is false), and the Yes / No list (`Oxn`) then has a
//     third row, Other (`_ = !d`, `A = EK`). A string default opens on that row: a non-empty one
//     with Other's box open and the text in it (`C`, `useState(_ && C !== void 0)`), an empty
//     one with the caret on it (`typeof r == "string" ? _ ? EK : 0`). The walk's Up then leaves
//     the caret on No (the box's exit, `G`, or Up from the row), so Yes recorded No, and No's Down
//     reopened Other (#768). A boolean, null or absent default opens on Yes or No.
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
                return (Input.Boolean, [new(Literal(true), null), new(Literal(false), null)], YesOrNo(p));
            case null when !Has(p, "enum") && !Has(p, "oneOf") && !Has(p, "anyOf"):
                return (Input.Text, [], null);
            default:
                // Untyped options, or a type the CLI does not know: it draws a text box, which
                // is not what the schema means. Nothing is planned for it.
                return (Input.Text, [], Unknown);
        }
    }

    // The field's heading as Copilot CLI 1.0.105 draws it (`Uxn`: `title ?? name`; a list's,
    // Yes/No's or multi-select's component adds a colon, a text or number box's does not). A
    // title that is not a string, or an empty one (drawn as nothing at all), is not a heading
    // the host can look for. Nor is one of white space alone: a text or number box draws it as a
    // blank row, which cannot be told from the blank rows around it, and a list's as a lone
    // colon. Nor is one with a control character: a line break ("Pick\n", "Pick\r") splits the
    // heading across rows, its colon on the next, while the tab bar's first row still reads as
    // the labels, so the field was found missing only once the fields before it had been
    // answered (#510 review, Trust on 412f1b2d). Without a label every field of the form is
    // refused before the first key, rather than this one only once the fields before it have
    // been answered (#510 review, Copilot 5780656913). Nor, last, is one whose spaces the host
    // cannot be sure how the CLI draws: white space at its ends, a run of spaces, or a space of
    // another kind ("Pick one ", "Pick  one", "Pick\u00A0one"). The screen's heading and tab bar
    // are read exactly as drawn, so read for another drawing of such a title they would refuse
    // the form, or pass one titled otherwise (#1137 review). A title of words between single
    // spaces is read exactly, in the tab bar and the heading alike.
    public static string? Label(string name, JsonElement p) =>
        (!p.TryGetProperty("title", out var title) || title.ValueKind == JsonValueKind.Null ? name
        : title.ValueKind == JsonValueKind.String && title.GetString() is { Length: > 0 } text ? text
        : null) is { } label && !string.IsNullOrWhiteSpace(label) && !label.Any(char.IsControl) && Plain(label) ? label : null;

    static bool Plain(string label) =>
        label == label.Trim() && !label.Any(c => char.IsWhiteSpace(c) && c != ' ') && !label.Contains("  ", StringComparison.Ordinal);

    // The heading as drawn, for a label: a text or number box's (`box`) title alone, any other's
    // with a colon (`Fxn`, `Lxn`; `Dxn`, `Oxn`, `Nxn`). Read as "<title>:" for every field, the
    // text field after a list was never found: the list was picked and committed, and the form
    // left half answered (#510 review, Copilot 5780329899). The colon is added to the title as
    // written, as the CLI draws the two together (`t,":"`, never trimmed).
    public static string Heading(string label, bool box) => Spaces().Replace(label + (box ? "" : ":"), " ").Trim();

    // The first field whose heading cannot be told on screen from an earlier one's, or null;
    // headings are null for a field with no label. The field being answered is known by its
    // heading and, among several, by the tab hints, which say only whether it is first, last or
    // between; the tab bar marks it by colour alone, which the screen read does not carry. So two
    // fields with the same heading, both between the first and the last, look the same: a screen
    // that changed without the form moving on (an Enter the CLI did not act on, while something
    // else repainted) would pass for the next one, and its keys go into the field before (#510
    // review, Copilot 5780128109).
    public static int? Alike(IReadOnlyList<string?> headings)
    {
        var count = headings.Count;
        string Place(int i) => i == 0 ? "first" : i == count - 1 ? "last" : "between";
        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < count; i++)
        {
            if (headings[i] is { } heading && !seen.Add((heading, Place(i)))) { return i; }
        }
        return null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

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

    // A Yes / No list's default: absent, null, true or false. Anything else is not walked: a string
    // opens it on its Other row, and any other value is not one the host has read the list with.
    static string? YesOrNo(JsonElement p) =>
        !p.TryGetProperty("default", out var def) || def.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
            ? null
            : "has a default that is not Yes or No (a text default opens it on its Other row)";

    // A checkbox list's default: absent or null (nothing ticked), or a list naming its options,
    // each once.
    static string? Ticks(JsonElement p, IReadOnlyList<Choice> choices)
    {
        if (!p.TryGetProperty("default", out var def) || def.ValueKind == JsonValueKind.Null) { return null; }
        if (def.ValueKind != JsonValueKind.Array) { return "has a default that is not a list"; }
        // Looked up by key, not matched against every option: each entry against every option was
        // quadratic on the thread that refreshes every session (#852).
        var at = Positions(choices.Select(c => c.Value));
        var named = new HashSet<int>();
        foreach (var entry in def.EnumerateArray())
        {
            if (Position(at, entry) is not { } i || !named.Add(i)) { return "has a default that does not name its options"; }
        }
        return null;
    }

    // Each option's place by its ScalarKey, or -1 for a key two options share: such a value names
    // no one option, as SameScalar matching it twice did not. An option equal to nothing (a number
    // read inexactly) is in it under no key.
    public static Dictionary<string, int> Positions(IEnumerable<JsonElement> options)
    {
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var o in options)
        {
            if (ScalarKey(o) is { } key) { at[key] = at.ContainsKey(key) ? -1 : i; }
            i++;
        }
        return at;
    }

    // The one option a value is, by Positions: null for none, or for one it cannot tell from another.
    public static int? Position(Dictionary<string, int> at, JsonElement value) =>
        ScalarKey(value) is { } key && at.TryGetValue(key, out var i) && i >= 0 ? i : null;

    // A scalar as SameScalar compares it: equal keys exactly when SameScalar holds. Null for one
    // equal to nothing, as a number it cannot read exactly is, or a string .NET will not read
    // (half a surrogate pair): SameScalar told that from a number without reading it, so a
    // default of ["\uD800"] among number options was a problem, not a failed mapping (#921 review).
    public static string? ScalarKey(JsonElement v)
    {
        _compared++;
        return v.ValueKind switch
        {
            JsonValueKind.String => Text(v) is { } s ? "s" + s : null,
            JsonValueKind.Number => ExactNumber(v.GetRawText()) is { } n ? "n" + n : null,
            JsonValueKind.True => "t",
            JsonValueKind.False => "f",
            JsonValueKind.Null => "z",
            _ => null,
        };
    }

    // How many values this thread has compared (SameScalar) or keyed (ScalarKey): what reading a
    // long option list costs, counted rather than timed, so a test can tell linear from quadratic
    // on a loaded runner, where a timed ratio could not be trusted (#921 review).
    [ThreadStatic] static long _compared;
    internal static long Compared => _compared;

    // Whether a field Copilot opens with its default typed in can be given any answer from another
    // device. Only its default can (CopilotForm.Keeps, #662), and only when the host knows what it
    // records: a text box records a string default as it is, and a number box the number
    // RecordedNumber reads. A text box with any other default (5, true, []), or a number box whose
    // default is null, not plainly a number or not held exactly, was offered and every answer
    // refused (#882). A default absent or empty opens nothing; a text box's null opens it empty.
    // A string default must also be one a client sends and Answers.Check takes (#921 review): not
    // blank once typed (a required one is refused as blank, #844), nor blank as the PWA sees it,
    // Invisible.Blank, which is wider ("\u200B", "\u2800", "\u0301"; the PWA leaves such a field
    // out of the answer, as the app does a white one); text at all (half a surrogate pair
    // escaped, "\uD800", is not: .NET will not read it, and both clients send it as U+FFFD, which
    // is not the default); and within Limits.AnswerText.
    public static bool PrefillAnswerable(Input input, JsonElement p)
    {
        if (!p.TryGetProperty("default", out var def) || (def.ValueKind == JsonValueKind.String && def.ValueEquals(""))) { return true; }
        return input switch
        {
            Input.Text => def.ValueKind == JsonValueKind.Null || (Text(def) is { } text && Sendable(text)),
            Input.Number => RecordedNumber(def) is not null,
            _ => true,
        };
    }

    // A property's string, as Shown, or null when it is absent or not a string: for what an agent
    // wrote into a tool call, where GetString on half a surrogate pair threw on every pass over
    // the transcript line, dropping the session and holding discovery (#921 review).
    public static string? Shown(JsonElement e, string name) => Json.Get(e, name) is { ValueKind: JsonValueKind.String } v ? Shown(v) : null;

    // A string as a client shows it: half a surrogate pair, which .NET will not read as text
    // (Text), as U+FFFD, the rest as written (Json.Text).
    public static string Shown(JsonElement v) => Json.Text(v);

    // A finite number as JavaScript's Number#toString writes it (ECMAScript, Number::toString):
    // the shortest digits that read back as it, which .NET's "R" also finds, laid out as
    // JavaScript lays them out. 1e20 is 100000000000000000000, 1e21 is 1e+21, 1.5e-7 is 1.5e-7.
    public static string JavaScriptNumber(double value)
    {
        if (value == 0) { return "0"; }
        var s = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var e = s.IndexOfAny(['E', 'e']);
        if (e >= 0) { exponent = int.Parse(s.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); s = s[..e]; }
        var dot = s.IndexOf('.');
        var digits = dot >= 0 ? s.Remove(dot, 1) : s;
        var point = dot >= 0 ? dot : s.Length;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; }
        digits = digits[lead..].TrimEnd('0');
        // The value is 0.digits × 10^n, digits k long.
        var n = point - lead + exponent;
        var k = digits.Length;
        var sign = value < 0 ? "-" : "";
        if (k <= n && n <= 21) { return sign + digits + new string('0', n - k); }
        if (0 < n && n <= 21) { return sign + digits[..n] + "." + digits[n..]; }
        if (-6 < n && n <= 0) { return sign + "0." + new string('0', -n) + digits; }
        var power = n - 1;
        var written = power < 0 ? $"e-{-power}" : $"e+{power}";
        return sign + (k == 1 ? digits : digits[0] + "." + digits[1..]) + written;
    }

    // A string as text, or null for anything else, half a surrogate pair included.
    public static string? Text(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) { return null; }
        try { return v.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    static bool Sendable(string text) =>
        !AgentBridge.Protocol.FieldConstraints.BlankOnceTyped(text) && !AgentBridge.Protocol.Invisible.Blank(text) && System.Text.Encoding.UTF8.GetByteCount(text) <= AgentBridge.Protocol.Limits.AnswerText;

    // The number a number box records for this value, as a JSON number, or null when that is not
    // certain. Copilot 1.0.105's form (`Uxn`, its accept step) turns a number field's string into
    // Number(text) when that is finite and keeps it a string otherwise, so a default of "3" is
    // recorded as 3. Only a string that is plainly a JSON number is read so (Number() also takes
    // hex, padding and "Infinity"), and only one the CLI holds exactly as written (HeldExactly):
    // "9007199254740993" would be recorded as 9007199254740992, and "1e-400" as 0, so neither is
    // the answer it reads as.
    public static JsonElement? RecordedNumber(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            // Text, not GetString: this runs while a question is mapped (PrefillAnswerable), where
            // "a\uD800" threw instead of answering at the machine (#921 review).
            JsonValueKind.String => Text(value),
            _ => null,
        };
        if (text is null || HeldExactly(text) is null) { return null; }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
    // Two JSON scalars are the same value: equal strings, the same literal, or equal numbers,
    // compared exactly. Read through decimal, 1e-29 was 0 and 3.0000000000000000000000000000001
    // was 3, so an answer that is not the default could pass for it (#700).
    public static bool SameScalar(JsonElement a, JsonElement b)
    {
        _compared++;
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
