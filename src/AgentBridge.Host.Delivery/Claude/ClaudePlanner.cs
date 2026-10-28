using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Claude;

// One question of Claude Code's AskUserQuestion prompt, as the keys have to drive it.
public sealed record ClaudeField(string Name, bool Multi, IReadOnlyList<string> Options);

// The prompt read from the tool call's own input, the way agent-ha-bridge reads it
// (ConvertFrom-ClaudeAskUserQuestion), rather than from the Question a client saw.
public sealed record ClaudeForm(IReadOnlyList<ClaudeField> Fields)
{
    // Claude's tool takes at most four questions, and agent-ha-bridge drives no more.
    public const int MaxFields = 4;

    public static (ClaudeForm? Form, string? Problem) Read(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return (null, "the question's input could not be read; answer it at the machine");
        }
        var fields = new List<ClaudeField>();
        foreach (var q in list.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object) { continue; }
            var text = Str(q, "question");
            if (string.IsNullOrWhiteSpace(text)) { continue; }
            var options = new List<string>();
            if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    var label = o.ValueKind == JsonValueKind.String ? o.GetString() : o.ValueKind == JsonValueKind.Object ? Str(o, "label") : null;
                    if (!string.IsNullOrWhiteSpace(label)) { options.Add(label); }
                }
            }
            var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
            fields.Add(new ClaudeField(text, multi, options));
        }
        if (fields.Count is 0 or > MaxFields) { return (null, "Claude's prompt has more questions than the host drives; answer it at the machine"); }
        if (fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Count) { return (null, "two of Claude's questions read the same; answer it at the machine"); }
        if (fields.Any(f => f.Options.Count == 0)) { return (null, "a question has no options to choose; answer it at the machine"); }
        return (new ClaudeForm(fields), null);
    }

    // Whether the question the client was shown is this prompt: the same questions in the
    // same order, each with the same kind and the same options in the same order.
    public bool Matches(Question question) =>
        question.Fields.Count == Fields.Count
        && question.Fields.Zip(Fields).All(p =>
            p.First.Name == p.Second.Name
            && p.First.Type == (p.Second.Multi ? FieldType.Multi : FieldType.Choice)
            && (p.First.Options ?? []).Select(o => o.Value).SequenceEqual(p.Second.Options, StringComparer.Ordinal));

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

// The keys for one answer, and what Claude should record for each question: the label
// chosen, or a multi-select's labels in list order joined by ", ". Ambiguous names the
// multi-selects whose recorded string could also be some other selection, so a match on
// it proves nothing.
public sealed record ClaudePlan(KeyPlan Keys, IReadOnlyList<(string Name, string Answer)> Fields, IReadOnlySet<string>? Ambiguous = null);

// Turns an answer into the keys Claude Code's AskUserQuestion prompt takes, as
// agent-ha-bridge drives it (hooks/decision-inject.ps1, Get-BridgeFormPayloads and
// Send-CopilotSessionForm). Pure, so every shape is covered by unit tests.
//
//   * A single choice: Down from the first row to the chosen one, and Enter. The prompt
//     opens on its first row; agent-ha-bridge counts from there and so does this.
//   * A multi-select list: its rows are numbered on screen, and typing a digit toggles that
//     row wherever the cursor is, so each chosen row is its digit, sent on its own (as one
//     burst the toggles were dropped). Then Down once per row, the options and the "Type
//     something" row, lands on Submit, and Enter presses it. agent-ha-bridge verified this
//     live against Claude Code 2.1.273.
//   * Each question is committed with its own Enter; several questions end on a review
//     screen, which AnswerService submits with one more Enter if the answer is not recorded.
public static class ClaudePlanner
{
    public const int FieldEnterDelayMs = 120;
    public const int ToggleGapMs = 350;
    public const int AfterFieldMs = 240;

