using System.Globalization;
using System.Text;

namespace AgentBridge.Protocol;

// A sub-agent's `name` as a host sends it (v1-sessions.md, Subagent; design/subagents.md §6).
// The name is the starter's own description of the job, so a prompt-injected agent chooses it:
// one could start a nested task called "Main agent", reverse "tnega niaM" with a bidi override,
// hide words behind zero-width characters, or fill the session's budget with one long name
// (#263). The clients already show only a safe label (the app's DelegateLabel, #250; the PWA's
// delegateName, #252; ui.md §6.3). This applies the same rules once, where the host makes the
// name, so every other reader (Home Assistant's attributes, notifications, a future client) gets
// it clean too. The clients keep their own cleaning as defence in depth, so the result has to
// read the same after they clean it again.
public static class DelegateName
{
    // The clients' cap, in characters (text elements), as a strip item or a heading shows.
    public const int MaxLength = 60;

    const string Opening = "Delegate “";
    const string Closing = "”";

    // 1. Bidi controls, zero-width and other format characters, controls and default-ignorables
    //    go; any whitespace, line and paragraph separators included, becomes one space.
    // 2. Longer than MaxLength characters or Limits.SubagentName bytes, it is cut at a whole
    //    character with an ellipsis. Both: one character can be a base with hundreds of marks.
    // 3. Nothing left: null, so a client names the delegate by its id, as for one with no name.
    // 4. A name that reads as the main agent's ("Main agent", "MAIN  AGENT asks", "Main-agent",
    //    fullwidth, accented or Cyrillic and Greek look-alike forms) becomes Delegate “<name>”,
    //    still within both caps. A name already in that form stays as it is, so cleaning twice
    //    (the HA connector does, after the host) never wraps it again.
    public static string? Clean(string? name)
    {
        var text = Strip(name);
        if (text.Length == 0) { return null; }
        var cut = Cut(text, MaxLength, Limits.SubagentName);
        if (!PosesAsMainAgent(cut) || IsLabelled(cut)) { return cut; }
        // From the capped text, not the whole: it is a prefix of it, so the shorter cut is the same.
        var inner = Cut(Unquoted(cut), MaxLength - Opening.Length - Closing.Length,
            Limits.SubagentName - Encoding.UTF8.GetByteCount(Opening + Closing));
        return Opening + inner + Closing;
    }

    static bool IsQuote(char c) => c is '"' or '“' or '”' or '„' or '‟' or '«' or '»' or '＂';

    // Inside the quotes, a quote of its own could close them and carry on as other words.
    static string Unquoted(string s)
    {
        if (s.AsSpan().IndexOfAny("\"“”„‟«»＂") < 0) { return s; }
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++) { if (IsQuote(chars[i])) { chars[i] = '\''; } }
        return new string(chars);
    }

    // Already in the form rule 4 gives: "Delegate", then the name in quotes with none of its own.
    // A client that labels names itself (the app's DelegateLabel) leaves this as it is too.
    public static bool IsLabelled(string s) =>
        s.Length > Opening.Length + Closing.Length && s.StartsWith(Opening, StringComparison.Ordinal) && s.EndsWith(Closing, StringComparison.Ordinal)
        && s.AsSpan(Opening.Length, s.Length - Opening.Length - Closing.Length).IndexOfAny("\"“”„‟«»＂") < 0;

    // The delegate as a line that says it asks or works names it ("<this> asks · …"): the cleaned
    // name after the word "Delegate", unless it starts with that already, or "A delegate" with no
    // name. The look-alike check above cannot be complete ("Ma1n agent" passes it), so this word
    // is the guard, as in the clients (ui.md §6.3): the main agent's own lines never have it.
    public static string Titled(string? name) =>
        Clean(name) is not { } label ? "A delegate"
        : label == "Delegate" || label.StartsWith("Delegate ", StringComparison.Ordinal) ? label
        : "Delegate " + label;

    static string Strip(string? text)
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
        return b.ToString();
    }

    static string Cut(string s, int maxLength, int maxBytes)
    {
        var info = new StringInfo(s);
        if (info.LengthInTextElements <= maxLength && Encoding.UTF8.GetByteCount(s) <= maxBytes) { return s; }
        const string ellipsis = "…";
        var budget = maxBytes - Encoding.UTF8.GetByteCount(ellipsis);
        var b = new StringBuilder();
        var bytes = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        for (var n = 0; n < maxLength - 1 && e.MoveNext(); n++)
        {
            var element = e.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > budget)
            {
                // A first character too big for the whole budget (a base under hundreds of
                // marks) keeps as much of itself as fits rather than leaving only the ellipsis.
                if (n == 0) { b.Append(Limits.KeepStart(element, budget)); }
                break;
            }
            b.Append(element);
            bytes += size;
        }
        return b.ToString().TrimEnd() + ellipsis;
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
    // "mainagent…" run together. Letters of other scripts that look like those in "main agent"
    // are read as them (a subset of Unicode's confusables). No such list is complete, which is
    // why the clients also put the word "Delegate" before every delegate's label.
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
    // letters of "main agent" in some case.
    static char? LookAlike(int v) => v switch
    {
        0x043C or 0x03BC => 'm',                                         // м (from М), μ (from Μ)
        0x0430 or 0x03B1 or 0x0251 => 'a',                               // а, α, ɑ
        0x0456 or 0x04CF or 0x03B9 or 0x0131 or 0x0269 or 0x04C0 => 'i', // і, ӏ, ι, ı, ɩ, Ӏ
        0x03BD or 0x0578 or 0x057C => 'n',                               // ν (from Ν), ո, ռ
        0x0261 or 0x050D or 0x0581 => 'g',                               // ɡ, ԍ, ց
        0x0435 or 0x03B5 => 'e',                                         // е, ε (from Ε)
        0x0442 or 0x03C4 => 't',                                         // т (from Т), τ (from Τ)
        _ => null,
    };
}
