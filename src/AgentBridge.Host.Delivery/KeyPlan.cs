using System.Globalization;
using System.Text;

namespace AgentBridge.Host.Delivery;

// One attach-write-detach on the session's console: Text as one burst of key events,
// then, if Enter, a pause and Enter as a keystroke of its own, then a pause before the
// next send. This is exactly the shape agent-ha-bridge's ConsoleInjector.Send has
// delivered with for a long time; a whole form inside one attach lost its arrow keys
// and committed every field at its first option, so each send stays separate.
//
// Arrow keys travel inside Text as the VT sequences ESC [ A and ESC [ B, typed as
// characters, the way the old injector sends them and the CLI reads them.
//
// Expect, when set, makes the send conditional on the screen: inside the same attach, just
// before its first key, the worker reads those rows of the console's visible window and
// types nothing unless every one still reads exactly so (trailing spaces aside). A
// permission prompt's option is chosen by what the host read on screen a moment earlier;
// this proves the options, and which of them is highlighted, are unchanged when the key goes.
//
// Checks, when set, are made with Expect, in the same read: what rows say, without holding
// them exactly, where the rest of them changes on its own. Copilot's footer under its input
// box carries the box's mode (shell, vim normal) beside a spinner and hints that animate, and
// after the words are typed the box's first row is theirs (#843 review).
public sealed record KeySend(string Text, bool Enter, int EnterDelayMs, int PauseAfterMs, IReadOnlyList<ScreenRow>? Expect = null,
    IReadOnlyList<ScreenRowCheck>? Checks = null);

// A row of the console's visible window, counted from its top, and its text.
public sealed record ScreenRow(int Row, string Text);

public enum ScreenCheck
{
    // The row shows none of Values as one of its parts (ScreenParts.Of).
    WithoutParts,
    // The row contains at least one of Values.
    WithAny,
    // The row contains none of Values.
    WithNone,
    // The row, leading spaces aside, starts with none of Values.
    NotStartingWith,
}

// What a row of the console's visible window must say. The injector worker makes the same
// checks (AgentBridge.Injector, Program.Passes); the two must agree.
public sealed record ScreenRowCheck(int Row, ScreenCheck Kind, IReadOnlyList<string> Values)
{
    public bool Passes(IReadOnlyList<string> rows) => Row < rows.Count && Kind switch
    {
        ScreenCheck.WithoutParts => !ScreenParts.Of(rows[Row]).Any(Values.Contains),
        ScreenCheck.WithAny => Values.Any(v => rows[Row].Contains(v, StringComparison.Ordinal)),
        ScreenCheck.WithNone => !Values.Any(v => rows[Row].Contains(v, StringComparison.Ordinal)),
        _ => !Values.Any(v => rows[Row].TrimStart().StartsWith(v, StringComparison.Ordinal)),
    };
}

// A footer-like row's parts: split at its '·' separators and at runs of two or more spaces,
// each trimmed, the empty ones left out. The injector worker reads rows the same way.
public static class ScreenParts
{
    public static List<string> Of(string row) =>
        [.. row.Split('\u00B7').SelectMany(part => System.Text.RegularExpressions.Regex.Split(part, " {2,}")).Select(part => part.Trim()).Where(part => part.Length > 0)];
}

public sealed record KeyPlan(IReadOnlyList<KeySend> Sends)
{
    // Asked by the injector once it holds the typing slot, just before anything is typed (it may
    // have waited there behind another delivery): a reason to type nothing now, or null.
    public Func<string?>? Proceed { get; init; }

    public const string Up = "\u001b[A";
    public const string Down = "\u001b[B";
    // A lone escape: the key itself, which a CLI tells from an arrow by nothing following it.
    public const string Escape = "\u001b";

    // What the worker reads on stdin: a header, then one line per send with the text as
    // base64 of its UTF-8, so nothing in it can be mistaken for the line format.
    public const string Header = "agent-bridge-keys 1";

    public string ToWorkerScript()
    {
        var script = new StringBuilder(Header).Append('\n');
        foreach (var send in Sends)
        {
            script.Append("send ")
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(send.Text)))
                .Append(' ').Append(send.Enter ? '1' : '0')
                .Append(' ').Append(send.EnterDelayMs.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(send.PauseAfterMs.ToString(CultureInfo.InvariantCulture));
            foreach (var expect in send.Expect ?? [])
            {
                script.Append(' ').Append(expect.Row.ToString(CultureInfo.InvariantCulture))
                    .Append(' ').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(expect.Text)));
            }
            // A check: "~" (without parts), "+" (with any) or "^" (not starting with), its row,
            // then its values, one per line.
            foreach (var check in send.Checks ?? [])
            {
                script.Append(' ').Append(check.Kind switch { ScreenCheck.WithoutParts => '~', ScreenCheck.WithAny => '+', ScreenCheck.WithNone => '-', _ => '^' })
                    .Append(check.Row.ToString(CultureInfo.InvariantCulture))
                    .Append(' ').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', check.Values))));
            }
            script.Append('\n');
        }
        return script.ToString();
    }

    // How long the worker will take, so the host can tell a hung worker from a slow plan.
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Sends.Sum(s => (s.Enter ? s.EnterDelayMs : 0) + s.PauseAfterMs));

    // For the log: what is pressed, never the words typed.
    public string Describe() => string.Join(" | ", Sends.Select(s =>
    {
        var keys = s.Text == Escape ? "esc"
            : s.Text.Replace(Up, "").Replace(Down, "").Replace(" ", "").Length == 0
            ? string.Join(' ', new[]
            {
                Count(s.Text, Up) is > 0 and var u ? $"{u}up" : null,
                Count(s.Text, Down) is > 0 and var d ? $"{d}down" : null,
                s.Text.EndsWith(' ') ? "space" : null,
            }.Where(k => k is not null))
            : $"{s.Text.Length} chars";
        return $"{keys}{(s.Enter ? " enter" : "")}{(s.Expect is { Count: > 0 } || s.Checks is { Count: > 0 } ? " (if the screen still shows it)" : "")}".Trim();
    }));

    static int Count(string text, string what) => (text.Length - text.Replace(what, "").Length) / what.Length;
}

public static class InjectableText
{
    // What may be typed into a live terminal: every control character (C0 and C1, which
    // includes CR, LF, tab and escape) and every line or paragraph separator becomes a
    // space, so words can never submit a line early or smuggle a terminal sequence. A
    // lone surrogate becomes U+FFFD: it is not a character, and the CLI would mangle it.
    public static string Collapse(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Append(c).Append(text[++i]);
                continue;
            }
            if (char.IsSurrogate(c)) { result.Append('\uFFFD'); continue; }
            var category = char.GetUnicodeCategory(c);
            result.Append(category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c);
        }
        return result.ToString();
    }

    // How two renderings of typed words are compared: whitespace runs are one space and
    // the ends are trimmed. The CLI may trim or reflow what it was given; anything more
    // than whitespace is a different answer.
    public static string Flatten(string text)
    {
        var result = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && result.Length > 0) { result.Append(' '); }
            space = false;
            result.Append(c);
        }
        return result.ToString();
    }
}