    public static (ClaudePlan? Plan, string? Problem) Plan(ClaudeForm form, AnswerRequest answer)
    {
        var given = answer.Fields ?? new Dictionary<string, JsonElement>();
        var sends = new List<KeySend>();
        var expected = new List<(string, string)>();
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in form.Fields)
        {
            if (!given.TryGetValue(field.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return (null, $"Claude's prompt cannot leave \"{field.Name}\" unanswered; choose a value for it");
            }
            if (!field.Multi)
            {
                if (value.ValueKind != JsonValueKind.String) { return (null, $"\"{field.Name}\": Claude's prompt takes one of its options here; type anything else at the machine"); }
                var index = IndexOf(field, value.GetString()!);
                if (index < 0) { return (null, $"\"{field.Name}\": not one of the choices"); }
                sends.Add(new KeySend(Repeat(KeyPlan.Down, index), true, FieldEnterDelayMs, AfterFieldMs));
                expected.Add((field.Name, field.Options[index]));
                continue;
            }
            if (value.ValueKind != JsonValueKind.Array) { return (null, $"\"{field.Name}\": expected a list of choices"); }
            var picked = new SortedSet<int>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) { return (null, $"\"{field.Name}\": Claude's prompt takes only its options here; type anything else at the machine"); }
                var index = IndexOf(field, item.GetString()!);
                if (index < 0) { return (null, $"\"{field.Name}\": not one of the choices"); }
                // Only the first nine rows have a digit to type.
                if (index >= 9) { return (null, $"\"{field.Name}\": only the first nine options can be ticked from here; answer it at the machine"); }
                picked.Add(index);
            }
            // The walk to Submit with nothing ticked submits whatever the list holds, which
            // agent-ha-bridge refuses for Claude too.
            if (picked.Count == 0) { return (null, $"\"{field.Name}\": choose at least one option"); }
            foreach (var index in picked) { sends.Add(new KeySend((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), false, 0, ToggleGapMs)); }
            sends.Add(new KeySend(Repeat(KeyPlan.Down, field.Options.Count + 1), true, FieldEnterDelayMs, AfterFieldMs));
            expected.Add((field.Name, string.Join(", ", picked.Select(i => field.Options[i]))));
            if (Joinable(field)) { ambiguous.Add(field.Name); }
        }
        return (new ClaudePlan(new KeyPlan(sends), expected, ambiguous), null);
    }

    // Claude records a multi-select as its labels joined by ", ". When a label holds a comma
    // or two labels read the same, different selections can join to one string ("A", "B"
    // and "A, B"), and the record cannot say which was ticked.
    static bool Joinable(ClaudeField field) =>
        field.Options.Any(o => o.Contains(',', StringComparison.Ordinal))
        || field.Options.Select(InjectableText.Flatten).Distinct(StringComparer.Ordinal).Count() != field.Options.Count;

    static int IndexOf(ClaudeField field, string value)
    {
        for (var i = 0; i < field.Options.Count; i++)
        {
            if (string.Equals(field.Options[i], value, StringComparison.Ordinal)) { return i; }
        }
        return -1;
    }

    static string Repeat(string key, int count) => string.Concat(Enumerable.Repeat(key, count));
}

// Whether what Claude recorded for the call is the answer typed: every question's answer,
// from toolUseResult.answers when the build writes it, else from the result's own text
// ('"question"="answer"'). Whitespace runs aside; anything else is a different answer.
public static class ClaudeVerifier
{
    public static (bool Matched, string? Reason) Check(ClaudePlan plan, ClaudeAnswerRecord record)
    {
        if (record.IsError) { return (false, "Claude recorded the question as declined or failed, not answered"); }
        foreach (var (name, answer) in plan.Fields)
        {
            if (plan.Ambiguous?.Contains(name) == true)
            {
                return (false, $"\"{name}\" has options that join into one another, so Claude's record cannot show which were ticked");
            }
            if (record.Answers is { } answers)
            {
                if (!answers.TryGetValue(name, out var got)) { return (false, "Claude recorded no answer to one of its questions"); }
                if (InjectableText.Flatten(got) != InjectableText.Flatten(answer)) { return (false, "Claude recorded a different answer"); }
            }
            else if (!InjectableText.Flatten(record.Text).Contains(InjectableText.Flatten($"\"{name}\"=\"{answer}\""), StringComparison.Ordinal))
            {
                return (false, "Claude recorded a different answer");
            }
        }
        return (true, null);
    }
}
