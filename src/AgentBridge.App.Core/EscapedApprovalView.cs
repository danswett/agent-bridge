using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// An approval the host escaped anything in (#803, ui.md §6.4, "Escaped approvals"): its message in
// pieces, the host's own escapes boxed and named, and the warning's sentences. The PWA's twin is
// pwa/js/escaped.js; the two draw one message alike.
public static partial class EscapedApprovalView
{
    public const string Title = "Approve a command with hidden characters?";
    public const string ConfirmLabel = "I've read it. Approve once";
    public const string ConfirmName = "Approve once: I've read the escaped command";
    public const string ScrollHint = "Scroll to the end to approve.";
    public const string Available = "Approve is now available";
    public const string ApproveLabel = "Approve once…";
    // How long the confirm stays disabled after the end of the command is first shown.
    public static readonly TimeSpan ConfirmDelay = TimeSpan.FromSeconds(1);

    public sealed record Piece(string Text, bool Mark);

    // The message in pieces: a run the host marked is a mark. Marks outside the message, overlapping
    // or out of order are drawn as text: the message is still drawn whole.
    public static IReadOnlyList<Piece> Segments(string message, IReadOnlyList<IReadOnlyList<int>>? marks)
    {
        var pieces = new List<Piece>();
        var at = 0;
        foreach (var m in marks ?? [])
        {
            if (!Within(message, m, at)) { continue; }
            if (m[0] > at) { pieces.Add(new(message[at..m[0]], false)); }
            pieces.Add(new(message.Substring(m[0], m[1]), true));
            at = m[0] + m[1];
        }
        if (at < message.Length) { pieces.Add(new(message[at..], false)); }
        return pieces;
    }

    // One marked run as the escapes it is made of.
    public static IReadOnlyList<string> Tokens(string run) => [.. Escape().Matches(run).Select(m => m.Value)];

    public sealed record RunPiece(string Text, bool Token);

    // A marked run in pieces, every character of it kept: each escape in it a token, and whatever
    // lies between the plain text it is. Tokens alone drew "\u202E; rm -rf work" as "\u202E",
    // while the confirmation hashed all of it (#836 review).
    public static IReadOnlyList<RunPiece> Pieces(string run)
    {
        var pieces = new List<RunPiece>();
        var at = 0;
        foreach (Match m in Escape().Matches(run))
        {
            if (m.Index > at) { pieces.Add(new(run[at..m.Index], false)); }
            pieces.Add(new(m.Value, true));
            at = m.Index + m.Length;
        }
        if (at < run.Length) { pieces.Add(new(run[at..], false)); }
        return pieces;
    }

    // Whether every mark lies in the message, in order, apart, and covers exactly one escape as the
    // host writes it and nothing beside it (EscapedApproval.IsEscape). A message whose marks don't
    // check is never offered the confirmation: Approve is the machine's (QuestionViewModel).
    public static bool MarksExact(string message, IReadOnlyList<IReadOnlyList<int>>? marks)
    {
        var at = 0;
        foreach (var m in marks ?? [])
        {
            if (!Within(message, m, at)) { return false; }
            if (!EscapedApproval.IsEscape(message.Substring(m[0], m[1]))) { return false; }
            at = m[0] + m[1];
        }
        return true;
    }

    // A mark [start, length] inside the message, from at on. Measured against what remains, never
    // as start + length: [[2147483647, 6]] overflowed that to a negative end, passed, and Substring
    // threw while the card was being built (#836 review).
    static bool Within(string message, IReadOnlyList<int> m, int at) =>
        m.Count == 2 && m[0] >= at && m[0] <= message.Length && m[1] > 0 && m[1] <= message.Length - m[0];

    [GeneratedRegex(@"\\u\{[0-9A-Fa-f]{1,6}\}|\\u[0-9A-Fa-f]{4}")]
    private static partial Regex Escape();

    static readonly Dictionary<int, string> Names = new()
    {
        [0x0008] = "backspace", [0x000B] = "line tabulation", [0x000C] = "form feed", [0x000D] = "carriage return", [0x001B] = "escape",
        [0x00A0] = "no-break space", [0x00AD] = "soft hyphen", [0x061C] = "Arabic letter mark", [0x180E] = "Mongolian vowel separator",
        [0x200B] = "zero width space", [0x200C] = "zero width non-joiner", [0x200D] = "zero width joiner", [0x200E] = "left-to-right mark",
        [0x200F] = "right-to-left mark", [0x2028] = "line separator", [0x2029] = "paragraph separator", [0x202A] = "left-to-right embedding",
        [0x202B] = "right-to-left embedding", [0x202C] = "pop directional formatting", [0x202D] = "left-to-right override",
        [0x202E] = "right-to-left override", [0x2060] = "word joiner", [0x2066] = "left-to-right isolate", [0x2067] = "right-to-left isolate",
        [0x2068] = "first strong isolate", [0x2069] = "pop directional isolate", [0xFEFF] = "zero width no-break space",
    };

    static int Value(string token) => int.Parse(token.Trim('\\', 'u', '{', '}'), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);

    static string Character(int value) => value is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(value) : ((char)value).ToString();

    // A token's accessible name: "hidden character, right-to-left override".
    public static string Describe(string token)
    {
        var value = Value(token);
        var c = Character(value);
        var kind = !ShownText.HasHidden(c) && ShownText.HasRightToLeft(c) ? "right-to-left letter" : "hidden character";
        return $"{kind}, {(Names.TryGetValue(value, out var name) ? name : $"U+{value:X4}")}";
    }

    // The warning's sentences, each only where it applies; the last always.
    public static IReadOnlyList<string> Warnings(string message, IReadOnlyList<IReadOnlyList<int>>? marks)
    {
        bool hidden = false, rightToLeft = false;
        foreach (var piece in Segments(message, marks).Where(p => p.Mark))
        {
            foreach (var t in Tokens(piece.Text))
            {
                var value = Value(t);
                var c = Character(value);
                hidden |= ShownText.HasHidden(c);
                rightToLeft |= ShownText.HasRightToLeft(c) || value is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0x200F or 0x061C;
            }
        }
        List<string> said = [];
        if (hidden) { said.Add(@"Boxed codes like \u000D are characters that run but aren't drawn."); }
        if (rightToLeft) { said.Add(@"Boxed codes like \u202E and right-to-left letters can reorder what you see."); }
        said.Add("Approving runs everything below, boxed characters included.");
        return said;
    }
}
