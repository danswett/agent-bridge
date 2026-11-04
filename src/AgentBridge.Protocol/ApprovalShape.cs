using System.Text.RegularExpressions;

namespace AgentBridge.Protocol;

// Whether an approval's message would be drawn as written by a renderer that reads it as Markdown.
// Every card now draws an approval as its source (the PWA's since #429, the Windows app's since #323,
// Home Assistant's since #220), so this is defence in depth: a card still holds at the machine any
// approval this says no to.
//
// The shape a host writes: a header, the agent's words, and the command and its arguments each in a
// fenced block. The agent's words are the model's own and can be anything (#212 escapes them): a line
// of backticks in an intention closes the command's fence early, so the command after it is drawn as
// Markdown (a link shows "Running tests" where `curl ... | sh` runs) while a block the agent wrote
// shows `npm test`. Read here without a Markdown parser, so it can be shared, and erring towards no:
//   - each block opens with a fence of three or more backticks or tildes, indented three spaces at
//     most, naming no language but json or diff, and closes at the first line that can close it,
//     which must exist; a line in it that starts with such a run but has anything but spaces after
//     it is refused, since renderers disagree on it (Markdig closes on a tab or a no-break space
//     after the run, marked, Home Assistant's, does not); and so is any other line in it shaped as
//     a fence (three or more backticks or tildes, indented three spaces at most): an intention
//     ending in `~~~` opened a block that swallowed the host's fence, so the command's own
//     backtick lines became markers, never drawn, and bash ran them as command substitutions;
//   - nothing but blank lines follows the first block: a host writes the agent's words, then the
//     command (or a tool, then its arguments) in blocks. A fence the agent's words open and a line
//     of the command closes would leave the rest of the command outside any block, drawn as
//     Markdown (a table that drops its last cells, so `curl ... | sh` is never shown) while every
//     fence still balances;
//   - text outside the blocks has, unescaped, no fence anywhere in a line, no list or quote (either
//     can take a block into it, ending where a line is not indented to it), no link, image or link
//     definition, no HTML or autolink, and no line indented as code;
//   - and with no block at all, the command is in the text, so nothing in it Markdown would draw
//     otherwise is allowed either, but the host's header and code spans each closed on its own line:
//     no emphasis, strikethrough or the extras' sub- and superscript, inserted or marked text
//     (`rm -rf *tmp*` would lose its stars), no backslash escape (`\$(...)` draws as `$(...)`), no
//     entity (`&num;` draws as `#`), no table pipe (the app draws a stray one as nothing), and no
//     code span across lines, which would join them into one. An underscore inside a word and a
//     lone tilde are drawn as written.
public static partial class ApprovalShape
{
    public static bool DrawsAsWritten(string message)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n');
        var prose = new List<string>();
        var blocks = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var open = FenceLine().Match(lines[i]);
            if (!open.Success)
            {
                if (blocks > 0 && lines[i].Trim().Length > 0) { return false; }
                prose.Add(lines[i]);
                continue;
            }
            if (open.Groups["info"].Value.Trim() is not ("" or "json" or "diff")) { return false; }
            var fence = open.Groups["fence"].Value;
            var end = -1;
            for (var j = i + 1; j < lines.Length && end < 0; j++)
            {
                var run = lines[j].TrimStart(' ');
                if (lines[j].Length - run.Length > 3) { continue; }
                if (!run.StartsWith(fence, StringComparison.Ordinal))
                {
                    if (FenceShaped().IsMatch(lines[j])) { return false; }
                    continue;
                }
                // Spaces only after the run close the block; anything else is refused (above).
                if (run.TrimStart(fence[0]).TrimEnd(' ').Length > 0) { return false; }
                end = j;
            }
            if (end < 0) { return false; }
            blocks++;
            i = end;
        }
        for (var i = 0; i < prose.Count; i++)
        {
            var line = Escaped().Replace(prose[i], "");
            if (Unsafe().IsMatch(line) || IndentedCode().IsMatch(prose[i]) || Container().IsMatch(prose[i])) { return false; }
            if (blocks == 0 && !Literal(i == 0 ? Header().Replace(prose[i], "") : prose[i])) { return false; }
        }
        return true;
    }

    // A line holding a command that Markdown draws as written, the backticks of code spans closed on it
    // aside.
    static bool Literal(string line) =>
        !Drawn().IsMatch(line) && !Underscore().IsMatch(line) && line.Count(c => c == '~') < 2 && line.Count(c => c == '^') < 2
        && !line.Contains("==", StringComparison.Ordinal) && !line.Contains("++", StringComparison.Ordinal) && SpansClosed(line);

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^ {0,3}(?:`{3}|~{3})")]
    private static partial Regex FenceShaped();

    // A backslash and the character it escapes, which CommonMark draws as that character.
    [GeneratedRegex(@"\\.")]
    private static partial Regex Escaped();

    // The host's "Allow **<name>**?" or "Allow **<name>** access?", its name escaped (the rest of the
    // line, the name included, is still checked as any text outside a block is).
    [GeneratedRegex(@"^Allow (?:the )?\*\*(?:\\.|[^*\\\n])+\*\*(?: access| tool)?")]
    private static partial Regex Header();

    // A fence (in a list, a quote or anywhere in a line), a link, image or reference definition, and
    // HTML or an autolink.
    [GeneratedRegex(@"```|~~~|\]\s*[(\[:]|!\[|<")]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"^(?: {4}|\t)")]
    private static partial Regex IndentedCode();

    [GeneratedRegex(@"^ {0,3}(?:>|[-*+](?:[ \t]|$)|[0-9]{1,9}[.)](?:[ \t]|$))")]
    private static partial Regex Container();

    // Every run of backticks in a line closed by another of the same length on it, in order, as a
    // renderer matches them: a code span that starts and ends there. Counting each length was not
    // enough: runs of 1, 2, 1, 2 leave the last open, and a next line of 2, 1, 2, 1 closed it, joining
    // the two lines into one span.
    static bool SpansClosed(string line)
    {
        var runs = Ticks().Matches(line).Select(m => m.Length).ToArray();
        // The next run of each one's length, found from the end, so a line of many runs is read once.
        var next = new int[runs.Length];
        var seen = new Dictionary<int, int>();
        for (var k = runs.Length - 1; k >= 0; k--)
        {
            next[k] = seen.GetValueOrDefault(runs[k], -1);
            seen[runs[k]] = k;
        }
        for (var i = 0; i < runs.Length; i++)
        {
            if (next[i] < 0) { return false; }
            i = next[i];
        }
        return true;
    }

    // Emphasis, a backslash before punctuation or ending a line, an entity, and a table pipe.
    [GeneratedRegex(@"\*|\\(?:[!-/:-@\[-`{-~]|$)|&[#A-Za-z]|\|")]
    private static partial Regex Drawn();

    // An underscore that can open or close emphasis: not between two letters or digits.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])_|_(?![\p{L}\p{N}])")]
    private static partial Regex Underscore();

    [GeneratedRegex("`+")]
    private static partial Regex Ticks();
}
