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

public static class CopilotInputBoxReader
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
    public static (CopilotInputBox? Box, string? Problem, bool Seen) Read(IReadOnlyList<string> rows)
    {
        var found = Rails(rows).Concat(Plain(rows)).ToList();
        if (found.Count != 1)
        {
            return (null, "the host could not find Copilot's input box on the session's screen, so it may be asking something there; answer it at the machine, or reply at the machine", false);
        }
        var (box, glyph, below) = found[0];
        if (below >= rows.Count || !IsFooter(rows[below]) || rows.Skip(below + 1).Any(r => r.Trim().Length > 0))
        {
            return (null, "the host could not find Copilot's input box at the bottom of the session's screen, over its footer, so it may be asking something there; answer it at the machine, or reply at the machine", false);
        }
        var footer = rows[below];
        if (Parts(footer).Contains(VimNormal))
        {
            return (null, "Copilot's input box at the machine is in vim normal mode, where typed keys act as commands; go back to insert mode there first, or reply at the machine", true);
        }
        if (glyph == ShellGlyph || box.Content == ShellPlaceholder || Shell(footer))
        {
            return (null, "Copilot's input box at the machine is in shell mode, where a reply would run as a shell command; leave shell mode there (Esc) first, or reply at the machine", true);
        }
        if (!box.Empty)
        {
            return (null, $"something is already typed in Copilot's input box at the machine (\"{Shown(box.Content)}\"); send or clear it there first", true);
        }
        // An empty box's footer lists the commands (`rDo`, drawn only while the box is empty).
        if (glyph is not (null or Glyph) || !Parts(footer).Contains("/ commands"))
        {
            return (null, "the host could not tell that Copilot's input box is the one for a prompt, outside shell mode; reply at the machine", false);
        }
        return (box, null, true);
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

    // The footer's parts, between its '·' separators (`oDo`, `Ft`, `Zlt`).
    static List<string> Parts(string row) => [.. row.Split('\u00B7').Select(part => part.Trim())];

    // What the footer under the prompt's box may carry (1.0.105's `oDo`): the sidebar hint, the
    // vim mode (`XDn`), the mode label (`Jlt`/`vEe`), the commands (`rDo`), the shell hint, and
    // the file and issue hints shown while the box holds text. A row with none of them is not
    // the footer.
    static bool IsFooter(string row) => Parts(row).Any(part => FooterParts.Contains(part));

    static readonly HashSet<string> FooterParts = new(StringComparer.Ordinal)
    {
        "\u2190 open sidebar", "/ commands", "? help", "@ files", "# issues", "esc exit shell mode", VimNormal, "-- INSERT --",
        "Interactive", "interactive", "Shell", "shell", "Plan", "plan", "Autopilot", "autopilot", "autopilot (limited)",
    };

    const string VimNormal = "-- NORMAL --";

    // Every rail box: '╻' over '┃' rows to '╹'. Its text is what follows each '┃'.
    static List<(CopilotInputBox Box, string? Glyph, int Below)> Rails(IReadOnlyList<string> rows)
    {
        var found = new List<(CopilotInputBox, string?, int)>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].TrimStart().StartsWith(RailTop)) { continue; }
            var end = i + 1;
            while (end < rows.Count && rows[end].TrimStart().StartsWith(Rail)) { end++; }
            if (end == i + 1 || end >= rows.Count || !rows[end].TrimStart().StartsWith(RailBottom)) { continue; }
            var lines = Enumerable.Range(i + 1, end - i - 1).Select(r => rows[r].TrimStart()[1..].Trim()).ToList();
            found.Add((new CopilotInputBox(Held(rows, i, end), Text(lines)), null, end + 1));
            i = end;
        }
        return found;
    }

    // Every box drawn without the frame: a row led by the prompt glyph between two '─' rules.
    static List<(CopilotInputBox Box, string? Glyph, int Below)> Plain(IReadOnlyList<string> rows)
    {
        var found = new List<(CopilotInputBox, string?, int)>();
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
            found.Add((new CopilotInputBox(Held(rows, i, end), Text(lines)), glyph, end + 1));
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
