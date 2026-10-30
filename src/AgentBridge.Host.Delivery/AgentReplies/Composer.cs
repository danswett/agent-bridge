namespace AgentBridge.Host.Delivery.AgentReplies;

// The input box of a Claude Code or Codex session as its screen shows it: the rows it spans,
// what is typed in it, and whether that is nothing. Without agent-ha-bridge's hooks the host
// cannot see a permission prompt, an AskUserQuestion or a Codex approval waiting, and a reply
// typed into one would answer it: words pick an option, and Enter accepts the highlighted one.
// Each of those takes the input box's place on screen, so a reply is typed only while the
// input box is positively on screen, and only while its rows still read as they did a moment
// before (KeySend.Expect).
public sealed record Composer(IReadOnlyList<ScreenRow> Rows, string Content, bool Empty)
{
    // Whether the box holds exactly these words. Whitespace is ignored altogether: the box
    // wraps a long line wherever it likes, even inside a hyphenated word.
    public bool Holds(string words) => Dense(words).Length > 0 && string.Equals(Dense(Content), Dense(words), StringComparison.Ordinal);

    // Whether the box holds the start of these words and no more: an agent busy drawing its own
    // output takes typed keys in slowly, and shows them as it does.
    public bool Begins(string words) => Dense(Content) is { Length: > 0 } dense && dense.Length < Dense(words).Length
        && Dense(words).StartsWith(dense, StringComparison.Ordinal);

    static string Dense(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}

// Claude Code 2.1: the input row starts with '>' at the left edge, between two full-width
// rules, with the mode line beneath:
//
//   ────────────────
//   > words
//   ────────────────
//     ⏵⏵ auto mode on (shift+tab to cycle)
//
// A permission prompt shows a rule over its own text and indented options (" > 1. Yes") with
// no rule closing them; an AskUserQuestion puts its tabs, not an input row, between its rules,
// and "Esc to cancel" beneath. Neither is an input box (seen live with 2.1.312).
public static class ClaudeComposer
{
    const int MaxInputRows = 12;
    // Shown in an empty box while replies wait in Claude's queue.
    const string QueuedHint = "Press up to edit queued messages";
    static readonly string[] Dialog = ["Esc to cancel", "Enter to select", "Enter to confirm"];

    public static Composer? Find(IReadOnlyList<string> rows)
    {
        var bottom = rows.Count - 1;
        while (bottom >= 0 && !IsRule(rows[bottom])) { bottom--; }
        if (bottom < 2) { return null; }
        var top = bottom - 1;
        while (top >= 0 && bottom - top <= MaxInputRows && !IsRule(rows[top])) { top--; }
        if (top < 0 || !IsRule(rows[top]) || bottom - top < 2) { return null; }
        var first = rows[top + 1];
        if (!(first.StartsWith('>') || first.StartsWith('❯'))) { return null; }
        for (var r = top; r < rows.Count; r++)
        {
            if (Dialog.Any(d => rows[r].Contains(d, StringComparison.OrdinalIgnoreCase))) { return null; }
        }
        var content = string.Join(' ', Enumerable.Range(top + 1, bottom - top - 1).Select(r => r == top + 1 ? rows[r][1..] : rows[r])).Trim();
        return new Composer(Span(rows, top, bottom), content, content.Length == 0 || content == QueuedHint);
    }

    static bool IsRule(string row)
    {
        var t = row.Trim();
        return t.Length >= 10 && t.All(c => c == '─');
    }

    internal static List<ScreenRow> Span(IReadOnlyList<string> rows, int from, int to) =>
        [.. Enumerable.Range(from, to - from + 1).Select(r => new ScreenRow(r, rows[r].TrimEnd()))];
}

// Codex 0.165: the input row starts with '›' (U+203A) at the left edge, any wrapped lines
// follow it, then a blank row, the status line and the footer. An empty box shows one of
// several placeholders ("Ask Codex to do anything"), so whether it is empty is read from the
// footer instead: "? for shortcuts" shows only while nothing is typed, idle or working.
//
//   › Ask Codex to do anything
//
//     GPT-6.1-Sol default · ~\project · title
//     ? for shortcuts
//
// An approval takes the box's place with options, the first of which also starts with '›'
// ("› 1. Yes, proceed (y)"), above "Press enter to confirm or esc to cancel".
public static class CodexComposer
{
    const int MaxRowsBelow = 16;
    static readonly string[] Dialog = ["Press enter to confirm", "esc to cancel", "Would you like to", "enter to submit"];

    public static Composer? Find(IReadOnlyList<string> rows)
    {
        var last = rows.Count - 1;
        while (last >= 0 && rows[last].Trim().Length == 0) { last--; }
        var top = last;
        while (top >= 0 && !rows[top].StartsWith('›')) { top--; }
        if (top < 0 || last - top > MaxRowsBelow) { return null; }
        for (var r = top; r <= last; r++)
        {
            if (Dialog.Any(d => rows[r].Contains(d, StringComparison.OrdinalIgnoreCase))) { return null; }
        }
        var end = top;
        while (end < last && rows[end + 1].Trim().Length > 0) { end++; }
        var content = string.Join(' ', Enumerable.Range(top, end - top + 1).Select(r => r == top ? rows[r][1..] : rows[r])).Trim();
        var empty = Enumerable.Range(end + 1, last - end).Any(r => rows[r].Contains("? for shortcuts", StringComparison.Ordinal));
        // An empty box is held to every row down to its footer; one with words in it, to its own.
        return new Composer(ClaudeComposer.Span(rows, top, empty ? last : end), content, empty);
    }
}
