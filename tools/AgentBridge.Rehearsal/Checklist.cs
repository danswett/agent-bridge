using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Rehearsal;

// docs/parity-checklist.md read as data: its step ids, the tool commands it tells a person
// to run, and the parity.md rows it cites. CI checks all three against the tool and
// parity.md, so the runbook cannot drift from either.
internal static partial class Checklist
{
    // A step is a table row whose first cell is its id in bold: | **E2-01** | …
    [GeneratedRegex(@"^\|\s*\*\*(?<id>[A-Z][A-Za-z0-9]*-[0-9]{2}[a-z]?)\*\*\s*\|", RegexOptions.Multiline)]
    private static partial Regex StepRow();

    // A parity.md row, cited by its section and the start of its first cell: §2 "Single choice"
    [GeneratedRegex("§(?<section>[0-9]+) \"(?<prefix>[^\"]+)\"")]
    private static partial Regex RowRef();

    [GeneratedRegex(@"^## (?<n>[0-9]+)\. ")]
    private static partial Regex Section();

    [GeneratedRegex("`(?<code>[^`\n]+)`")]
    private static partial Regex Span();

    public const string Tool = "agent-bridge-rehearsal";

    public static IReadOnlyList<string> StepIds(string checklist) =>
        [.. StepRow().Matches(checklist).Select(m => m.Groups["id"].Value)];

    public static IReadOnlyList<(int Section, string Prefix)> RowRefs(string checklist) =>
        [.. RowRef().Matches(checklist).Select(m => (int.Parse(m.Groups["section"].Value), m.Groups["prefix"].Value))];

    // Every invocation of the tool: from inline code, and from lines of fenced blocks.
    public static IReadOnlyList<string> Commands(string checklist)
    {
        var found = new List<string>();
        var fenced = false;
        foreach (var raw in checklist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (fenced)
            {
                if (From(line) is { } c) { found.Add(c); }
                continue;
            }
            foreach (System.Text.RegularExpressions.Match m in Span().Matches(line))
            {
                if (From(m.Groups["code"].Value) is { } c) { found.Add(c); }
            }
        }
        return found;
    }

    static string? From(string text)
    {
        var at = text.IndexOf(Tool + " ", StringComparison.Ordinal);
        if (at < 0) { return null; }
        // A tool name inside a longer word (a path, a project name) is not an invocation.
        if (at > 0 && !(char.IsWhiteSpace(text[at - 1]) || text[at - 1] is '(' or '&' or '=')) { return null; }
        var command = text[at..];
        // The command ends at a pipe, a closing parenthesis, a semicolon or a comment, outside
        // quotes: `$x = (agent-bridge-rehearsal …)`, `… | ConvertFrom-Json`, `… # why`.
        char? quote = null;
        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (quote is { } q) { if (ch == q) { quote = null; } continue; }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch is '|' or ')' or ';' || (ch == '#' && char.IsWhiteSpace(command[i - 1]))) { return command[..i].Trim(); }
        }
        return command.Trim();
    }

    // As PowerShell splits a simple command line: whitespace, with "…" or '…' kept whole.
    public static IReadOnlyList<string> Split(string command)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        var any = false;
        foreach (var ch in command)
        {
            if (quote is { } q)
            {
                if (ch == q) { quote = null; } else { sb.Append(ch); }
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; any = true; continue; }
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 || any) { words.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0 || any) { words.Add(sb.ToString()); }
        return words;
    }

    // parity.md's feature rows, by section: the first cell of each table row, with the
    // Markdown that does not change what it says (backticks, bold) taken out.
    public static IReadOnlyList<(int Section, string Row)> ParityRows(string parity)
    {
        var rows = new List<(int, string)>();
        int? section = null;
        foreach (var raw in parity.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Section().Match(line) is { Success: true } s) { section = int.Parse(s.Groups["n"].Value); continue; }
            if (line.StartsWith("## ", StringComparison.Ordinal)) { section = null; continue; }
            if (section is not { } n || !line.StartsWith("| ", StringComparison.Ordinal)) { continue; }
            var first = line[2..line.IndexOf('|', 2)].Trim();
            if (first is "Feature / behaviour" || first.StartsWith("---", StringComparison.Ordinal)) { continue; }
            rows.Add((n, Plain(first)));
        }
        return rows;
    }

    public static string Plain(string cell) => cell.Replace("`", "").Replace("**", "");
}
