using System.Text.RegularExpressions;
using AgentBridge.Host.Core.Parsing;

namespace AgentBridge.Host.Delivery.Copilot;

// Whether the session's screen shows this ask_user form at the field the next keys are for
// (#510 review). A screen that has settled is not enough: a permission dialog that opens
// before its transcript entry is written is just as steady, and an Enter held to its rows
// would pick whatever it highlights. Nor is any form: an MCP server's elicitation is drawn
// the same way, and may have a field of the same name. So the rows a field's keys are held to
// must be this form, at that field, before they are bound.
//
// From Copilot CLI 1.0.105's cli-main.js (minified names; recheck them on a CLI update):
//   * The form (`MIo`) is a box drawn with its top and bottom rules alone (paddingX 1), its
//     first row inside "Copilot needs information." for ask_user (its source is
//     Copilot's own, `JD`); an MCP server's reads "<server> needs information.". Under the
//     heading, the question's message.
//   * Then the fields (`Uxn`): with more than one, a tab bar (`Da`) naming each in order
//     (` <label> `, the selected one `[<label>]` where colour is unavailable); then only the
//     field being answered, headed on a row of its own: a list, a Yes/No or a multi-select
//     (`Dxn`, `Oxn`, `Nxn`) as `<title ?? name>:`, a text or number box (`Fxn`, `Lxn`) as
//     `<title ?? name>` with no colon (and " * <error>" beside it while its input is invalid).
//     The bar shows every label only while they all fit across the window; otherwise it
//     shows those around the selected one, between arrows (`Da`), which the bar check refuses
//     at the first field, before any key. So the bar read there vouches for every later
//     field's title before the first key: one that fits holds every label and more, so each
//     heading, drawn in the same column, fits on its row too; and a title the console draws
//     other than as written (a line break, a character it shows otherwise) is drawn so in the
//     bar too, and refused there.
//   * Under it, the key hints (`Ft`, joined by " · ", wrapping): "enter accept", "tab next"
//     unless at the last field, "shift+tab prev" unless at the first, "ctrl+d decline",
//     "esc cancel".
//   * A permission prompt is a numbered list with a plain "1. Yes", drawn bare or inside a
//     box's sides (docs/host-answers.md), one of its options under the pointer; a form's
//     options are never numbered. `cxo` draws one prompt at a time in the session's prompt
//     place, so a live one replaces the form; one drawn against it is refused all the same.
public static partial class CopilotFormScreen
{
    const string Heading = "Copilot needs information.";

    // Recognition only: neither result proves the box empty or authorizes a key.
    internal enum PinnedOtherView { Unrecognized, PlaceholderOrDraft, TextOrDraft }

    // The one pinned layout captured for 1.0.105: the open Other label inside the form,
    // followed immediately by the rail box below its bottom rule. The capture did not
    // distinguish the placeholder from an identical draft, or establish that a blank row
    // is empty. Other layouts and multi-row boxes remain unrecognized.
    internal static PinnedOtherView PinnedOther(IReadOnlyList<string> rows, CopilotForm form)
    {
        if (form.Fields is not [{ Kind: CopilotFieldKind.Choice }]) { return PinnedOtherView.Unrecognized; }
        var headed = Enumerable.Range(0, rows.Count).Where(r => Heads(rows, r)).ToList();
        if (headed.Count != 1) { return PinnedOtherView.Unrecognized; }
        var rule = rows[headed[0] - 1].TrimEnd(' ');
        var bottom = headed[0] + 1;
        while (bottom < rows.Count && rows[bottom].TrimEnd(' ') != rule) { bottom++; }
        if (bottom + 3 >= rows.Count || rows.Skip(bottom + 4).Any(r => r.Trim(' ').Length != 0)) { return PinnedOtherView.Unrecognized; }
        var frame = rows.Take(bottom + 1).ToList();
        if (Locate(frame, form, 0, out var field, out var hints) is not null) { return PinnedOtherView.Unrecognized; }
        if (frame.Skip(field + 1).Take(hints - field - 1).Count(r => r.Trim(' ') == "\u276F Other (type your answer below)") != 1)
        {
            return PinnedOtherView.Unrecognized;
        }
        var indent = rule.Length - rule.TrimStart(' ').Length;
        var width = rule.Length - indent;
        if (width < 2) { return PinnedOtherView.Unrecognized; }
        var prefix = new string(' ', indent);
        if (rows[bottom + 1].TrimEnd(' ') != prefix + "\u257B" + new string('\u2584', width - 1)
            || rows[bottom + 3].TrimEnd(' ') != prefix + "\u2579" + new string('\u2580', width - 1))
        {
            return PinnedOtherView.Unrecognized;
        }
        var textRow = rows[bottom + 2].TrimEnd(' ');
        var textPrefix = prefix + "\u2503 ";
        if (!textRow.StartsWith(textPrefix, StringComparison.Ordinal)) { return PinnedOtherView.Unrecognized; }
        var text = textRow[textPrefix.Length..];
        if (text.Length == 0 || text.Length > width - 2 || text.Any(char.IsControl)) { return PinnedOtherView.Unrecognized; }
        if (text != OtherPlaceholder && (OtherPlaceholder.StartsWith(text, StringComparison.Ordinal)
            || text.EndsWith('\u2026') && OtherPlaceholder.StartsWith(text[..^1], StringComparison.Ordinal)))
        {
            return PinnedOtherView.Unrecognized;
        }
        return text == "Type your answer..." ? PinnedOtherView.PlaceholderOrDraft : PinnedOtherView.TextOrDraft;
    }

