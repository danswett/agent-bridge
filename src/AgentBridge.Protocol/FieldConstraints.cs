using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Protocol;

// A form field's schema constraints, as its agent asked for them: bounds on a number, the
// length, pattern and format of text, how many of a list may be ticked. Carried so a client can
// say what is wrong before sending, and checked by Answers.Check and again by the host on what
// it would type (#766). Copilot CLI 1.0.105 checks none of them on its own ask_user form, so an
// answer outside them would be recorded as given.
//
// Numbers are compared exactly as written, never as doubles; a host carries only bounds a
// JavaScript client also holds exactly. Lengths count UTF-16 code units, as JavaScript does.
public sealed partial record FieldConstraints
{
    public JsonElement? Minimum { get; init; }
    public JsonElement? Maximum { get; init; }
    public JsonElement? ExclusiveMinimum { get; init; }
    public JsonElement? ExclusiveMaximum { get; init; }
    public JsonElement? MultipleOf { get; init; }
    public bool? Integer { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    // An ECMAScript pattern, unanchored and without flags, as `new RegExp(pattern)` reads it.
    public string? Pattern { get; init; }
    // "email", "uri", "date" or "date-time": the four Copilot CLI knows. Others are not carried.
    public string? Format { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }

    public static readonly IReadOnlyList<string> Formats = ["email", "uri", "date", "date-time"];

    // The longest pattern carried; a longer one is answered at the machine.
    public const int MaxPatternLength = 1024;

    static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    const string Unchecked = "its limits cannot be checked here; answer it at the machine";

    // Why a field's value is outside these constraints, or null. A value of the wrong kind is
    // Answers.Check's to name, and blank text is no answer, so neither is judged here.
    public string? Problem(FieldType type, JsonElement value) => type switch
    {
        FieldType.Number when value.ValueKind == JsonValueKind.Number => NumberProblem(value.GetRawText()),
        FieldType.Text when value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) => TextProblem(value.GetString()!),
        FieldType.Multi when value.ValueKind == JsonValueKind.Array => CountProblem(Ticked(value)),
        _ => null,
    };

    // A number, as JSON number text.
    public string? NumberProblem(string number)
    {
        if (ExactDecimal.Parse(number) is not { } n) { return Unchecked; }
        foreach (var (bound, fails, words) in new (JsonElement?, Func<int, bool>, string)[]
        {
            (Minimum, c => c < 0, "at least"), (Maximum, c => c > 0, "at most"),
            (ExclusiveMinimum, c => c <= 0, "more than"), (ExclusiveMaximum, c => c >= 0, "less than"),
        })
        {
            if (bound is not { } b) { continue; }
            if (Bound(b) is not { } limit) { return Unchecked; }
            if (fails(ExactDecimal.Compare(n, limit))) { return $"must be {words} {b.GetRawText()}"; }
        }
        if (Integer == true && !n.IsInteger) { return "must be a whole number"; }
        if (MultipleOf is { } m)
        {
            if (Bound(m) is not { } step || step.Sign <= 0) { return Unchecked; }
            if (!ExactDecimal.IsMultiple(n, step)) { return $"must be a multiple of {m.GetRawText()}"; }
        }
        return null;
    }

    // Text, as it would be recorded.
    public string? TextProblem(string text)
    {
        if (MinLength is { } min && text.Length < min) { return $"must be at least {min} characters"; }
        if (MaxLength is { } max && text.Length > max) { return $"must be at most {max} characters"; }
        if (Pattern is { } pattern)
        {
            if (Compile(pattern) is not { } regex) { return Unchecked; }
            try
            {
                if (!regex.IsMatch(text)) { return "does not match the form the question asks for"; }
            }
            catch (RegexMatchTimeoutException) { return Unchecked; }
        }
        return Format switch
        {
            "email" when !Email().IsMatch(text) => "must be an email address",
            "uri" when !Uri().IsMatch(text) => "must be a URL starting http:// or https://",
            "date" when !IsDate(text) => "must be a date, as YYYY-MM-DD",
            "date-time" when !IsDateTime(text) => "must be a date and time, as YYYY-MM-DDTHH:MM",
            _ => null,
        };
    }

