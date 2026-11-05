namespace AgentBridge.Host.Delivery.Copilot;

// Copilot CLI's input box as its screen shows it, read before a reply is typed into it (#825).
// A reply is typed only into the box, empty, outside shell mode: words already in it would go
// with the reply, and in shell mode Enter runs what the box holds as a shell command.
//
// From Copilot CLI 1.0.105's cli-main.js (minified names; recheck them on a CLI update):
//   * The box is the InputArea (`SUn`), drawn by `qc`. With the prompt frame on (`S$t`: the
//     default outside Apple's Terminal, and what the owner's sessions draw, seen live), it is
//     the "rail" variant: a column of '╻', '┃' per line and '╹' at the left, beside a box with
//     a '▄' top and a '▀' bottom border, the text one column in. The prompt glyph is not drawn
//     there; the mode is only in colour, in the footer's label ("Shell · …", `vEe`/`Jlt`) and,
//     in an empty box, the placeholder "Execute shell command" (`W`).
//   * With the frame off (COPILOT_PROMPT_FRAME=0, Apple's Terminal, or a theme without
//     backgrounds) it is a row between two '─' rules, led by the prompt glyph:
//     `F === "shell" ? "! " : "\u276F "`.
//   * Typing '!' at the start enters shell mode and the '!' is taken out of the text (`onEdit`),
//     so the box's text never shows it.
//   * The footer is the row right under the input area (`oDo`), its parts between '·': the
//     sidebar hint ("← open sidebar") and "/ commands · ? help" (`rDo`) only while the box is
//     empty, the mode label ("Interactive · Allow All", "Shell · …"), "esc exit shell mode"
//     while empty in shell mode, "@ files · # issues" while it holds text, and first, in vim
//     mode, "-- NORMAL --" or "-- INSERT --" (`Zlt`/`XDn`). In vim's normal mode typed keys are
//     commands, and a paste is swallowed.
public sealed record CopilotInputBox(IReadOnlyList<ScreenRow> Rows, string Content)
{
    public bool Empty => Content.Length == 0;
}

// What the screen shows of the one input box it holds, whatever is in it, for a caller that
// acts on what a person left there (#804's Clear and send). Read only; it decides nothing, and
// the reader's refusals do not depend on it.
//   * Rows: the box's rows as read, its edges included.
//   * Content: all of its text, as drawn, its lines joined by '\n'. In shell mode an empty box
//     draws its placeholder, which reads as "Execute shell command".
//   * Lines: how many text rows the box draws between its edges.
//   * Shell: in shell mode, by the plain box's glyph, the placeholder, or the footer's label.
//   * Main: the prompt's own box, at the bottom of the screen directly over its footer (or a
//     turn's row) with nothing but blank rows below.
//   * Complete: its top edge is on screen, so Content is all of it. Only a rail box can be
//     seen cut off (its '┃' rows running from the top of the window); the plain box's lines
//     with its first rule and glyph row scrolled away cannot be told from output, and are not
//     a box at all.
//   * Vim: the vim mode the main box's footer shows ("-- INSERT --", "-- NORMAL --"); None
//     when it shows neither, or the box is not the main one. A running turn's row in a window
//     under 80 columns leaves the label out, so it reads None there too.
public sealed record CopilotInputBoxState(IReadOnlyList<ScreenRow> Rows, string Content, int Lines, bool Shell, bool Main, bool Complete, CopilotVimMode Vim);

public enum CopilotVimMode { None, Insert, Normal }

public static partial class CopilotInputBoxReader
{
    const char RailTop = '\u257B';
    const char Rail = '\u2503';
    const char RailBottom = '\u2579';
    const char Rule = '\u2500';
    const string Glyph = "\u276F";
    const string ShellGlyph = "!";
    // What the box shows, empty, in shell mode (`W`).
    const string ShellPlaceholder = "Execute shell command";
    // How much of what is in the box a refusal shows.
    public const int ShownLength = 500;

