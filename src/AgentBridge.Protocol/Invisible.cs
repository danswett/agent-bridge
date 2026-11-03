using System.Globalization;
using System.Text;

namespace AgentBridge.Protocol;

// What an agent's input can see past, or drop, before it reads what a person typed: white
// space, control characters (the host types them as spaces) and format characters (Cf: U+FEFF,
// U+200B-U+200D, U+2060, the bidi marks), which show as nothing at all. Shared by the reply,
// launch and answer checks, which the PWA mirrors (pwa/js/replies.js INVISIBLE).
//
// Format characters were once counted as text, so "\uFEFF/clear" passed the reply and launch
// checks as not starting with /. Copilot CLI trims what it is given with JavaScript's trim(),
// which removes U+FEFF, and then runs it as /clear on the machine (#448). Other agents differ,
// and may change, so every Cf character is passed over, not only those one CLI strips today.
static class Invisible
{
    public static bool Is(Rune r) => Rune.IsWhiteSpace(r) || Rune.IsControl(r) || IsFormat(r);

    // Shown as nothing, so an answer of nothing else is empty. Control characters stay text
    // here, as they always have for answers: refusing them is not this check's to decide.
    public static bool Blank(string? text) => text is null || text.EnumerateRunes().All(r => Rune.IsWhiteSpace(r) || IsFormat(r));

    // The first character an agent's input would act on, or null when there is none. A lone
    // surrogate reads as U+FFFD, which is visible.
    public static int? First(string text)
    {
        foreach (var r in text.EnumerateRunes())
        {
            if (!Is(r)) { return r.Value; }
        }
        return null;
    }

    static bool IsFormat(Rune r) => Rune.GetUnicodeCategory(r) == UnicodeCategory.Format;
}
