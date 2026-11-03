using System.Globalization;
using System.Text;

namespace AgentBridge.App.Core;

// A delegate's name as every surface shows it: the strip, the Delegates tab, the inspector, the
// conversation's events, the status line, a question's heading and the Needs you lane (ui.md §6.3
// and §8; docs/design/subagents.md §6). The name is the starter's own description of the job, so a
// prompt-injected delegate chooses it. The host only collapses its whitespace and cuts it to 120
// bytes, so a delegate could start a nested task called "Main agent" and have its words shown
// under what looks like the main agent's own label, or reverse "tnega niaM" with a bidi override
// (#250's security review). One rule, here, so the PWA can follow it case for case.
public static class DelegateLabel
{
    // Characters, not bytes: about what a strip item and the inspector's heading show.
    public const int MaxLength = 60;

    // 1. Bidi controls, zero-width and other format characters, controls and default-ignorables
    //    go; any whitespace, line and paragraph separators included, becomes one space.
    // 2. Longer than MaxLength, it is cut at a whole character with an ellipsis.
    // 3. A middle dot, and the dots drawn like it, becomes "-": it is the separator between a
    //    status's facts, and a name with one ("Idle · Read logs") read as a fact of its own (#286).
    // 4. Nothing left: the delegate is named by the start of its id.
    // 5. A name that reads as the main agent's ("Main agent", "MAIN AGENT asks", "Main-agent",
    //    fullwidth, accented or Cyrillic and Greek look-alike forms) is quoted after the word
    //    Delegate.
    // The look-alike check can't be complete, so it is not the guard: every line that says who
    // asks or works names a delegate with the word Delegate in front (Titled), and the main agent
    // never has it.
    public static string For(string? name, string? id)
    {
        var clean = Undotted(Clean(name, MaxLength));
        if (clean.Length == 0) { return ShortId(id) is { Length: > 0 } s ? $"Delegate {s}" : "Delegate"; }
        return PosesAsMainAgent(clean) ? $"Delegate “{Unquoted(clean)}”" : clean;
    }

    // U+00B7 and what is drawn like it or normalises to it, as pwa/js/attribution.js's DOTS: the
    // fact separator, real or forged. By code point, since U+10101 is outside the BMP.
    static readonly HashSet<int> Dots = [0x00B7, 0x0387, 0x1427, 0x16EB, 0x2022, 0x2027, 0x2219, 0x22C5, 0x2E31, 0x2E33, 0x30FB, 0x318D, 0xA78F, 0xFF65, 0x10101];

    static string Undotted(string s) => Replaced(s, r => Dots.Contains(r.Value) ? "-" : null);

    // Inside the quotes, a quote of its own could close them and carry on as other words: every
    // character Unicode files as an opening or closing quote (Pi, Pf) or marks Quotation_Mark, and
    // those drawn like one: primes, the Hebrew geresh and gershayim, modifier-letter primes and
    // apostrophes, the double acute, and ornament quotes (#303's security review).
    static readonly HashSet<int> QuoteLike =
    [
        0x0022, 0x201A, 0x201E, 0x2E42, 0x300C, 0x300D, 0x300E, 0x300F, 0x301D, 0x301E, 0x301F,
        0xFE41, 0xFE42, 0xFE43, 0xFE44, 0xFF02, 0xFF07, 0xFF62, 0xFF63,
        0x2032, 0x2033, 0x2034, 0x2035, 0x2036, 0x2037, 0x2057,
        0x05F3, 0x05F4, 0x02B9, 0x02BA, 0x02BB, 0x02BC, 0x02DD, 0x02EE,
        0x275B, 0x275C, 0x275D, 0x275E, 0x1F676, 0x1F677, 0x1F678,
    ];

    static bool IsQuote(Rune r) => QuoteLike.Contains(r.Value) || Rune.GetUnicodeCategory(r) is UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation;

    static string Unquoted(string s) => Replaced(s, r => IsQuote(r) ? "'" : null);

    // Commas and what is drawn like them. A label read aloud (ui.md §9) joins its facts with ", ",
    // so a name of "x, idle, DEV-VM2" would read as facts of its own to a screen reader (#303's
    // security review).
    static readonly HashSet<int> Commas =
    [
        0x002C, 0x055D, 0x060C, 0x066B, 0x07F8, 0x1363, 0x1802, 0x1808, 0x201A, 0x2E41, 0x2E4C, 0x3001,
        0xA4FE, 0xA60D, 0xA6F5, 0xFE10, 0xFE11, 0xFE50, 0xFE51, 0xFF0C, 0xFF64,
    ];

