using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Copilot;

// What the transcript should record for one field once the keys have landed.
public abstract record Expected
{
    private Expected() { }

    // Left unanswered: absent, null or empty.
    public sealed record Omitted : Expected;
    // One option, recorded as its schema scalar.
    public sealed record Option(JsonElement Raw) : Expected;
    // Typed words: a text field, or a choice's Other.
    public sealed record Words(string Text) : Expected;
    // A number, typed as its JSON text; recorded as a number or as that text.
    public sealed record Number(JsonElement Raw) : Expected;
    public sealed record Flag(bool Value) : Expected;
    // A multi-select list's ticked options, in any order.
    public sealed record Set(IReadOnlyList<JsonElement> Raws) : Expected;
}

// The keys for one answer, and what the session should record when they land. Text is
// set for a question without fields; Fields otherwise, in the prompt's order.
public sealed record CopilotPlan(KeyPlan Keys, string? Text, IReadOnlyList<(string Name, Expected Value)> Fields);

// Turns an answer into the keys Copilot CLI's ask_user prompt needs, as agent-ha-bridge
// drives it (hooks/decision-inject.ps1). Pure: the same question and answer always give
// the same keys, so every shape is covered by unit tests rather than found live.
public static class CopilotPlanner
{
    // The pauses agent-ha-bridge settled on against live prompts: the Enter that submits a
    // reply after its words (a burst is a paste, and only a separate keypress submits it);
    // between a field's keys and its committing Enter (StepDelayMs); between the toggles of
    // one list (BridgeFormKeyGapMs); after each field (2 x StepDelayMs); and the moment
    // "Other" needs to hand the main input over before words reach it (OpenDelayMs).
    public const int ReplyEnterDelayMs = 300;
    public const int FieldEnterDelayMs = 120;
    public const int ToggleGapMs = 350;
    public const int AfterFieldMs = 240;
    public const int OtherOpenDelayMs = 400;

    // The keys, or why the answer cannot be typed. The answer has already passed
    // Answers.Check against the question the form Matches.
    public static (CopilotPlan? Plan, string? Problem) Plan(CopilotForm form, AnswerRequest answer)
    {
        if (form.Fields.Count == 0)
        {
            var words = InjectableText.Collapse(answer.Text ?? "");
            if (string.IsNullOrWhiteSpace(words)) { return (null, "the answer is empty"); }
            return (new CopilotPlan(new KeyPlan([new KeySend(words, true, ReplyEnterDelayMs, 0)]), words, []), null);
        }

        var given = answer.Fields ?? new Dictionary<string, JsonElement>();
        var sends = new List<KeySend>();
        var expected = new List<(string, Expected)>();
        foreach (var field in form.Fields)
        {
            var has = given.TryGetValue(field.Name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            List<string> keys;
            Expected record;
            if (!has)
            {
                // A text box left empty is skipped by Enter. An option list cannot be: Enter
                // commits whatever its caret is on, which is an answer nobody gave.
                if (field.Kind is not (CopilotFieldKind.Text or CopilotFieldKind.Number))
                {
                    return (null, $"Copilot's prompt cannot leave \"{field.Name}\" unanswered; choose a value for it");
                }
                (keys, record) = ([""], new Expected.Omitted());
            }
            else
            {
                var (k, r, problem) = Field(form, field, value);
                if (problem is not null) { return (null, $"\"{field.Name}\": {problem}"); }
                if (k is null) { return Other(field, value); }
                (keys, record) = (k, r!);
            }
            for (var i = 0; i < keys.Count; i++)
            {
                var last = i == keys.Count - 1;
                sends.Add(new KeySend(keys[i], last, last ? FieldEnterDelayMs : 0, last ? AfterFieldMs : ToggleGapMs));
            }
            expected.Add((field.Name, record));
        }
        return (new CopilotPlan(new KeyPlan(sends), null, expected), null);
    }

    // The keys that answer one field, each its own send; null keys mean the words go
    // through Other, which is a plan of its own.
    static (List<string>? Keys, Expected? Record, string? Problem) Field(CopilotForm form, CopilotField field, JsonElement value)
    {
        switch (field.Kind)
        {
            case CopilotFieldKind.Text:
                {
                    var words = InjectableText.Collapse(value.GetString() ?? "");
                    return ([words], string.IsNullOrWhiteSpace(words) ? new Expected.Omitted() : new Expected.Words(words), null);
                }
            case CopilotFieldKind.Number:
                return ([value.GetRawText()], new Expected.Number(value.Clone()), null);
            case CopilotFieldKind.Boolean:
                {
                    var at = value.ValueKind == JsonValueKind.True ? 0 : 1;
                    return ([ToRow(field, at)], new Expected.Flag(at == 0), null);
                }
            case CopilotFieldKind.Choice:
                {
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        // Only a lone option list opens Other on the Down past its last option;
                        // in a tabbed form that walk has never been seen to work.
                        return form.Fields.Count == 1 ? (null, null, null) : ([], null, "Other can only be typed into a question with one option list");
                    }
                    var at = Index(field, value.GetString());
                    if (at < 0) { return ([], null, "not one of the choices"); }
                    return ([ToRow(field, at)], new Expected.Option(field.Options[at].Raw), null);
                }
            default:
                return Multi(field, value);
        }
    }

