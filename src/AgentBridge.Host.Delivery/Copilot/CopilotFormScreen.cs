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
//     heading, the question's message, wrapped by the renderer (`gVr`) wherever Unicode line
//     breaking allows, so a space may be missing where a row breaks, and nowhere else (Drawn).
//   * Then the fields (`Uxn`): with more than one, a tab bar (`Da`) naming each in order, on
//     one row (` <label> ` a column apart, the selected one `[<label>]` where colour is
//     unavailable); then only the
//     field being answered, headed on a row of its own: a list, a Yes/No or a multi-select
//     (`Dxn`, `Oxn`, `Nxn`) as `<title ?? name>:`, a text or number box (`Fxn`, `Lxn`) as
//     `<title ?? name>` with no colon (and " * <error>" beside it while its input is invalid).
//     The bar shows every label only while they all fit across the window; otherwise it
//     shows those around the selected one, between arrows (`Da`, read in Bars). Whole, the bar
//     read at the first field vouches for every later field's title before the first key:
//     it holds every label and more, so each heading, drawn in the same column, fits on its
//     row too; and a title the console draws other than as written (a line break, a character
//     it shows otherwise) is drawn so in the bar too, and refused there. Windowed, it vouches
//     for none of the hidden titles, so it is refused, named, with nothing typed.
//   * Under it, the key hints (`Ft`, joined by " · ", wrapping): "enter accept", "tab next"
//     unless at the last field, "shift+tab prev" unless at the first, "ctrl+d decline",
//     "esc cancel".
//   * A permission prompt is a numbered list with a plain "1. Yes", drawn bare or inside a
//     box's sides (docs/host-answers.md), one of its options under the pointer; a form's
//     options are never numbered. `cxo` draws one prompt at a time in the session's prompt
//     place, so a live one replaces the form; one drawn against it is refused all the same.
// Each check Locate makes, named, so a refusal says which one failed (#1123): the reason the
// person reads is one sentence for several of them, and a refusal that could not be told
// apart from the log took a morning to trace to a question wrapped after a hyphen.
public enum FormCheck
{
    Shown,
    // Not exactly one heading of this form's own (none, as when its top is off the window, or two).
    Heading,
    // No row under the heading repeats the top's rule.
    BottomRule,
    // Something drawn under the form's frame.
    Under,
    // The rows under the heading do not read as the question's message.
    Message,
    // The tab bar does not name the fields.
    Bar,
    // The tab bar is the one the CLI draws at this field, but too wide for the console, so it
    // hides some of the fields' titles, and the form cannot be told from another one.
    BarWindow,
    // The form has no such field, or one the host cannot say how it is headed.
    Field,
    // The field being answered is not the one headed.
    FieldHeading,
    // The key hints do not say this field.
    Hints,
    // A permission prompt drawn in the form's place or against it.
    Prompted,
}

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

    // Showing's reason, with the check that gave it and the screen's outline for the log: where
    // its rules and headings are and the row the check failed at, never what any row says.
    public static (string? Why, FormCheck Check, int Row, string Outline) Diagnose(IReadOnlyList<string> rows, CopilotForm form, int index)
    {
        var why = Locate(rows, form, index, out _, out _, out var check, out var at);
        return (why, check, at, Outline(rows, check, at));
    }

    static string Outline(IReadOnlyList<string> rows, FormCheck check, int at)
    {
        var rules = Enumerable.Range(0, rows.Count).Where(r => IsRule(rows[r])).Select(r => $"{r}:{Indent(rows[r])}+{rows[r].Trim(' ').Length}");
        var headings = Enumerable.Range(0, rows.Count).Where(r => Heads(rows, r));
        var blank = rows.Count(r => r.Trim(' ').Length == 0);
        var widest = rows.Count == 0 ? 0 : rows.Max(r => r.TrimEnd(' ').Length);
        return $"rows={rows.Count} widest={widest} blank={blank} rules=[{string.Join(',', rules)}] headings=[{string.Join(',', headings)}] check={Name(check)}{(at >= 0 ? $"@{at}" : "")}";
    }

    // A check's name as the log says it: stable, for whatever reads the log.
    public static string Name(FormCheck check) => check switch
    {
        FormCheck.Shown => "shown",
        FormCheck.Heading => "heading",
        FormCheck.BottomRule => "bottom-rule",
        FormCheck.Under => "under",
        FormCheck.Message => "message",
        FormCheck.Bar => "bar",
        FormCheck.BarWindow => "bar-window",
        FormCheck.Field => "field",
        FormCheck.FieldHeading => "field-heading",
        FormCheck.Hints => "hints",
        _ => "prompted",
    };

    static bool IsRule(string row) => row.Trim(' ') is { Length: > 0 } line && line.All(c => c == '\u2500');

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

    static string? Locate(IReadOnlyList<string> rows, CopilotForm form, int index, out int fieldHeading, out int hintsStart) =>
        Locate(rows, form, index, out fieldHeading, out hintsStart, out _, out _);

    static string? Locate(IReadOnlyList<string> rows, CopilotForm form, int index, out int fieldHeading, out int hintsStart, out FormCheck check, out int failedAt)
    {
        var found = new Found();
        var why = Find(rows, form, index, found);
        (fieldHeading, hintsStart, check, failedAt) = why is null ? (found.Heading, found.Hints, FormCheck.Shown, -1) : (-1, -1, found.Check, found.Row);
        return why;
    }

    // What Find found: the field's heading and hints row, or the check that failed and where.
    sealed class Found
    {
        public int Heading = -1, Hints = -1, Row = -1;
        public FormCheck Check = FormCheck.Shown;

        public string Fail(FormCheck check, int row, string reason)
        {
            (Check, Row) = (reason == Prompted ? FormCheck.Prompted : check, row);
            return reason;
        }
    }

    const string Prompted = "a permission prompt showed on the session's screen in place of the question's form";
    const string NotForm = "the session's screen no longer showed the question's form";
    const string Narrow = "the session's window is too narrow for the question's form to show every field's title, so the host cannot tell it is this question; answer it at the machine, or widen the window and answer again";
    const string Under = "something other than the question's form was drawn under it on the session's screen";

    static string? Find(IReadOnlyList<string> rows, CopilotForm form, int index, Found found)
    {
        var headed = Enumerable.Range(0, rows.Count).Where(r => Heads(rows, r)).ToList();
        if (headed.Count != 1) { return found.Fail(FormCheck.Heading, headed.Count > 1 ? headed[1] : -1, CopilotPermissionScreen.Ends(rows, rows.Count) ? Prompted : NotForm); }
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
        if (bottom >= rows.Count) { return found.Fail(FormCheck.BottomRule, -1, CopilotPermissionScreen.Ends(rows, rows.Count) ? Prompted : NotForm); }
        if (rows.Skip(bottom + 1).Any(r => r.Trim(' ').Length > 0)) { return found.Fail(FormCheck.Under, bottom + 1, CopilotPermissionScreen.Ends(rows, rows.Count) ? Prompted : Under); }
        var frame = rows;
        rows = [.. rows.Take(bottom)];
        var at = headed[0] + 1;
        // The whole question, as drawn: its lines, each wrapped onto as many rows as it takes
        // (Drawn). Copilot CLI 1.0.105's renderer (`gVr`) breaks a line wherever Unicode line
        // breaking allows (`lineBreakPositions`), not only at a space: after a hyphen, between
        // CJK characters, inside a word too long for a row. Joined with a space, "from ag-" over
        // "agent-bridge-ops" read "ag- agent", and every answer to that question was refused, the
        // form steady on screen (#1123, #848). Only where a row breaks may a space be missing;
        // every space inside a row is the question's own, so "Now here" is never "Nowhere".
        if (Lines(form.Message) is { Length: > 0 } message)
        {
            at = Next(rows, at);
            if (!Drawn(rows, ref at, message, top.Trim(' ').Length - 2)) { return found.Fail(FormCheck.Message, at, NotForm); }
        }
        if (index < 0 || index >= form.Fields.Count) { return found.Fail(FormCheck.Field, -1, "the question's form has no such field"); }
        if (form.Fields.Any(f => f.Label is null))
        {
            var unheaded = form.Fields.First(f => f.Label is null);
            return found.Fail(FormCheck.Field, -1, $"the host cannot tell how the form heads its field \"{unheaded.Name}\", so it cannot see that the form shows it");
        }
        var label = form.Fields[index].Label!;
        var off = $"the form on the session's screen was not at the field \"{label}\"";
        var heading = Headed(form.Fields[index]);
        if (form.Fields.Count > 1)
        {
            // One row, read exactly as `Da` draws it at this field (Bars): each label as it is,
            // brackets in a title included, its spaces and the gaps between labels too, so two
            // titles are never read as one ("Now here" is not "Nowhere"), nor one as two. The
            // bar fits itself to the console's width less four columns (`$fn`), within the
            // frame, so it is drawn on one row; one read across rows is not taken. Too wide for
            // the console, it shows only the labels around the field being answered, between
            // arrows, and is refused below.
            at = Next(rows, at);
            if (at >= rows.Count || !Bars([.. form.Fields.Select(f => f.Label!)], index).TryGetValue(rows[at].Trim(' '), out var windowed))
            {
                return found.Fail(FormCheck.Bar, at, NotForm);
            }
            // Windowed, the bar hides some titles, and a form that differs from this question only
            // in a hidden title reads the same: its message, the labels shown, the field's heading
            // and hints. Each field's own heading is read before its keys, but a field typed into
            // stays so, so the keys could fill a form that is not this question before the one
            // title that tells them apart showed (#1137 review). Named, it is refused with nothing
            // typed, for the machine, rather than read as no form at all (#1123).
            if (windowed) { return found.Fail(FormCheck.BarWindow, at, Narrow); }
            at++;
        }
        at = Next(rows, at);
        if (at >= rows.Count || Collapse(rows[at]) != heading) { return found.Fail(FormCheck.FieldHeading, at, off); }
        if (Hints(rows, at + 1, index, form.Fields.Count) is not var (start, end)) { return found.Fail(FormCheck.Hints, at + 1, off); }
        (found.Heading, found.Hints) = (at, start);
        // A permission prompt drawn against the form: one ending just above its frame, read by
        // its structure (CopilotPermissionScreen.Ends), not by any row of the session's output
        // above it reading "1. Yes", which refused every answer to a form under an agent's
        // numbered list (#510 review, Copilot 4484009805); or any "1. Yes" row inside the frame
        // below its hints, where the form draws nothing of its own. What is inside the form
        // above that (an option, or words typed into a field, reading "1. Yes") is the form's
        // own.
        return CopilotPermissionScreen.Ends(frame, headed[0] - 1) || rows.Skip(end + 1).Any(Permission) ? found.Fail(FormCheck.Prompted, end + 1, Prompted) : null;
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

    // Every way the tab bar (`Da`) can draw the form's labels at field `index`, as its row reads
    // with its ends trimmed, each with whether it is windowed. Each label ` <label> ` (`wlo`), the
    // field being answered `[<label>]` where colour is unavailable, one column between them.
    // Every label while they all fit across the console (`$fn` counts how many do around the
    // selected one); otherwise `n` of them, the window `Hfn` centres on the selected one (from
    // `index - n / 2`, kept inside the list), "← " before it when labels are hidden on the left
    // and " →" after it when on the right. A window is one of these alone: the selected field in
    // it, the arrows saying exactly which ends are hidden, and the labels in it in the form's
    // order; read so, it is named (bar-window) rather than refused as no form at all. Six long
    // titles at 102 columns showed two and "→" (#1123). A label with an arrow of its own could
    // read as a window it is not, so such a form's bar is taken only whole.
    static Dictionary<string, bool> Bars(IReadOnlyList<string> labels, int index)
    {
        string Run(int start, int end, bool mark) =>
            string.Join(" ", labels.Skip(start).Take(end - start).Select((l, i) => mark && start + i == index ? $"[{l}]" : $" {l} "));
        var bars = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [Run(0, labels.Count, false).Trim(' ')] = false,
            [Run(0, labels.Count, true).Trim(' ')] = false,
        };
        if (labels.Any(l => l.Contains('\u2190', StringComparison.Ordinal) || l.Contains('\u2192', StringComparison.Ordinal))) { return bars; }
        for (var n = 1; n < labels.Count; n++)
        {
            var start = Math.Clamp(index - n / 2, 0, labels.Count - n);
            var end = start + n;
            foreach (var mark in new[] { false, true })
            {
                bars.TryAdd(((start > 0 ? "\u2190 " : "") + Run(start, end, mark) + (end < labels.Count ? " \u2192" : "")).Trim(' '), true);
            }
        }
        return bars;
    }

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
    static readonly HashSet<string> HintItems = new(
    [
        "\u2191/\u2193 select", "\u2191/\u2193 adjust", "space toggle", "enter accept", "tab next", "shift+tab prev", "ctrl+d decline", "esc cancel",
    ], StringComparer.Ordinal);

    // The hints row's parts: the last row below `from` whose rows up to three above it end the
    // hints ("esc cancel"), and the rows it wrapped from just above it, as far up as every part
    // read is one of the hints. Null when there is no such row. The renderer breaks a row
    // wherever line breaking allows, a hint's own words or a hint itself included: joined with
    // a space, "esc" over "cancel" or "esc can" over "cel" were not the hints, and every answer
    // in a narrow window was refused (#910). Where a row breaks, a space may be missing (Hint);
    // inside a row, every space is read as drawn, so "enteraccept" is not "enter accept".
    static (List<string> Parts, int Start, int End)? HintParts(IReadOnlyList<string> rows, int from)
    {
        for (var end = rows.Count - 1; end >= from; end--)
        {
            if (rows[end].Trim().Length == 0) { continue; }
            var first = Math.Max(from, end - 3);
            if (Hint(Joined(rows, first, end).Split('\u00B7')[^1]) != "esc cancel") { continue; }
            for (var start = first; start <= end; start++)
            {
                if (rows[start].Trim().Length == 0) { continue; }
                var parts = Joined(rows, start, end).Split('\u00B7').Select(Hint).ToList();
                if (parts.All(p => p is not null)) { return ([.. parts.OfType<string>()], start, end); }
            }
            return null;
        }
        return null;
    }

    // Rows `start` to `end`, each read with its runs of white space as one space, a line break
    // where each row ends.
    static string Joined(IReadOnlyList<string> rows, int start, int end) =>
        string.Join('\n', rows.Skip(start).Take(end - start + 1).Select(Collapse));

    // The hint a part of the hints reads as, each row break in it a space or nothing; null when
    // it is none.
    static string? Hint(string part)
    {
        var bits = part.Trim(' ', '\n').Split('\n');
        if (bits.Length > 4 || bits.Any(b => b.Length == 0)) { return null; }
        for (var breaks = 0; breaks < 1 << (bits.Length - 1); breaks++)
        {
            var text = bits[0];
            for (var i = 1; i < bits.Length; i++) { text += (((breaks >> (i - 1)) & 1) == 1 ? " " : "") + bits[i]; }
            if (HintItems.Contains(text)) { return text; }
        }
        return null;
    }

    // The question as the form draws it, line by line: each line's runs of white space as one
    // space, its ends trimmed, blank lines left out (Drawn skips the blank rows between lines).
    static string Lines(string? text) =>
        string.Join('\n', (text ?? "").Split('\n', '\r').Select(Collapse).Where(l => l.Length > 0));

    // Whether the rows from `at` read as `text` (Lines), each line wrapped onto as many rows as
    // it takes, rows of `width` columns; `at` is left after its last row, or at the row that
    // does not read so. Each row, its runs of white space as one space, must be the text's next
    // characters exactly. Between two rows of a line, the space the row broke at is not drawn;
    // where there is none, the break must be one line breaking allows (Breaks). Blank rows are
    // taken only between lines.
    static bool Drawn(IReadOnlyList<string> rows, ref int at, string text, int width)
    {
        var p = 0;
        while (true)
        {
            if (at >= rows.Count) { return false; }
            var row = Collapse(rows[at]);
            if (row.Length == 0 || row.Length > text.Length - p || string.CompareOrdinal(text, p, row, 0, row.Length) != 0) { return false; }
            (p, at) = (p + row.Length, at + 1);
            if (p == text.Length) { return true; }
            if (text[p] == '\n') { (p, at) = (p + 1, Next(rows, at)); }
            else if (text[p] == ' ') { p++; }
            else if (!Breaks(text, p, width)) { return false; }
        }
    }

    // Whether a row may break before `text[p]` with no space there: after a hyphen, a dash or a
    // slash, beside a wide character (CJK, an emoji), or inside a word longer than a row.
    static bool Breaks(string text, int p, int width)
    {
        if (text[p - 1] is '-' or '/' or '\u2010' or '\u2013' or '\u2014' || Wide(text[p - 1]) || Wide(text[p])) { return true; }
        var (start, end) = (p, p);
        while (start > 0 && text[start - 1] is not (' ' or '\n')) { start--; }
        while (end < text.Length && text[end] is not (' ' or '\n')) { end++; }
        return end - start > width;
    }

    static bool Wide(char c) =>
        char.IsSurrogate(c) || c is >= '\u1100' and <= '\u115F' or >= '\u2E80' and <= '\uA4CF' or >= '\uAC00' and <= '\uD7A3'
            or >= '\uF900' and <= '\uFAFF' or >= '\uFE30' and <= '\uFE4F' or >= '\uFF00' and <= '\uFF60' or >= '\uFFE0' and <= '\uFFE6';

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
