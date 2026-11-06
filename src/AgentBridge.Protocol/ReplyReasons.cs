using System.Globalization;

namespace AgentBridge.Protocol;

// Reasons a reply was not sent that carry what a person left unsent at the machine (#825):
// the requesting client's own reply result may show it to them, but nothing else may carry it,
// the host's log and Home Assistant's history included, since a draft can hold a pasted
// password or token (#843 review). Shared is what those carry instead: a fixed reason and the
// draft's length.
public static class ReplyReasons
{
    const string DraftOpen = "something is already typed in Copilot's input box at the machine (";
    const string DraftClose = "); send or clear it there first";
    const string Characters = " characters";

    // The refusal for a draft in Copilot's input box, for the person's own client: its length,
    // then the draft as they are shown it (escaped and cut, by the caller).
    public static string Draft(string content, string shown) =>
        $"{DraftOpen}{new StringInfo(content).LengthInTextElements.ToString(CultureInfo.InvariantCulture)}{Characters}: \"{shown}\"{DraftClose}";

    // A reason as a log or Home Assistant may carry it: a draft's refusal as its fixed reason and
    // length only; any other reason as it is.
    public static string Shared(string reason)
    {
        if (!reason.StartsWith(DraftOpen, StringComparison.Ordinal) || !reason.EndsWith(DraftClose, StringComparison.Ordinal)) { return reason; }
        var rest = reason.AsSpan(DraftOpen.Length);
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) { digits++; }
        var length = digits > 0 && rest[digits..].StartsWith(Characters, StringComparison.Ordinal) ? rest[..digits].ToString() : "some";
        return $"{DraftOpen}{length}{Characters}, not shown here{DraftClose}";
    }
}