    // The box, empty and outside shell mode, or why a reply cannot be typed into it; Seen when
    // the screen showed the prompt's box and the problem is what is in it.
    //
    // The box is the one at the bottom of the screen, its bottom edge directly over the footer
    // and nothing but blank rows below that: the CLI draws everything else (the agent's output
    // included) above its input area. Code in that output is drawn as plain text, so rows that
    // look like a box, and a footer under them, can be printed by the agent (Trust's #843
    // review); one drawn above the real box, or above a dialog in its place, is not anchored
    // there. Rail and plain boxes are looked for together, and the screen must show exactly
    // one, so a forged rail cannot hide the real plain box under it.
    //
    // State is what the screen shows of the box, whenever it shows exactly one, refused or not
    // (CopilotInputBoxState).
    public static (CopilotInputBox? Box, string? Problem, bool Seen, CopilotInputBoxState? State) Read(IReadOnlyList<string> rows)
    {
        const string NotFound = "the host could not find Copilot's input box on the session's screen, so it may be asking something there; answer it at the machine, or reply at the machine";
        var found = Rails(rows).Concat(Plain(rows)).ToList();
        // Exactly one whole box; one cut off at the top counts only where there is no other, so
        // it is described but never changes what is refused.
        var whole = found.Where(b => b.Complete).ToList();
        if (whole.Count != 1 && !(whole.Count == 0 && found.Count == 1)) { return (null, NotFound, false, null); }
        if (whole.Count == 1) { found = whole; }
        var (box, glyph, below, complete) = found[0];
        var working = below < rows.Count && Working(rows[below]);
        var main = below < rows.Count && (working || IsFooter(rows[below])) && !rows.Skip(below + 1).Any(r => r.Trim().Length > 0);
        var shell = glyph == ShellGlyph || box.Content == ShellPlaceholder || (main && Shell(rows[below]));
        var vim = !main ? CopilotVimMode.None : Has(rows[below], VimNormal) ? CopilotVimMode.Normal : Has(rows[below], VimInsert) ? CopilotVimMode.Insert : CopilotVimMode.None;
        var state = new CopilotInputBoxState(box.Rows, box.Content, box.Rows.Count - (complete ? 2 : 1), shell, main, complete, vim);
        // A box cut off at the top was never one the host could find.
        if (!complete) { return (null, NotFound, false, state); }
        if (!main)
        {
            return (null, "the host could not find Copilot's input box at the bottom of the session's screen, over its footer, so it may be asking something there; answer it at the machine, or reply at the machine", false, state);
        }
        var footer = rows[below];
        if (Has(footer, VimNormal))
        {
            return (null, "Copilot's input box at the machine is in vim normal mode, where typed keys act as commands; go back to insert mode there first, or reply at the machine", true, state);
        }
        if (shell)
        {
            return (null, "Copilot's input box at the machine is in shell mode, where a reply would run as a shell command; leave shell mode there (Esc) first, or reply at the machine", true, state);
        }
        if (!box.Empty)
        {
            return (null, AgentBridge.Protocol.ReplyReasons.Draft(box.Content, Shown(box.Content)), true, state);
        }
        // In a window under 80 columns (`LMe`: V5.compact) a running turn's row leaves out the vim
        // and mode labels, and a box holding only spaces in shell mode draws as empty, so an
        // empty box there says nothing of its mode (Trust's #843 review). The width is the box's
        // own bottom edge, which spans the frame.
        if (working && box.Rows[^1].Text.TrimEnd().Length < CompactWidth)
        {
            return (null, "Copilot's window at the machine is under 80 columns wide, where a running turn hides whether its input box is in shell or vim mode; reply once the turn ends, or reply at the machine", false, state);
        }
        // An idle footer lists the commands while the box is empty (`rDo`), and only then, so it
        // must; a turn's own row never does, so there the box's own rows, read empty, decide.
        if (glyph is not (null or Glyph) || (!working && !Has(footer, "/ commands")))
        {
            return (null, "the host could not tell that Copilot's input box is the one for a prompt, outside shell mode; reply at the machine", false, state);
        }
        return (box, null, true, state);
    }

