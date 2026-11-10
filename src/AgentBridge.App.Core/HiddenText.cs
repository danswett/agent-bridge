using System.Buffers;
using System.Globalization;
using System.Text;

namespace AgentBridge.App.Core;

// Text that is not on screen as it runs (#165, #220): characters drawn as nothing or somewhere
// other than where they run, and right-to-left letters, which reorder what is drawn around them.
// The Needs you lane refuses a prompt with either; a code block draws the first as escapes and
// reads left to right (ui.md §6.4).
public static class HiddenText
{
    // Characters a reader cannot see as they run: format characters (bidi controls, zero-width
    // spaces and joiners), controls other than a newline or a tab (a CR too: a shell runs it as part
    // of the command), private-use and unassigned code points, unpaired surrogates, the line and
    // paragraph separators, and what draws blank but is part of a shell's word: every space but
    // U+0020, and U+2800 (pwa/js/hidden-text.js HIDDEN).
    public static bool HasHidden(string text)
    {
        var blankBase = true;
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var r, out var n) != OperationStatus.Done || Hidden(r) || (blankBase && IsMark(r))) { return true; }
            blankBase = NextBase(r, blankBase);
            i += n;
        }
        return false;
    }

    // A combining mark whose base is whitespace or the start of a line draws on nothing, or on a
    // neighbour: `true \u0307#; echo RAN` drew its `#` as part of a comment while a shell ran the
    // echo. A mark after a visible character is an accent and stays as written (ui.md §6.4).
    static bool IsMark(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    // What the next mark would sit on: a mark keeps the base it had, so a run of them after a space is all hidden.
    static bool NextBase(Rune r, bool blankBase) => IsMark(r) ? blankBase : Rune.IsWhiteSpace(r);

    // The right-to-left blocks: Hebrew to Arabic Extended, their presentation forms, and the
    // supplementary planes' right-to-left ranges (pwa/js/needs-you.js RTL).
    public static bool HasRightToLeft(string text) => text.EnumerateRunes().Any(RightToLeft);

    static bool RightToLeft(Rune r) => r.Value is
        (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
        or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF);

    static bool Hidden(Rune r) => r.Value is 0x2028 or 0x2029 or Blank || Ignorable(r.Value) || Rune.GetUnicodeCategory(r) switch
    {
        UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate => true,
        UnicodeCategory.Control => r.Value is not ('\n' or '\t'),
        // Every space but the space itself: a shell takes U+00A0, U+2000-U+200A, U+202F, U+205F,
        // U+3000 and U+1680 as part of a word, so `true\u00A0#; curl … | sh` drew as a comment
        // and bash ran the curl (#546).
        UnicodeCategory.SpaceSeparator => r.Value != ' ',
        _ => false,
    };

    // A symbol that always draws blank, though a shell takes it as part of a word as the spaces
    // above (PowerShell ran `Write-Output ok\u2800#; …`'s second command, #546). The Hangul
    // fillers and U+17B4/U+17B5, which draw blank too, are default-ignorable (Ignorable).
    const int Blank = 0x2800;

    // Every default-ignorable code point, drawn as nothing though it is there: variation selectors
    // (which can make one name look like another), the combining grapheme joiner, Hangul fillers,
    // tags (Unicode's DerivedCoreProperties, Default_Ignorable_Code_Point; pwa/js/needs-you.js).
    static bool Ignorable(int c) => c is 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFEFF or 0xFFA0
        or (>= 0x180B and <= 0x180F) or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F)
        or (>= 0xFE00 and <= 0xFE0F) or (>= 0xFFF0 and <= 0xFFF8) or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A)
        or (>= 0xE0000 and <= 0xE0FFF);

    // Any such character drawn as a visible escape (\u202E), so a line reads in the order it runs:
    // the lane's line, and every code block (#220, #296), whose Copy takes the escapes as drawn.
    // rightToLeft: right-to-left letters too. Even in a left-to-right line, punctuation between two
    // of them joins their run and is drawn reversed (bidi rules W6, N1, L2): `echo א#;ב;rm -rf ~`
    // draws as `echo ב;#א;rm -rf ~`, commented out, and runs `rm -rf ~`. The owner's rule (#253,
    // decision 3) is to escape them and still offer Approve once; Hebrew or Arabic in a code block
    // or an approval is the harder to read for it.
    public static string Reveal(string text, bool rightToLeft = false)
    {
        var b = new StringBuilder(text.Length);
        var blankBase = true;
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var r, out var n) != OperationStatus.Done)
            {
                b.Append($"\\u{(int)text[i]:X4}");
                i += Math.Max(n, 1);
                continue;
            }
            if (Hidden(r) || (blankBase && IsMark(r)) || (rightToLeft && RightToLeft(r))) { b.Append(r.Value > 0xFFFF ? $"\\u{{{r.Value:X}}}" : $"\\u{r.Value:X4}"); } else { b.Append(r.ToString()); }
            blankBase = NextBase(r, blankBase);
            i += n;
        }
        return b.ToString();
    }
}