    // How many options are ticked.
    public string? CountProblem(int count)
    {
        if (MinItems is { } min && count < min) { return $"choose at least {min}"; }
        if (MaxItems is { } max && count > max) { return $"choose at most {max}"; }
        return null;
    }

    static int Ticked(JsonElement list)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var others = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) { values.Add(item.GetString()!); } else { others++; }
        }
        return values.Count + others;
    }

    static ExactDecimal? Bound(JsonElement b) => b.ValueKind == JsonValueKind.Number ? ExactDecimal.Parse(b.GetRawText()) : null;

    // The pattern as .NET reads JavaScript's `new RegExp(pattern)`, or null when it cannot be
    // read the same way. RegexOptions.ECMAScript alone is not JavaScript: its `\s` is ASCII where
    // JavaScript's is Unicode white space, its `.` takes \r and U+2028, its `$` matches before a
    // final \n, and `\a` or `\p{L}` mean something there that they do not in JavaScript, where an
    // unknown escape is the letter itself. So those are written out as JavaScript means them, and
    // any group, escape or class this cannot vouch for answers at the machine.
    public static Regex? Compile(string pattern)
    {
        if (Translate(pattern) is not { } translated) { return null; }
        try { return new Regex(translated, RegexOptions.ECMAScript, PatternTimeout); }
        catch (ArgumentException) { return null; }
    }

    // JavaScript's \s: WhiteSpace and LineTerminator.
    const string JsSpace = @"\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";

    static string? Translate(string pattern)
    {
        if (pattern.Length > MaxPatternLength) { return null; }
        var b = new StringBuilder(pattern.Length + 16);
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length) { return null; }
                var e = pattern[++i];
                switch (e)
                {
                    case 's': b.Append(inClass ? JsSpace : $"[{JsSpace}]"); continue;
                    case 'S' when inClass: return null;
                    case 'S': b.Append($"[^{JsSpace}]"); continue;
                    case 'c' when i + 1 < pattern.Length && char.IsAsciiLetter(pattern[i + 1]): b.Append('\\').Append(e).Append(pattern[++i]); continue;
                    case 'd' or 'D' or 'w' or 'W' or 'b' or 'B' or 't' or 'n' or 'v' or 'f' or 'r' or 'x' or 'u':
                        b.Append('\\').Append(e); continue;
                    default:
                        // A digit is a back-reference or octal in both; any other letter means
                        // something else in .NET (or nothing), and punctuation is itself in both.
                        if (char.IsAsciiDigit(e) || (!char.IsLetter(e) && e < 0x80)) { b.Append('\\').Append(e); continue; }
                        return null;
                }
            }
            if (inClass)
            {
                // .NET reads -[ inside a class as subtraction; JavaScript, as two members.
                if (c == '[') { return null; }
                if (c == ']') { inClass = false; }
                b.Append(c);
                continue;
            }
            switch (c)
            {
                case '[':
                    // JavaScript's [] matches nothing and [^] anything; .NET reads the ] as a member.
                    if (pattern.AsSpan(i).StartsWith("[]") || pattern.AsSpan(i).StartsWith("[^]")) { return null; }
                    inClass = true;
                    b.Append(c);
                    if (i + 1 < pattern.Length && pattern[i + 1] == '^') { b.Append('^'); i++; }
                    break;
                case '(' when i + 1 < pattern.Length && pattern[i + 1] == '?':
                    if (i + 2 >= pattern.Length || pattern[i + 2] is not (':' or '=' or '!')) { return null; }
                    b.Append("(?").Append(pattern[i + 2]);
                    i += 2;
                    break;
                case '.': b.Append(@"[^\n\r\u2028\u2029]"); break;
                case '$': b.Append(@"(?![\s\S])"); break;
                default: b.Append(c); break;
            }
        }
        return inClass ? null : b.ToString();
    }

    // Copilot CLI 1.0.105's own checks for these formats (`g1e`), with JavaScript's \s and $, and
    // its Date.parse made a strict ISO 8601 read: a host refuses a date that could not be one.
    [GeneratedRegex(@"\A[^\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF@]+@[^\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF@]+\.[^\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF@]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\Ahttps?://[^\n\r\u2028\u2029]", RegexOptions.CultureInvariant)]
    private static partial Regex Uri();

    [GeneratedRegex(@"\A([0-9]{4}-[0-9]{2}-[0-9]{2})T([0-9]{2}:[0-9]{2})(:[0-9]{2}(\.[0-9]{1,3})?)?(Z|[+-]([0-9]{2}):([0-9]{2}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeText();

    static bool IsDate(string text) =>
        text.Length == 10 && DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    static bool IsDateTime(string text)
    {
        var m = DateTimeText().Match(text);
        if (!m.Success || !IsDate(m.Groups[1].Value)) { return false; }
        var time = m.Groups[2].Value + (m.Groups[3].Success ? m.Groups[3].Value[..3] : "");
        if (!DateTime.TryParseExact(time, m.Groups[3].Success ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { return false; }
        return !m.Groups[6].Success || (int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) <= 23 && int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture) <= 59);
    }
}

// A JSON number held exactly: Mantissa × 10^Exponent, trailing zeros moved into the exponent.
// Parse is null for text that is not a JSON number, or whose exponent is beyond what any schema
// means, never a throw.
public readonly partial record struct ExactDecimal(BigInteger Mantissa, long Exponent)
{
    const long ExponentBound = 1_000_000_000;

    public int Sign => Mantissa.Sign;

    public bool IsInteger => Mantissa.IsZero || Exponent >= 0;

    public static ExactDecimal? Parse(string text)
    {
        if (text is null || text.Length > 4096 || !JsonNumber().IsMatch(text)) { return null; }
        var s = text;
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
        var mantissa = BigInteger.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (mantissa.IsZero) { return new ExactDecimal(BigInteger.Zero, 0); }
        while (mantissa % 10 == 0) { mantissa /= 10; exponent++; }
        return new ExactDecimal(mantissa, exponent);
    }

    // The order of a and b, compared exactly.
    public static int Compare(ExactDecimal a, ExactDecimal b)
    {
        if (a.Sign != b.Sign) { return a.Sign.CompareTo(b.Sign); }
        if (a.Sign == 0) { return 0; }
        var magnitude = CompareMagnitude(BigInteger.Abs(a.Mantissa), a.Exponent, BigInteger.Abs(b.Mantissa), b.Exponent);
        return a.Sign > 0 ? magnitude : -magnitude;
    }

    static int CompareMagnitude(BigInteger a, long ae, BigInteger b, long be)
    {
        // Their orders of magnitude first; only numbers of one order are aligned, by at most as
        // many digits as one of them has, so no exponent is ever multiplied out.
        var ao = Digits(a) + ae;
        var bo = Digits(b) + be;
        if (ao != bo) { return ao.CompareTo(bo); }
        return ae >= be ? (a * BigInteger.Pow(10, (int)(ae - be))).CompareTo(b) : a.CompareTo(b * BigInteger.Pow(10, (int)(be - ae)));
    }

    // Whether value is a whole number of steps (step > 0).
    public static bool IsMultiple(ExactDecimal value, ExactDecimal step)
    {
        if (value.Mantissa.IsZero) { return true; }
        var shift = value.Exponent - step.Exponent;
        if (shift >= 0)
        {
            return (value.Mantissa * BigInteger.ModPow(10, shift, step.Mantissa)) % step.Mantissa == 0;
        }
        if (-shift > Digits(BigInteger.Abs(value.Mantissa))) { return false; }
        return value.Mantissa % (step.Mantissa * BigInteger.Pow(10, (int)-shift)) == 0;
    }

    static long Digits(BigInteger n) => n.IsZero ? 1 : n.ToString(CultureInfo.InvariantCulture).Length;

    [GeneratedRegex(@"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumber();
}