    // What is in the box, as a person is shown it: hidden characters escaped (ShownText.Reveal),
    // and no more than ShownLength characters of it.
    public static string Shown(string content)
    {
        var cut = content.Length <= ShownLength ? content : content[..(char.IsHighSurrogate(content[ShownLength - 1]) ? ShownLength - 1 : ShownLength)] + "\u2026";
        return AgentBridge.Protocol.ShownText.Reveal(cut, rightToLeft: true);
    }

    // The footer's word for shell mode: its label ("Shell · Allow All", or "shell" alone) or its
    // hint ("esc exit shell mode").
    static bool Shell(string row) => Parts(row).Any(part => part is "Shell" or "shell" or "esc exit shell mode");

    // The footer's parts, between its '·' separators (`oDo`, `Ft`, `Zlt`, `Xlt`). The model or
    // agent shown at the row's right end (`LMe`) is set apart by spaces only, so a run of two or
    // more spaces divides parts too.
    static List<string> Parts(string row) => ScreenParts.Of(row);

    // The footer parts that say a reply must not be typed into the box: vim's normal mode, and
    // shell mode's label (idle, or in a running turn's row) and hint. Held in the worker, with
    // the box's own rows, up to each key (ScreenRowWithout): the box's rows read the same in
    // those modes, and only the footer tells them apart (#843 review).
    public static readonly IReadOnlyList<string> UnsafeFooter = [VimNormal, "Shell", "shell", "esc exit shell mode"];

    // Something only Copilot's footer under the box, idle or a running turn's row, says: one of
    // these must still be there up to each key, or the footer has gone (a dialog, a resize)
    // and nothing says the box's mode (#843 review).
    public static readonly IReadOnlyList<string> FooterSigns =
    [
        "open sidebar", "/ commands", "? help", "@ files", "# issues", "esc exit shell mode", VimNormal, VimInsert,
        "Interactive", "interactive", "Shell", "shell", "Plan", "plan", "Autopilot", "autopilot",
        "esc interrupt", "esc stop agents", "esc edit prompt", "esc again to", " enqueue",
    ];

    // What the box's first text row starts with, leading spaces aside, when its text starts with
    // '!' (the frame-off box's shell glyph, or a '!' the CLI has not yet taken as shell mode).
    public static readonly IReadOnlyList<string> Bang = [ShellGlyph, $"{Rail} {ShellGlyph}", $"{Rail}{ShellGlyph}"];

    static bool Has(string row, string part) => Parts(row).Contains(part);

    // The row a running turn draws in the footer's place (1.0.105's `LMe` with its activity
    // indicator): the vim mode and, in plan, autopilot or shell mode, the mode label, each with
    // a '·' after it; the spinner ("Working", the turn's intent, "Executing"); then Esc's hint
    // ("esc interrupt", "esc esc interrupt", "esc stop agents", "esc edit prompt", "esc again
    // to …") and, where the CLI offers it, "· <key> enqueue". No commands or sidebar hints. A
    // row with neither of the turn's own hints (a turn being cancelled drops them) is not taken
    // for it. With the hints hidden (`hideToggleHelp`) the idle footer is drawn empty, so no
    // reply is typed into an idle session there (refused, as a box with no footer); a turn's row
    // keeps Esc's hint, which is not one of the hidden ones.
    static bool Working(string row) => TurnHints().IsMatch(row);

    [System.Text.RegularExpressions.GeneratedRegex(@"\besc (?:esc )?(?:interrupt|stop agents|edit prompt|again to \S)|\S \benqueue\b")]
    private static partial System.Text.RegularExpressions.Regex TurnHints();

