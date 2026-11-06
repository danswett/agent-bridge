using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.Protocol;

// An approval holding characters a host shows as escapes (#220): a bidi control, a zero-width or
// other hidden character, a control, a right-to-left letter. Deny goes from anywhere, as for any
// approval. Approve goes only as a two-step confirmation from a person's device that this machine
// approved: the device shows the escaped command in full, and only its own "I've read it" button
// signs a command naming this question and exactly what was read (#803,
// docs/protocol/v1-sessions.md, "Escaped approvals").
public static class EscapedApproval
{
    // Offered in hello on a channel through the relay from a person's device this machine approved,
    // while its policy is confirm. Advisory: every answer is checked again.
    public const string Capability = "sessions.approveEscaped";

    // The confirmation's signed-command action: distinct from anything else a device signs.
    public const string Action = "session.approve:escaped";

    // EscapedText.Approve.
    public const string Confirm = "confirm";
    public const string Machine = "machine";
    // More escapes than a question marks (MaxMarks): what the person reads is not described
    // exactly, so Approve is the machine's whatever its policy; Deny still goes (#836 review).
    public const string TooMany = "tooMany";

    // A confirmation lasts at most this long, from when its button was pressed.
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    // Marks a question carries at most. One with more says so in Approve (TooMany), which is never
    // confirmed: what cannot be described exactly is approved at the machine. It is not truncated,
    // which would take Deny from every surface too.
    public const int MaxMarks = 512;

    // EscapedText.Shown for a message as sent.
    public static string Shown(string message) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(message)));

    // The escaped text for a message as sent, its marks those that lie wholly within it and cover
    // exactly one escape each (IsEscape), as clients check them.
    public static EscapedText For(string message, IEnumerable<(int Start, int Length)> marks, string approve) => new()
    {
        Shown = Shown(message),
        Marks = [.. marks.Where(m => m.Start >= 0 && m.Start <= message.Length && m.Length > 0 && m.Length <= message.Length - m.Start && IsEscape(message.Substring(m.Start, m.Length)))
            .Select(m => (IReadOnlyList<int>)[m.Start, m.Length])],
        Approve = approve,
    };

    // One escape exactly as ShownText writes it: \u and four upper-case hex digits, or \u{...} of
    // five or six past the first plane. A mark is one of these, nothing more: what else is in it
    // would be text a client draws as a token, or not at all, under a confirmation of all of it.
    public static bool IsEscape(string text) => EscapeGrammar.IsMatch(text);

    static readonly System.Text.RegularExpressions.Regex EscapeGrammar =
        new(@"\A\\u(?:[0-9A-F]{4}|\{[0-9A-F]{5,6}\})\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The same escaped text for the start of its message that a budget kept (SessionBudget).
    public static EscapedText Cut(EscapedText escaped, string kept) =>
        For(kept, escaped.Marks.Where(m => m.Count == 2).Select(m => (m[0], m[1])), escaped.Approve);
}
