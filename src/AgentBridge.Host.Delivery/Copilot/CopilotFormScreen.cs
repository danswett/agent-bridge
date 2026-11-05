using System.Text.RegularExpressions;

namespace AgentBridge.Host.Delivery.Copilot;

// Whether the session's screen shows this ask_user form at the field the next keys are for
// (#510 review). A screen that has settled is not enough: a permission dialog that opens
// before its transcript entry is written is just as steady, and an Enter held to its rows
// would pick whatever it highlights. Nor is any form: an MCP server's elicitation is drawn
// the same way, and may have a field of the same name. So the rows a field's keys are held to
// must be this form, at that field, before they are bound.
//
// From Copilot CLI 1.0.105's cli-main.js (minified names; recheck them on a CLI update):
//   * The form (`MIo`) is headed "Copilot needs information." for ask_user (its source is
//     Copilot's own, `JD`); an MCP server's reads "<server> needs information.". Under the
//     heading, the question's message.
//   * Then the fields (`Uxn`): with more than one, a tab bar (`Da`) naming each in order
//     (` <label> `, the selected one `[<label>]` where colour is unavailable); then only the
//     field being answered, headed `<title ?? name>:` on a row of its own (`Fxn`, `Lxn`,
//     `Oxn`, `Dxn`, `Nxn`).
//   * Under it, the key hints (`Ft`, joined by " · ", wrapping): "enter accept", "tab next"
//     unless at the last field, "shift+tab prev" unless at the first, "ctrl+d decline",
//     "esc cancel".
//   * A permission prompt is a numbered list with a plain "1. Yes", drawn bare or inside a
//     box's sides (docs/host-answers.md); a form's options are never numbered.
public static partial class CopilotFormScreen
{
    const string Heading = "Copilot needs information.";

    // Null when the rows show this form at field `index`; otherwise why not, said as a reason
    // for stopping before the next key. The parts are looked for in the order the CLI draws
    // them, each below the last.
    public static string? Showing(IReadOnlyList<string> rows, CopilotForm form, int index)
    {
        const string Prompted = "a permission prompt showed on the session's screen in place of the question's form";
        const string NotForm = "the session's screen no longer showed the question's form";
        var headed = Enumerable.Range(0, rows.Count).Where(r => Collapse(rows[r]) == Heading).ToList();
        if (headed.Count != 1) { return rows.Any(Permission) ? Prompted : NotForm; }
        var at = headed[0] + 1;
        // The whole question, as drawn: its lines, each wrapped onto as many rows as it takes.
        if (Collapse(form.Message ?? "") is { Length: > 0 } message)
        {
            var shown = "";
            while (shown != message)
            {
                if (at >= rows.Count) { return NotForm; }
                var more = Collapse(shown + " " + rows[at++]);
                if (!message.StartsWith(more, StringComparison.Ordinal)) { return NotForm; }
                shown = more;
            }
        }
        if (index < 0 || index >= form.Fields.Count) { return "the question's form has no such field"; }
        if (form.Fields.Any(f => f.Label is null))
        {
            var unheaded = form.Fields.First(f => f.Label is null);
            return $"the host cannot tell how the form heads its field \"{unheaded.Name}\", so it cannot see that the form shows it";
        }
        var label = form.Fields[index].Label!;
        var off = $"the form on the session's screen was not at the field \"{label}\"";
        if (form.Fields.Count > 1)
        {
            // Each label as it is, brackets in a title included; the field being answered alone
            // may be in brackets of the bar's own, where colour is unavailable.
            var labels = form.Fields.Select(f => f.Label!).ToList();
            var plain = Collapse(string.Join(' ', labels));
            var marked = Collapse(string.Join(' ', labels.Select((l, i) => i == index ? $"[{l}]" : l)));
            at = Next(rows, at);
            if (at >= rows.Count || Collapse(rows[at]) is var bar && bar != plain && bar != marked) { return NotForm; }
            at++;
        }
        at = Next(rows, at);
        if (at >= rows.Count || Collapse(rows[at]) != Collapse(label) + ":") { return off; }
        if (Hints(rows, at + 1, index, form.Fields.Count) is not { } end) { return off; }
        // A permission prompt drawn beside the form, above its heading or below its hints. What
        // is inside the form (an option, or words typed into a field, reading "1. Yes") is the
        // form's own.
        return rows.Take(headed[0]).Concat(rows.Skip(end + 1)).Any(Permission) ? Prompted : null;
    }

