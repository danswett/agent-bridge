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
    // 3. Nothing left: the delegate is named by the start of its id.
    // 4. A name that reads as the main agent's ("Main agent", "MAIN AGENT asks", "Main-agent",
    //    fullwidth, accented or Cyrillic and Greek look-alike forms) is quoted after the word
    //    Delegate.
    // The look-alike check can't be complete, so it is not the guard: every line that says who
    // asks or works names a delegate with the word Delegate in front (Titled), and the main agent
    // never has it.
    public static string For(string? name, string? id)
    {
        var clean = Clean(name, MaxLength);
        if (clean.Length == 0) { return ShortId(id) is { Length: > 0 } s ? $"Delegate {s}" : "Delegate"; }
        return PosesAsMainAgent(clean) ? $"Delegate “{Unquoted(clean)}”" : clean;
    }

    // Inside the quotes, a quote of its own could close them and carry on as other words.
    static string Unquoted(string s) => string.Concat(s.Select(c => c is '"' or '“' or '”' or '„' or '‟' or '«' or '»' or '＂' ? '\'' : c));

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