    // Copilot's checkbox list: "↑/↓ select · space toggle · enter accept". It is walked once:
    // Up far enough to reach the top from any row (Up stops at the first row, as in the
    // single-choice list), so a caret somebody moved at the terminal cannot shift every
    // toggle a row, then down to the bottom pressing Space only on rows whose state has to
    // change: the schema default opens some already ticked, and toggling those blindly
    // would untick them. Ticks somebody changed at the terminal are in no transcript; see
    // docs/host-answers.md.
    static (List<string>? Keys, Expected? Record, string? Problem) Multi(CopilotField field, JsonElement value)
    {
        var wanted = new SortedSet<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                // Its Other row sits among the checkboxes, where Enter accepts what is ticked.
                return ([], null, "a multi-select list takes no other answer at Copilot's prompt");
            }
            var at = Index(field, item.GetString());
            if (at < 0) { return ([], null, "not one of the choices"); }
            wanted.Add(at);
        }
        if (wanted.Count == 0 && !field.AllowsEmpty) { return ([], null, "choose at least one option"); }
        var top = string.Concat(Enumerable.Repeat(KeyPlan.Up, field.Options.Count - 1));
        var keys = new List<string>();
        var cursor = 0;
        for (var row = 0; row < field.Options.Count; row++)
        {
            if (wanted.Contains(row) == field.Checked.Contains(row)) { continue; }
            keys.Add((keys.Count == 0 ? top : "") + string.Concat(Enumerable.Repeat(KeyPlan.Down, row - cursor)) + " ");
            cursor = row;
        }
        // Nothing to toggle: the defaults are the answer, and Enter accepts them as they are,
        // once the caret is back on a row of the list.
        if (keys.Count == 0) { keys.Add(top); }
        return (keys, new Expected.Set([.. wanted.Select(i => field.Options[i].Raw)]), null);
    }
    // Words through a lone option list's "Other (type your answer)". Up far enough to reach
    // the top from any row, then one Down per option: Up stops at the first option, and
    // the Down on the last one opens Other at once, so nothing is pressed after the walk.
    // Starting from the top rather than the default is deliberate: a caret moved at the
    // terminal would leave a walk counted from the default short, on an option, and the
    // Enter that submits the words would commit that option instead (agent-ha-bridge #240).
    static (CopilotPlan?, string?) Other(CopilotField field, JsonElement value)
    {
        var words = InjectableText.Collapse(value.GetProperty("other").GetString() ?? "");
        // Copilot trims what it is given, and an empty submit closes Other and leaves the
        // caret on the last option: nothing would be answered.
        if (string.IsNullOrWhiteSpace(words)) { return (null, $"\"{field.Name}\": the other answer is empty"); }
        var n = field.Options.Count;
        var walk = string.Concat(Enumerable.Repeat(KeyPlan.Up, n - 1)) + string.Concat(Enumerable.Repeat(KeyPlan.Down, n));
        var keys = new KeyPlan([new KeySend(walk, false, 0, OtherOpenDelayMs), new KeySend(words, true, ReplyEnterDelayMs, 0)]);
        return (new CopilotPlan(keys, null, [(field.Name, new Expected.Words(words))]), null);
    }

    // To a row of an option list from wherever its caret rests: Up far enough to reach the
    // top from any row (Up stops at the first option, as the Other walk relies on), then
    // Down to the row. Counting from the schema default instead would land one row off
    // whenever somebody had moved the caret at the terminal, and Enter would commit it.
    static string ToRow(CopilotField field, int row) =>
        string.Concat(Enumerable.Repeat(KeyPlan.Up, field.Options.Count - 1)) + string.Concat(Enumerable.Repeat(KeyPlan.Down, row));

    // The option a value names; two options with one value make it ambiguous.
    static int Index(CopilotField field, string? value)
    {
        var found = -1;
        for (var i = 0; i < field.Options.Count; i++)
        {
            if (field.Options[i].Value != value) { continue; }
            if (found >= 0) { return -1; }
            found = i;
        }
        return found;
    }
}
