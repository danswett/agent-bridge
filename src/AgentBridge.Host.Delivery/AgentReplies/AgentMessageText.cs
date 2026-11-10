using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.AgentReplies;

// The one line typed into a session for another agent's message (design/agent-principals.md
// §6.4, #1335):
//
//   [Slipgates · from an agent on DESK · not typed by your user] Please rebase onto main.
//
// The receiving agent reads its prompt as plain text, so what a sender writes must not be able to
// pass for the header, and the header may carry only what this host knows itself: its own machine
// name, never the sender's session title. This is hygiene, not a security boundary: a model may
// ignore the marker, and the look-alike table below is a bounded list for the marker's letters and
// brackets, not the UTS #39 confusables data. Pure and unwired; slice 4's reply wiring calls it.
public static class AgentMessageText
{
    // The longest machine name carried, so a long one cannot push the words out of view.
    const int MaxFieldLength = 64;
    const char FullWidthAt = '＠';

    static readonly string Marker = Product.Name;

    // A bracket, then the product name, however spelled; matched on the folded copy of the words.
    static readonly Regex Imitation = new(@"\[\s*" + Regex.Escape(Product.Name.ToLowerInvariant()), RegexOptions.CultureInvariant);

    // Brackets that read as '[' or ']' and letters of the marker that read as ASCII, after NFKC (which
    // already takes the full-width and mathematical forms) and lower-casing.
    static readonly Dictionary<char, char> Lookalikes = new()
    {
        ['⁅'] = '[', ['〔'] = '[', ['【'] = '[', ['〖'] = '[', ['〚'] = '[', ['⟦'] = '[', ['❲'] = '[', ['⟬'] = '[', ['⦋'] = '[', ['﹝'] = '[', ['［'] = '[',
        ['⁆'] = ']', ['〕'] = ']', ['】'] = ']', ['〗'] = ']', ['〛'] = ']', ['⟧'] = ']', ['❳'] = ']', ['⟭'] = ']', ['⦌'] = ']', ['﹞'] = ']', ['］'] = ']',
        ['ѕ'] = 's', ['ꜱ'] = 's', ['ʂ'] = 's',
        ['ӏ'] = 'l', ['ǀ'] = 'l', ['|'] = 'l', ['ⅼ'] = 'l',
        ['і'] = 'i', ['ı'] = 'i', ['ι'] = 'i', ['ɩ'] = 'i', ['ⅰ'] = 'i',
        ['р'] = 'p', ['ρ'] = 'p',
        ['ɡ'] = 'g', ['ց'] = 'g',
        ['а'] = 'a', ['α'] = 'a', ['ɑ'] = 'a',
        ['т'] = 't', ['τ'] = 't',
        ['е'] = 'e', ['є'] = 'e', ['ε'] = 'e',
    };

    // The line to type. With `prefix` off (a message that goes without the header) the words are
    // still escaped, and one that starts with an option key (a digit or letter) gets a leading space,
    // so a prompt waiting on a choice is not answered by it.
    public static string Frame(string words, string machine, bool prefix = true)
    {
        var body = EscapeWords(words);
        if (prefix)
        {
            var header = $"[{Marker} · from an agent on {Field(machine)} · not typed by your user]";
            return body.Length == 0 ? header : header + " " + body;
        }
        return body.Length > 0 && char.IsLetterOrDigit(body[0]) ? " " + body : body;
    }

    // A trusted field, with the characters that frame the header replaced by visible ones.
    static string Field(string? value)
    {
        var text = Clean(value ?? "");
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            sb.Append(Lookalikes.TryGetValue(c, out var look) && look is '[' or ']' ? look : c);
        }
        var escaped = sb.ToString()
            .Replace('[', '(').Replace(']', ')').Replace('"', '\'').Replace('·', '-')
            .Trim();
        if (escaped.Length > MaxFieldLength) { escaped = escaped[..MaxFieldLength].TrimEnd(); }
        return escaped.Length == 0 ? "an unnamed machine" : escaped;
    }

    static string EscapeWords(string? words)
    {
        var text = Clean(words ?? "");
        var folded = new string([.. text.Select(Fold)]);
        // Replaced from the end so earlier indexes stay true; folding is one char for one char.
        foreach (var m in Imitation.Matches(folded).Reverse())
        {
            text = string.Concat(text.AsSpan(0, m.Index), "(" + Marker, text.AsSpan(m.Index + m.Length));
        }
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var atTokenStart = i == 0 || text[i - 1] == ' ';
            sb.Append(text[i] == '@' && atTokenStart ? FullWidthAt : text[i]);
        }
        return sb.ToString();
    }

    static char Fold(char c)
    {
        var lower = char.ToLowerInvariant(c);
        return Lookalikes.TryGetValue(lower, out var plain) ? plain : lower;
    }

    // NFKC, hidden and control characters removed, every kind of white space (newlines, U+2028/2029
    // included) as one space: the result is one line.
    static string Clean(string text)
    {
        var normal = Strip(Strip(text).Normalize(NormalizationForm.FormKC));
        var sb = new StringBuilder(normal.Length);
        foreach (var c in normal)
        {
            if (char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator)
            {
                if (sb.Length > 0 && sb[^1] != ' ') { sb.Append(' '); }
            }
            else { sb.Append(c); }
        }
        return sb.ToString().TrimEnd();
    }

    // Controls (keeping white space for Clean to fold), format characters (zero-width joiners,
    // bidi marks, tags), variation selectors, fillers and private-use or unpaired code points.
    static string Strip(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Hidden(rune)) { continue; }
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    static bool Hidden(Rune r)
    {
        if (Rune.IsWhiteSpace(r)) { return false; }
        if (r.Value is 0x034F or 0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0 or 0xFFFD) { return true; }
        if (r.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF) { return true; }
        return Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }
}