    // Null when the rows show this form at field `index`; otherwise why not, said as a reason
    // for stopping before the next key. The parts are looked for in the order the CLI draws
    // them, each below the last.
    public static string? Showing(IReadOnlyList<string> rows, CopilotForm form, int index) => Locate(rows, form, index, out _, out _);

    // The rows of the field being answered, strictly between its heading and the hints, where
    // what is typed into it is drawn; null when the rows do not show this form at that field.
    // Words are looked for here alone, the rows read as one so a long answer is found across the
    // rows it wraps onto: Copilot CLI 1.0.105 draws a text field's input (`Fxn`, `qc`) between a
    // rule above and below with no edge down either side, its first row "❯ " and the rest
    // indented, so no edge says which rows go together (#510 review, Trust on 8335427d). Counted
    // across the whole screen, a field titled "Data" over hints starting "enter" read "ae" across
    // the two, and the words "ae" typed between them hid it, so their Enter was never pressed
    // (Copilot 4480874526). The heading and hints are never part of it; it is less than the
    // whole screen, so it never shows words the screen does not. In an option list's rows, an
    // empty Other box's placeholder is read as the empty box it is: counted, it was the words
    // "answer" already showing before they were typed, and once they replaced it they showed no
    // more often, so their Enter was withheld (#510 review, Copilot 4482280064). Taken out of
    // every read alike, it can only make a box showing it read as empty, never words show that
    // are not there. A list's alone: a text or number box draws no placeholder, so there it was
    // an answer reading "Type your answer..." that was taken out, and its Enter withheld
    // (Copilot 4482568470). Kept while Other is refused (AnswerService), for the box a live
    // session takes Other's words in, which draws the same placeholder.
    public static List<string>? Input(IReadOnlyList<string> rows, CopilotForm form, int index)
    {
        if (Locate(rows, form, index, out var heading, out var hints) is not null) { return null; }
        var region = rows.Skip(heading + 1).Take(hints - heading - 1);
        return form.Fields[index].Kind == CopilotFieldKind.Choice ? [.. region.Select(Unplaceheld)] : [.. region];
    }

    static string? Locate(IReadOnlyList<string> rows, CopilotForm form, int index, out int fieldHeading, out int hintsStart)
    {
        (fieldHeading, hintsStart) = (-1, -1);
        const string Prompted = "a permission prompt showed on the session's screen in place of the question's form";
        const string NotForm = "the session's screen no longer showed the question's form";
        const string Under = "something other than the question's form was drawn under it on the session's screen";
        var headed = Enumerable.Range(0, rows.Count).Where(r => Heads(rows, r)).ToList();
        if (headed.Count != 1) { return CopilotPermissionScreen.Ends(rows, rows.Count) ? Prompted : NotForm; }
        // The form's frame closes with the top's own rule again (`MIo`), and the frame is the
        // last thing drawn: the CLI draws a prompt (`ekn`, an elicitation among them) in place of
        // its input box and that box's footer, so nothing is under a form it is showing. Read to
        // the end of the screen, the hints could be another form's (an MCP server's elicitation
        // drawn under it), and an input drawn there (a shell's) was not looked at, though keys go
        // to whichever is live (#510 review, Copilot 4479698033). Everything after this is read
        // inside the frame.
        var top = rows[headed[0] - 1].TrimEnd(' ');
        var bottom = headed[0] + 1;
        while (bottom < rows.Count && rows[bottom].TrimEnd(' ') != top) { bottom++; }
        if (bottom >= rows.Count) { return CopilotPermissionScreen.Ends(rows, rows.Count) ? Prompted : NotForm; }
        if (rows.Skip(bottom + 1).Any(r => r.Trim(' ').Length > 0)) { return CopilotPermissionScreen.Ends(rows, rows.Count) ? Prompted : Under; }
        var frame = rows;
        rows = [.. rows.Take(bottom)];
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
        var heading = Headed(form.Fields[index]);
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
        if (at >= rows.Count || Collapse(rows[at]) != heading) { return off; }
        if (Hints(rows, at + 1, index, form.Fields.Count) is not var (start, end)) { return off; }
        (fieldHeading, hintsStart) = (at, start);
        // A permission prompt drawn against the form: one ending just above its frame, read by
        // its structure (CopilotPermissionScreen.Ends), not by any row of the session's output
        // above it reading "1. Yes", which refused every answer to a form under an agent's
        // numbered list (#510 review, Copilot 4484009805); or any "1. Yes" row inside the frame
        // below its hints, where the form draws nothing of its own. What is inside the form
        // above that (an option, or words typed into a field, reading "1. Yes") is the form's
        // own.
        return CopilotPermissionScreen.Ends(frame, headed[0] - 1) || rows.Skip(end + 1).Any(Permission) ? Prompted : null;
    }