    // Words a label reads aloud, with no comma of their own: each becomes a space, and a run of
    // spaces one.
    public static string Aloud(string text)
    {
        var spaced = Replaced(text, r => Commas.Contains(r.Value) ? " " : null);
        return ReferenceEquals(spaced, text) ? text : string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    static string Replaced(string s, Func<Rune, string?> with)
    {
        StringBuilder? b = null;
        var at = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (with(r) is { } instead)
            {
                b ??= new StringBuilder(s.Length).Append(s, 0, at);
                b.Append(instead);
            }
            else { b?.Append(r.ToString()); }
            at += r.Utf16SequenceLength;
        }
        return b?.ToString() ?? s;
    }

    // Whether a label already says it is a delegate's, so a line can say "Delegate" once.
    public static bool SaysDelegate(string label) => label == "Delegate" || label.StartsWith("Delegate ", StringComparison.Ordinal);

    // The label with "Delegate" in front, unless it says so already: the inspector's heading read
    // aloud, the conversation's events, the status line, a question's heading and Needs you.
    public static string Titled(string label) => SaysDelegate(label) ? label : $"Delegate {label}";

    // The id as a reply to the main agent may quote it ("Ask main agent to stop it"): Copilot's
    // are GUIDs. An id with anything else in it is not put in a reply at all, since it is the one
    // piece of the delegate's that goes there.
    public static string? SafeId(string? id) =>
        id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':') ? id : null;

    static string ShortId(string? id)
    {
        var safe = new string(Clean(id, 128).Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return safe.Length <= 8 ? safe : safe[..8];
    }

    internal static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var b = new StringBuilder(text.Length);
        var space = false;
        foreach (var r in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r))
            {
                space = b.Length > 0;
                continue;
            }
            if (Hidden(r)) { continue; }
            if (space)
            {
                b.Append(' ');
                space = false;
            }
            b.Append(r.ToString());
        }
        var s = b.ToString();
        var info = new StringInfo(s);
        return info.LengthInTextElements <= max ? s : info.SubstringByTextElements(0, max - 1).TrimEnd() + "…";
    }

    // Format (Cf: bidi controls, zero-width space and joiners, soft hyphen, tags), controls,
    // unpaired surrogates, private use and unassigned code points; and the default-ignorables that
    // Unicode files under other categories (Hangul fillers, variation selectors and the like).
    static bool Hidden(Rune r)
    {
        switch (Rune.GetUnicodeCategory(r))
        {
            case UnicodeCategory.Format:
            case UnicodeCategory.Control:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.OtherNotAssigned:
                return true;
        }
        var v = r.Value;
        return v is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFFA0
            || v is >= 0x180B and <= 0x180F
            || v is >= 0xFE00 and <= 0xFE0F
            || v is >= 0xE0000 and <= 0xE0FFF;
    }

    // NFKD folds fullwidth and other compatibility forms, its marks are dropped, and case goes;
    // then the words, split at anything but a letter or digit: "main" followed by "agent…", or
    // "mainagent…" run together. By rune, not UTF-16 unit, so a mark outside the BMP (U+1D165)
    // is dropped like any other rather than splitting the word. Letters of other scripts that
    // look like those in "main agent" are read as them (a subset of Unicode's confusables).
    static bool PosesAsMainAgent(string clean)
    {
        var folded = new StringBuilder();
        foreach (var r in clean.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) { continue; }
            if (!Rune.IsLetterOrDigit(r)) { folded.Append(' '); continue; }
            var lower = Rune.ToLowerInvariant(r);
            if (LookAlike(lower.Value) is { } latin) { folded.Append(latin); }
            else { folded.Append(lower.ToString()); }
        }
        var words = folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].StartsWith("mainagent", StringComparison.Ordinal)) { return true; }
            if (words[i] == "main" && i + 1 < words.Length && words[i + 1].StartsWith("agent", StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    // Lower case, after NFKD: Cyrillic, Greek, Armenian and Latin IPA letters drawn like the
    // letters of "main agent" in some case. Unlisted look-alikes still get the word Delegate.
    static char? LookAlike(int v) => v switch
    {
        0x043C or 0x03BC => 'm',                                      // м (from М), μ (from Μ)
        0x0430 or 0x03B1 or 0x0251 => 'a',                            // а, α, ɑ
        0x0456 or 0x04CF or 0x03B9 or 0x0131 or 0x0269 or 0x04C0 => 'i', // і, ӏ, ι, ı, ɩ, Ӏ
        0x03BD or 0x0578 or 0x057C => 'n',                            // ν (from Ν), ո, ռ
        0x0261 or 0x050D or 0x0581 => 'g',                            // ɡ, ԍ, ց
        0x0435 or 0x03B5 => 'e',                                      // е, ε (from Ε)
        0x0442 or 0x03C4 => 't',                                      // т (from Т), τ (from Τ)
        _ => null,
    };
}