    static bool Permission(string row) => PermissionRow().IsMatch(row);

    // Why two of the form's fields could not be told apart on screen, or null. The field being
    // answered is known by its heading and, among several, by the tab hints, which say only
    // whether it is first, last or between; the tab bar marks it by colour alone, which the
    // screen read does not carry. So two fields with the same heading, both between the first
    // and the last, look the same: a screen that changed without the form moving on (an Enter
    // the CLI did not act on, while something else repainted) would pass for the next one, and
    // its keys go into the field before (#510 review, Copilot 5780128109).
    public static string? Ambiguous(CopilotForm form)
    {
        var count = form.Fields.Count;
        string Place(int i) => i == 0 ? "first" : i == count - 1 ? "last" : "between";
        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < count; i++)
        {
            if (form.Fields[i].Label is not { } label) { continue; }
            if (!seen.Add((Collapse(label), Place(i))))
            {
                return $"two of this question's fields are headed \"{label}\", and the host cannot tell on screen which of them the form is at; answer it at the machine";
            }
        }
        return null;
    }

    // The form's key hints, below `from`: accept, decline and cancel always, and with more than
    // one field, next and prev as the field's place says. Read from the hints row alone, never
    // from the question or what is typed into the field, either of which may say "tab next".
    // The hints row's last row, or null when the hints do not say this field.
    static int? Hints(IReadOnlyList<string> rows, int from, int index, int count)
    {
        if (HintParts(rows, from) is not ({ } parts, var end)) { return null; }
        if (!parts.Contains("enter accept") || !parts.Contains("ctrl+d decline") || !parts.Contains("esc cancel")) { return null; }
        return count <= 1 || (parts.Contains("tab next") == index < count - 1 && parts.Contains("shift+tab prev") == index > 0) ? end : null;
    }

    // What `Ft` can say under the form (`Uxn`'s hints), each "<key> <action>".
    static readonly HashSet<string> HintItems = new(StringComparer.Ordinal)
    {
        "\u2191/\u2193 select", "\u2191/\u2193 adjust", "space toggle", "enter accept", "tab next", "shift+tab prev", "ctrl+d decline", "esc cancel",
    };

    // The hints row's parts: the last row below `from` that ends with "esc cancel", the hints'
    // last, and the rows it wrapped from just above it, as far up as every part read is one of
    // the hints. Null when there is no such row.
    static (List<string> Parts, int End)? HintParts(IReadOnlyList<string> rows, int from)
    {
        for (var end = rows.Count - 1; end >= from; end--)
        {
            if (!Collapse(rows[end]).EndsWith("esc cancel", StringComparison.Ordinal)) { continue; }
            for (var start = Math.Max(from, end - 3); start <= end; start++)
            {
                var parts = Collapse(string.Join(' ', rows.Skip(start).Take(end - start + 1))).Split('\u00B7').Select(p => p.Trim()).ToList();
                if (parts.All(HintItems.Contains)) { return (parts, end); }
            }
            return null;
        }
        return null;
    }

    static int Next(IReadOnlyList<string> rows, int from)
    {
        while (from < rows.Count && rows[from].Trim().Length == 0) { from++; }
        return from;
    }

    static string Collapse(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // "1. Yes", highlighted or not, bare or between a box's sides.
    [GeneratedRegex(@"^[\s\u2502\u2503|]*(?:\u276F\s*)?1\.\s+Yes\b")]
    private static partial Regex PermissionRow();
}