    static bool Permission(string row) => PermissionRow().IsMatch(row);

    // A form's own heading: the first row inside its frame (`MIo`: a box drawn with its top and
    // bottom borders alone, paddingX 1, the heading first in it), so right under a row of the
    // top border's rule and one column in from it. Not any row that reads so: the question, a
    // field's title and an option are drawn inside the frame, under the heading or a blank row,
    // or as one of a list's rows at the list's own indent. Counted wherever they read so, they
    // refused a valid answer, and an option that started highlighted ("❯" before it) read so
    // only once the keys had moved off it, so the Enter was withheld from a part-typed field
    // (#510 review, Copilot 4478963305). Two forms on screen still have two headings.
    static bool Heads(IReadOnlyList<string> rows, int r)
    {
        if (r == 0 || Collapse(rows[r]) != Heading) { return false; }
        var rule = rows[r - 1].TrimEnd(' ');
        var line = rule.TrimStart(' ');
        return line.Length > 0 && line.All(c => c == '\u2500') && Indent(rows[r]) == rule.Length - line.Length + 1;
    }

    static int Indent(string row) => row.Length - row.TrimStart(' ').Length;

    // The field's heading as drawn (CopilotFieldSchema.Heading).
    static string Headed(CopilotField field) =>
        CopilotFieldSchema.Heading(field.Label!, field.Kind is CopilotFieldKind.Text or CopilotFieldKind.Number);

    // Why two of the form's fields could not be told apart on screen, or null
    // (CopilotFieldSchema.Alike, which QuestionMapper lists for the machine too).
    public static string? Ambiguous(CopilotForm form) =>
        CopilotFieldSchema.Alike([.. form.Fields.Select(f => f.Label is null ? null : Headed(f))]) is { } i
            ? $"two of this question's fields are headed \"{form.Fields[i].Label}\", and the host cannot tell on screen which of them the form is at; answer it at the machine"
            : null;

    // The form's key hints, below `from`: accept, decline and cancel always, and with more than
    // one field, next and prev as the field's place says. Read from the hints row alone, never
    // from the question or what is typed into the field, either of which may say "tab next".
    // The hints row's last row, or null when the hints do not say this field.
    static (int Start, int End)? Hints(IReadOnlyList<string> rows, int from, int index, int count)
    {
        if (HintParts(rows, from) is not ({ } parts, var start, var end)) { return null; }
        if (!parts.Contains("enter accept") || !parts.Contains("ctrl+d decline") || !parts.Contains("esc cancel")) { return null; }
        return count <= 1 || (parts.Contains("tab next") == index < count - 1 && parts.Contains("shift+tab prev") == index > 0) ? (start, end) : null;
    }

    // What `Ft` can say under the form (`Uxn`'s hints), each "<key> <action>".
    static readonly HashSet<string> HintItems = new(StringComparer.Ordinal)
    {
        "\u2191/\u2193 select", "\u2191/\u2193 adjust", "space toggle", "enter accept", "tab next", "shift+tab prev", "ctrl+d decline", "esc cancel",
    };

    // The hints row's parts: the last row below `from` that ends with "esc cancel", the hints'
    // last, and the rows it wrapped from just above it, as far up as every part read is one of
    // the hints. Null when there is no such row.
    static (List<string> Parts, int Start, int End)? HintParts(IReadOnlyList<string> rows, int from)
    {
        for (var end = rows.Count - 1; end >= from; end--)
        {
            if (!Collapse(rows[end]).EndsWith("esc cancel", StringComparison.Ordinal)) { continue; }
            for (var start = Math.Max(from, end - 3); start <= end; start++)
            {
                var parts = Collapse(string.Join(' ', rows.Skip(start).Take(end - start + 1))).Split('\u00B7').Select(p => p.Trim()).ToList();
                if (parts.All(HintItems.Contains)) { return (parts, start, end); }
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

    // An empty Other box, as Copilot CLI 1.0.105 draws it (`Dxn`, and the other lists' `jl` with
    // `placeholder:"Type your answer..."`): the caret, then the placeholder, whole or cut short
    // with an ellipsis where the row is too narrow; the caret alone in its place. A text or number
    // box (`Fxn`, `Lxn`) draws no placeholder.
    const string OtherPlaceholder = "Type your answer...";

    static string Unplaceheld(string row)
    {
        if (CaretRow().Match(row) is not { Success: true } m) { return row; }
        var text = m.Groups[2].Value;
        var shown = text == OtherPlaceholder || text.Length > 1 && text[^1] == '\u2026' && OtherPlaceholder.StartsWith(text[..^1], StringComparison.Ordinal);
        return shown ? m.Groups[1].Value : row;
    }

    [GeneratedRegex(@"^(\s*\u276F\s*)(.*?)\s*$")]
    private static partial Regex CaretRow();

    // "1. Yes", highlighted or not, bare or between a box's sides.
    [GeneratedRegex(@"^[\s\u2502\u2503|]*(?:\u276F\s*)?1\.\s+Yes\b")]
    private static partial Regex PermissionRow();
}