    // What the footer under the prompt's box may carry (1.0.105's `oDo`): the sidebar hint, the
    // vim mode (`XDn`), the mode label (`Jlt`/`vEe`), the commands (`rDo`), the shell hint, and
    // the file and issue hints shown while the box holds text. A row with none of them is not
    // the footer.
    static bool IsFooter(string row) => Parts(row).Any(part => FooterParts.Contains(part));

    static readonly HashSet<string> FooterParts = new(StringComparer.Ordinal)
    {
        "\u2190 open sidebar", "/ commands", "? help", "@ files", "# issues", "esc exit shell mode", VimNormal, VimInsert,
        "Interactive", "interactive", "Shell", "shell", "Plan", "plan", "Autopilot", "autopilot", "autopilot (limited)",
    };

    const string VimNormal = "-- NORMAL --";
    const string VimInsert = "-- INSERT --";

    // Below this many columns a running turn's row drops its vim and mode labels (`V5.compact`).
    const int CompactWidth = 80;

    // Every rail box: '╻' over '┃' rows to '╹'. Its text is what follows each '┃'.
    // A box whose '┃' rows start at the window's first row is one cut off at the top (Complete
    // false): taller than the window, its top edge scrolled away.
    static List<(CopilotInputBox Box, string? Glyph, int Below, bool Complete)> Rails(IReadOnlyList<string> rows)
    {
        var found = new List<(CopilotInputBox, string?, int, bool)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var complete = rows[i].TrimStart().StartsWith(RailTop);
            if (!complete && !(i == 0 && rows[0].TrimStart().StartsWith(Rail))) { continue; }
            var first = complete ? i + 1 : i;
            var end = first;
            while (end < rows.Count && rows[end].TrimStart().StartsWith(Rail)) { end++; }
            if (end == first || end >= rows.Count || !rows[end].TrimStart().StartsWith(RailBottom)) { continue; }
            var lines = Enumerable.Range(first, end - first).Select(r => rows[r].TrimStart()[1..].Trim()).ToList();
            found.Add((new CopilotInputBox(Held(rows, i, end), Text(lines)), null, end + 1, complete));
            i = end;
        }
        return found;
    }

    // Every box drawn without the frame: a row led by the prompt glyph between two '─' rules.
    static List<(CopilotInputBox Box, string? Glyph, int Below, bool Complete)> Plain(IReadOnlyList<string> rows)
    {
        var found = new List<(CopilotInputBox, string?, int, bool)>();
        for (var i = 0; i + 2 < rows.Count; i++)
        {
            if (!IsRule(rows[i])) { continue; }
            var first = rows[i + 1].TrimStart();
            var glyph = first.StartsWith(Glyph, StringComparison.Ordinal) ? Glyph : first.StartsWith(ShellGlyph, StringComparison.Ordinal) ? ShellGlyph : null;
            if (glyph is null) { continue; }
            var end = i + 2;
            while (end < rows.Count && !IsRule(rows[end])) { end++; }
            if (end >= rows.Count) { continue; }
            var lines = new List<string> { first[glyph.Length..].Trim() };
            lines.AddRange(Enumerable.Range(i + 2, end - i - 2).Select(r => rows[r].Trim()));
            found.Add((new CopilotInputBox(Held(rows, i, end), Text(lines)), glyph, end + 1, true));
            i = end;
        }
        return found;
    }

    static bool IsRule(string row) => row.Trim() is { Length: > 2 } r && r.All(c => c == Rule);

    // The box's lines, without the blank ones the box pads itself with at either end.
    static string Text(List<string> lines)
    {
        var start = lines.FindIndex(l => l.Length > 0);
        if (start < 0) { return ""; }
        var end = lines.FindLastIndex(l => l.Length > 0);
        return string.Join('\n', lines.GetRange(start, end - start + 1));
    }

    static List<ScreenRow> Held(IReadOnlyList<string> rows, int from, int to) => [.. Enumerable.Range(from, to - from + 1).Select(r => new ScreenRow(r, rows[r]))];
}
