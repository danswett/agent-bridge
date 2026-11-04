using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Protocol;

// Whether text is drawn in the order it runs, for what a person approves (#220). A bidi control can
// make `curl ... | sh` read as a comment, and a zero-width character can hide in a name, yet the text
// that runs is the text that was there all along. So a host shows such characters, and right-to-left
// letters, as visible escapes (\u202E, \u05D0) and still offers Approve once: the owner's "escape and
// allow" rule (#253, decision 3). A surface that cannot draw a prompt exactly as written holds it at
// the machine itself. The same sets as the clients' (App.Core NeedsYouEntry.HasHidden,
// pwa/js/hidden-text.js).
public static partial class ShownText
{
    // Characters a reader cannot see as they run: format characters (bidi controls, zero-width
    // spaces and joiners), controls other than a newline or a tab (a CR too: a shell runs it as part
    // of the command), private-use and unassigned code points, unpaired surrogates, the line and
    // paragraph separators, and every default-ignorable code point.
    public static bool HasHidden(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var r, out var n) != OperationStatus.Done || Hidden(r)) { return true; }
            i += n;
        }
        return false;
    }

    // Letters of the right-to-left scripts reorder what is drawn around them without any control:
    // three Hebrew letters before `; rm -rf / #` show its comment marker first.
    public static bool HasRightToLeft(string text) => text.EnumerateRunes().Any(r => r.Value is
        (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
        or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF));

    // Either of the above: text that may be drawn other than as it runs. An HTML character
    // reference counts as the character it stands for, since a Markdown renderer (the app's, Home
    // Assistant's) decodes one in prose: "&#x202E;" is drawn as a live U+202E.
    public static bool Misleads(string text) => HasHidden(text) || HasRightToLeft(text) || HasMisleadingReference(text);

    // A character reference (&#x202E;, &#8207;, &rlm;, &ZeroWidthSpace;) that stands for a
    // character above. Read as an HTML renderer reads one, which is more than Markdown decodes: any
    // number of leading zeros, no closing semicolon, and HTML5's names, which .NET's own decoder
    // does not know (Home Assistant's card and its Android notifications draw HTML).
    public static bool HasMisleadingReference(string text) =>
        text.Contains('&') && Reference().Matches(text).Any(m => Misleading(m));

    static bool Misleading(Match m)
    {
        if (m.Groups["dec"].Success || m.Groups["hex"].Success)
        {
            var hex = m.Groups["hex"].Success;
            var value = 0L;
            foreach (var c in (hex ? m.Groups["hex"] : m.Groups["dec"]).Value)
            {
                value = value * (hex ? 16 : 10) + (c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
                if (value > 0x10FFFF) { return false; }
            }
            // A renderer draws U+FFFD for nothing, a surrogate or past the last code point.
            if (value == 0 || value is >= 0xD800 and <= 0xDFFF) { return false; }
            var r = new Rune((int)value);
            return Hidden(r) || HasRightToLeft(r.ToString());
        }
        var name = m.Groups["name"].Value;
        // HTML decodes its legacy names with no semicolon, even before other letters ("&shyx"), and
        // some renderers decode others so; any such name with no semicolon counts, as a whole word.
        return MisleadingNames.Contains(name) || name.StartsWith("shy", StringComparison.Ordinal);
    }

    // HTML5's named references (html.spec.whatwg.org/entities.json) to a character HasHidden or
    // HasRightToLeft finds, a variation selector among them. None of its names is to a
    // right-to-left letter.
    static readonly HashSet<string> MisleadingNames = new(StringComparer.Ordinal)
    {
        "af", "ApplyFunction", "caps", "cups", "gesl", "gvertneqq", "gvnE", "ic", "InvisibleComma", "InvisibleTimes", "it",
        "lates", "lesg", "lrm", "lvertneqq", "lvnE", "NegativeMediumSpace", "NegativeThickSpace", "NegativeThinSpace",
        "NegativeVeryThinSpace", "NoBreak", "rlm", "shy", "smtes", "sqcaps", "sqcups", "varsubsetneq", "varsubsetneqq",
        "varsupsetneq", "varsupsetneqq", "vsubne", "vsubnE", "vsupne", "vsupnE", "ZeroWidthSpace", "zwj", "zwnj",
    };

    [GeneratedRegex(@"&(?:#(?<dec>[0-9]+)|#[xX](?<hex>[0-9a-fA-F]+)|(?<name>[A-Za-z][A-Za-z0-9]*))(?<semi>;)?")]
    private static partial Regex Reference();

    // Every hidden character as a visible escape: \u202E, or \u{E0041} past the first plane. An
    // unpaired surrogate is escaped on its own. rightToLeft: right-to-left letters too, as an
    // approval's are: even in a left-to-right line the punctuation between two of them joins their
    // run and is drawn reversed, so `echo \u05D0#;\u05D1;rm -rf ~` (as letters) would read as commented out.
    public static string Reveal(string text, bool rightToLeft = false)
    {
        if (!HasHidden(text) && !(rightToLeft && HasRightToLeft(text))) { return text; }
        var b = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var r, out var n) != OperationStatus.Done)
            {
                b.Append($"\\u{(int)text[i]:X4}");
                i += Math.Max(n, 1);
                continue;
            }
            if (Hidden(r) || (rightToLeft && HasRightToLeft(r.ToString()))) { b.Append(r.Value > 0xFFFF ? $"\\u{{{r.Value:X}}}" : $"\\u{r.Value:X4}"); } else { b.Append(r.ToString()); }
            i += n;
        }
        return b.ToString();
    }

    // Whether text holds an escape, as Reveal writes one, of a hidden character or a right-to-left
    // letter. A host sends an approval escaped, so the Needs you lane, which never offers one holding
    // such characters inline, looks for their escapes too (and refuses the six characters "\u202E"
    // an agent wrote itself along with them).
    public static bool HasRevealed(string text) => Unreveal(text) != text;

    // Each such escape read back as the character it stands for (an escape of anything else is left
    // as it is), so what is said of a message a host escaped is what would be said of it as written.
    public static string Unreveal(string text) => !text.Contains("\\u", StringComparison.Ordinal) ? text : Escape().Replace(text, m =>
        !int.TryParse(m.Groups["hex"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) ? m.Value
        : v is >= 0xD800 and <= 0xDFFF ? ((char)v).ToString()
        : Rune.TryCreate(v, out var r) && (Hidden(r) || HasRightToLeft(r.ToString())) ? r.ToString()
        : m.Value);

    public const string HiddenNote = "Contains hidden characters";
    public const string RightToLeftNote = "Contains right-to-left text";

    // What an approval's card says its message held, in words above it (ui.md §6.4), its escapes
    // counted as the characters they stand for: Approve once is offered for it, and an escape can be
    // read past while its character still acts. PowerShell ends a `#` comment at a bare CR, so
    // `Get-ChildItem # list files\u000DRemove-Item ...` reads as a comment on one line, and runs.
    public static IReadOnlyList<string> Notes(string text)
    {
        var t = Unreveal(text);
        var notes = new List<string>(2);
        if (HasHidden(t)) { notes.Add(HiddenNote); }
        if (HasRightToLeft(t)) { notes.Add(RightToLeftNote); }
        return notes;
    }

    [GeneratedRegex(@"\\u(?:(?<hex>[0-9A-Fa-f]{4})|\{(?<hex>[0-9A-Fa-f]{1,6})\})")]
    private static partial Regex Escape();

    static bool Hidden(Rune r) => r.Value is 0x2028 or 0x2029 || Ignorable(r.Value) || Rune.GetUnicodeCategory(r) switch
    {
        UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate => true,
        UnicodeCategory.Control => r.Value is not ('\n' or '\t'),
        _ => false,
    };

    // Unicode's Default_Ignorable_Code_Point (DerivedCoreProperties): drawn as nothing though it is
    // there. Variation selectors can make one name look like another.
    static bool Ignorable(int c) => c is 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFEFF or 0xFFA0
        or (>= 0x180B and <= 0x180F) or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F)
        or (>= 0xFE00 and <= 0xFE0F) or (>= 0xFFF0 and <= 0xFFF8) or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A)
        or (>= 0xE0000 and <= 0xE0FFF);
}
