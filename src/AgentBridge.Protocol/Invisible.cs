using System.Globalization;
using System.Text;

namespace AgentBridge.Protocol;

// What an agent's input can pass over, drop or draw as nothing before what a person typed
// starts. Shared by the reply, launch and answer checks, which the PWA mirrors
// (pwa/js/replies.js INVISIBLE), and by the host's look at an agent's input box. It covers:
//   white space;
//   control characters (Cc), which the host types as spaces;
//   format characters (Cf: U+FEFF, U+200B-U+200D, U+2060, bidi marks, tags);
//   default-ignorable code points (Hangul fillers, variation selectors, U+034F, U+115F);
//   combining marks (M), which have no base of their own;
//   unassigned code points (Cn), which a newer agent's Unicode may treat as any of those;
//   U+2800, the blank braille pattern.
// So a reply, prompt or answer is judged by its first visible base character.
//
// White space and control characters alone once counted. Then "\uFEFF/clear" passed the reply
// and launch checks as not starting with /, and Copilot CLI, which trims its input with
// JavaScript's trim() (U+FEFF included), would run it as /clear on the machine (#448). Claude
// Code drops a wider set still before it sends anything, then leaves what is left in its box: a
// reply "\u3164/" puts a bare / there, which the next reply's Enter would run. Every agent
// strips differently, and may change, so this passes over all of it, not only what one agent
// strips today.
public static class Invisible
{
    public static bool Is(Rune r) => Rune.IsWhiteSpace(r) || r.Value == 0x2800 || Rune.GetUnicodeCategory(r) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => true,
        _ => DefaultIgnorable(r.Value),
    };

    // Shows as nothing, so says nothing: an empty reply or answer.
    public static bool Blank(string? text) => First(text) is null;

    // The first character an agent's input would act on, or null when there is none. A lone
    // surrogate reads as U+FFFD, which is visible.
    public static int? First(string? text)
    {
        foreach (var r in (text ?? "").EnumerateRunes())
        {
            if (!Is(r)) { return r.Value; }
        }
        return null;
    }

    // Unicode's Default_Ignorable_Code_Point (DerivedCoreProperties.txt), which .NET does not
    // expose. Those that are Cf, Cn or M are caught by category above, but are listed whole so
    // this reads against the table; the PWA uses \p{Default_Ignorable_Code_Point} itself.
    static bool DefaultIgnorable(int c) => c switch
    {
        0x00AD or 0x034F or 0x061C or 0x3164 or 0xFEFF or 0xFFA0 => true,
        >= 0x115F and <= 0x1160 => true,
        >= 0x17B4 and <= 0x17B5 => true,
        >= 0x180B and <= 0x180F => true,
        >= 0x200B and <= 0x200F => true,
        >= 0x202A and <= 0x202E => true,
        >= 0x2060 and <= 0x206F => true,
        >= 0xFE00 and <= 0xFE0F => true,
        >= 0xFFF0 and <= 0xFFF8 => true,
        >= 0x1BCA0 and <= 0x1BCA3 => true,
        >= 0x1D173 and <= 0x1D17A => true,
        >= 0xE0000 and <= 0xE0FFF => true,
        _ => false,
    };
}
