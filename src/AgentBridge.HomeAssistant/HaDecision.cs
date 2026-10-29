using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// A question as agent-ha-bridge's card draws it: the daemon's captured field
// (Get-DecisionSchemaFields), how a session's controls are armed for it
// (Set-CopilotMqttDecision) and cleared after it (Clear-CopilotMqttDecision), and how a
// multi-select slot is written and read back. Ported from hooks/decision-mqtt.ps1 and
// hooks/decision-bridge-common.ps1, and checked against what those functions publish
// (Fixtures/ha-decisions.json).

// One field as the daemon captures it. Options are the labels the card shows; Field is the
// host's own, which says what each label means as an answer.
public sealed record DecisionField(
    string Label,
    IReadOnlyList<string> Options,
    bool IsText,
    bool MultiSelect,
    IReadOnlyList<int> DefaultIndexes,
    bool AllowsEmpty,
    Field? Source = null);

// What arming a question publishes. TerminalOnly questions are shown with nothing to answer
// them with, and nothing on the card is ever read as their answer.
public sealed record DecisionPlan(
    string QuestionId,
    string Question,
    IReadOnlyList<string> Choices,
    IReadOnlyList<DecisionField> Fields,
    bool AnswerOnTap,
    bool TerminalOnly,
    bool TakesTypedAnswer)
{
    // Every question with fields is answered through its slots (Set-CopilotMqttDecision).
    public bool UsesFieldSlots => Fields.Count >= 1 && Fields.Count <= HaContract.MaxFields;
}

// One step of arming or clearing, in the order the daemon takes them.
public abstract record DecisionStep;
public sealed record PublishStep(string Topic, string Payload, bool Retain) : DecisionStep;
// select.select_option on an optimistic select, once Home Assistant offers the option.
public sealed record SelectStep(string EntityId, string Option) : DecisionStep;
// The field slots just republished, put back onto their pinned ids before they are driven.
public sealed record PinFieldsStep(int Slots) : DecisionStep;

public static partial class HaDecision
{
    public const string Idle = "Idle";
    public const string Awaiting = "Awaiting answer...";
    public const string Choose = "Choose...";
    public const string Cancel = "Cancel request";
    public const int QuestionMaxChars = 6000;
    public const string TruncatedNote = "\n\n_(truncated - see terminal)_";
    // The reply box's blank sentinel (ReplyBlankValue): a space, because Home Assistant will not
    // set a text entity to nothing.
    public const string ReplyBlank = " ";

    static readonly string[] Reserved = [Idle, Awaiting, Choose, Cancel, "unknown", "unavailable"];

    // Get-CopilotMixedFormHint.
    public static string MixedFormHint(string label) =>
        $"*Type **{label}** in the Reply box, choose the rest above, then press **Send** " +
        "beside the box - only it carries what you typed. Nothing to add? Press **Send answer**.*";

    // What the Copilot hook appends to a question the card cannot drive.
    public static string TerminalNote(string why) => $"\n\n**This one has to be answered in the terminal** - {why}";

    const string TooManyFields = "it has more fields than the dashboard can drive, so a reply typed here would not reach the prompt.";

    // The host's question as the card should show it.
    public static DecisionPlan Plan(Question question, AgentKind agent)
    {
        if (question.AnswerAtMachine == true || question.Truncated == true)
        {
            return Terminal(question, question.Truncated == true
                ? "it was too long to show here in full, so it is answered where it can be read."
                : "this prompt can only be answered at the machine.");
        }
        if (question.Kind == QuestionKind.Approval)
        {
            // A Codex approval: its own tap is the answer (Publish-DaemonSessionDecision).
            return new DecisionPlan(question.Id, question.Message, ["Approve", "Deny"], [], AnswerOnTap: true, TerminalOnly: false, TakesTypedAnswer: false);
        }
        if (question.Fields.Count == 0)
        {
            return new DecisionPlan(question.Id, question.Message, [], [], false, false, false);
        }
        List<DecisionField> fields;
        try
        {
            fields = [.. question.Fields.Select(f => Capture(f, agent))];
            Validate(fields);
        }
        catch (InvalidDataException) { return Terminal(question, TooManyFields); }
        if (!Answerable(fields)) { return Terminal(question, TooManyFields); }

        var message = question.Message;
        // As the Copilot hook does: a lone field's options also ride as the selector's choices,
        // and a form mixing dropdowns with a free-text field says which control takes the words.
        IReadOnlyList<string> choices = fields.Count == 1 ? fields[0].Options : [];
        if (fields.Count > 1 && fields.FirstOrDefault(f => f.IsText) is { } text) { message += "\n\n" + MixedFormHint(text.Label); }
        // Test-DecisionTakesTypedAnswer, and only where the host can deliver words: a lone choice
        // whose prompt offers "Other".
        var typed = TakesTypedAnswer(fields, choices, false)
            && fields is [{ Source: { Type: FieldType.Choice, AllowOther: true } }];
        return new DecisionPlan(question.Id, message, choices, fields, false, false, typed);
    }

    static DecisionPlan Terminal(Question question, string why) =>
        new(question.Id, question.Message + TerminalNote(why), [], [], false, TerminalOnly: true, TakesTypedAnswer: false);

    // Get-DecisionSchemaFields, from the host's field.
    public static DecisionField Capture(Field field, AgentKind agent)
    {
        var label = string.IsNullOrWhiteSpace(field.Title) ? field.Name : field.Title!;
        IReadOnlyList<string> options = field.Type switch
        {
            FieldType.Choice or FieldType.Multi => [.. (field.Options ?? []).Select(o => o.Label ?? o.Value)],
            // A checkbox is shown as Yes/No but recorded as the JSON literal.
            FieldType.Boolean => ["Yes", "No"],
            _ => [],
        };
        var multi = field.Type == FieldType.Multi && options.Count > 0;
        var defaults = new List<int>();
        if (multi && field.Default is { ValueKind: JsonValueKind.Array } d)
        {
            var values = field.Options!.Select(o => o.Value).ToList();
            foreach (var entry in d.EnumerateArray())
            {
                var key = entry.ValueKind == JsonValueKind.String ? entry.GetString()! : entry.GetRawText();
                var hits = values.Select((v, i) => (v, i)).Where(x => x.v == key).Select(x => x.i).ToList();
                if (hits.Count != 1 || defaults.Contains(hits[0])) { throw new InvalidDataException("Each multi-select decision default must identify exactly one distinct option."); }
                defaults.Add(hits[0]);
            }
        }
        // Whether nothing at all is an answer: the daemon reads it off the schema's minItems,
        // absent meaning yes, and only for Copilot (Claude's prompt has never been seen to take
        // one). The host's field does not carry minItems, so a Copilot list is taken to allow it.
        var allowsEmpty = multi && agent == AgentKind.Copilot;
        return new DecisionField(label, options, options.Count == 0, multi, defaults, allowsEmpty, field);
    }

    // The checks Set-CopilotMqttDecision makes before publishing anything.
    public static void Validate(IReadOnlyList<DecisionField> fields)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!labels.Add(field.Label)) { throw new InvalidDataException("Decision field labels are ambiguous."); }
            if (field.IsText) { continue; }
            CheckOptions(field.MultiSelect ? MultiSelect.Choices(field) : field.Options);
        }
    }

    // Get-DecisionSchemaFieldChoices' label rule: distinct, not reserved, and short enough that
    // the select never truncates one into another.
    public static void CheckOptions(IEnumerable<string> labels)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length > 250 || label != label.Trim() || ControlCharacter().IsMatch(label)
                || Reserved.Contains(label, StringComparer.OrdinalIgnoreCase) || !seen.Add(label))
            {
                throw new InvalidDataException("Decision option labels must be distinct, non-reserved, and fit the selector without truncation.");
            }
        }
    }

    [GeneratedRegex("[\\x00-\\x1f\\x7f]")]
    private static partial Regex ControlCharacter();

    // Test-DecisionFieldsAnswerable.
    public static bool Answerable(IReadOnlyList<DecisionField> fields)
    {
        if (fields.Count == 0 || fields.Count > HaContract.MaxFields) { return false; }
        if (fields.Count(f => f.IsText) > 1) { return false; }
        return fields.All(f => !f.MultiSelect || MultiSelect.Choices(f).Count > 0);
    }

    // Test-DecisionTakesTypedAnswer.
    public static bool TakesTypedAnswer(IReadOnlyList<DecisionField> fields, IReadOnlyList<string> choices, bool answerOnTap)
    {
        if (answerOnTap) { return false; }
        if (fields.Count > 1) { return false; }
        if (fields.Count == 1) { return !fields[0].IsText && !fields[0].MultiSelect; }
        return choices.Any(c => !string.IsNullOrWhiteSpace(c));
    }

    // Set-CopilotMqttDecision. armedSlots is how many field slots the last question left armed,
    // or -1 when that is not known (every slot is then swept).
    public static IReadOnlyList<DecisionStep> Arm(DecisionPlan plan, string node, string sessionName, string machineName, int armedSlots, DateTimeOffset askedAt)
    {
        var t = HaContract.Topics(node);
        var steps = new List<DecisionStep>();
        string[] options = plan.UsesFieldSlots ? [Awaiting, Cancel]
            : plan.Choices.Count > 0 ? [Awaiting, .. plan.Choices, Cancel]
            : [Awaiting];
        var (topic, config) = HaContract.DecisionConfig(t, sessionName, machineName, options, enabledByDefault: false);
        steps.Add(new PublishStep(topic, HaContract.Serialize(config), true));
        steps.Add(new SelectStep(SessionEntities.Decision(node), Awaiting));
        steps.AddRange(Fields(plan.UsesFieldSlots ? plan.Fields : [], node, sessionName, machineName, armedSlots));

        var question = plan.Question.Length > QuestionMaxChars ? plan.Question[..QuestionMaxChars].TrimEnd() + TruncatedNote : plan.Question;
        var attributes = new JsonObject
        {
            ["decision_id"] = plan.QuestionId,
            ["question"] = question,
            ["choices"] = new JsonArray([.. plan.Choices.Select(c => (JsonNode)c)]),
            ["mode"] = plan.Choices.Count > 0 || plan.Fields.Count > 0 ? "multiple_choice" : "freeform",
            ["multi_field"] = plan.UsesFieldSlots,
            ["field_count"] = plan.UsesFieldSlots ? plan.Fields.Count : 0,
            ["session"] = sessionName,
            ["machine"] = machineName,
            ["asked_at"] = askedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (plan.AnswerOnTap) { attributes["answer_on_tap"] = true; }
        if (plan.TakesTypedAnswer) { attributes["takes_typed_answer"] = true; }
        if (plan.UsesFieldSlots)
        {
            for (var i = 1; i <= plan.Fields.Count; i++)
            {
                var field = plan.Fields[i - 1];
                attributes[$"field_{i}_label"] = string.IsNullOrWhiteSpace(field.Label) ? $"Field {i}" : field.Label;
                if (!field.MultiSelect) { continue; }
                attributes[$"field_{i}_multi"] = true;
                attributes[$"field_{i}_options"] = new JsonArray([.. field.Options.Select(o => (JsonNode)o)]);
                attributes[$"field_{i}_separator"] = MultiSelect.Separator;
                if (MultiSelect.Codes(field).Count > 0) { attributes[$"field_{i}_codes"] = true; }
                if (MultiSelect.EmptyChoice(field) is { Length: > 0 } empty) { attributes[$"field_{i}_empty"] = empty; }
            }
        }
        steps.Add(new PublishStep(t.DecisionAttributes, HaContract.Serialize(attributes), true));
        return steps;
    }

    // Clear-CopilotMqttDecision.
    public static IReadOnlyList<DecisionStep> Clear(string node, string sessionName, string machineName, int armedSlots)
    {
        var t = HaContract.Topics(node);
        var (topic, config) = HaContract.DecisionConfig(t, sessionName, machineName, [Idle], enabledByDefault: false);
        var steps = new List<DecisionStep>
        {
            new PublishStep(topic, HaContract.Serialize(config), true),
            new PublishStep(t.DecisionAttributes, "{}", true),
            new SelectStep(SessionEntities.Decision(node), Idle),
        };
        steps.AddRange(Fields([], node, sessionName, machineName, armedSlots));
        return steps;
    }

    // Publish-CopilotMqttDecisionFields: only the slots this question arms, plus any the last
    // one left armed; every slot when that is unknown.
    static IEnumerable<DecisionStep> Fields(IReadOnlyList<DecisionField> fields, string node, string sessionName, string machineName, int armedSlots)
    {
        var t = HaContract.Topics(node);
        var previous = armedSlots >= 0 ? Math.Min(armedSlots, HaContract.MaxFields) : -1;
        var slots = previous < 0 ? HaContract.MaxFields : Math.Max(fields.Count, previous);
        for (var i = 1; i <= slots; i++)
        {
            var field = i <= fields.Count ? fields[i - 1] : null;
            IEnumerable<string> options = [Idle];
            var label = $"Field {i}";
            if (field is { IsText: false })
            {
                label = string.IsNullOrWhiteSpace(field.Label) ? $"Field {i}" : field.Label;
                options = [Choose, .. field.MultiSelect ? MultiSelect.Choices(field) : field.Options];
            }
            var (topic, config) = HaContract.FieldConfig(t, sessionName, machineName, i, label, options);
            yield return new PublishStep(topic, HaContract.Serialize(config), true);
        }
        if (slots > 0) { yield return new PinFieldsStep(slots); }
        for (var i = 1; i <= slots; i++)
        {
            var field = i <= fields.Count ? fields[i - 1] : null;
            yield return new SelectStep(SessionEntities.Field(node, i), field is { IsText: false } ? MultiSelect.Start(field) : Idle);
        }
    }
}

// How a multi-select field rides on a Home Assistant select, which holds one value: every subset
// is an entry, written as words where they fit and as positions ('#1,3') where they do not, and
// "nothing ticked" has an entry of its own where the field allows it.
public static partial class MultiSelect
{
    public const string Separator = " + ";
    public const string EmptyLabel = "(none)";
    public const string EmptyCode = "#0";
    const int MaxChoices = 63;
    const int MaxCodes = 1023;
    const int MaxOptions = 10;

    [GeneratedRegex("^#[1-9][0-9]*(,[1-9][0-9]*)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    // Get-DecisionMultiSelectMasks: every non-empty subset, single picks first.
    static IEnumerable<int> Masks(int count)
    {
        for (var size = 1; size <= count; size++)
        {
            for (var mask = 1; mask < 1 << count; mask++)
            {
                if (System.Numerics.BitOperations.PopCount((uint)mask) == size) { yield return mask; }
            }
        }
    }

    static int[] Bits(int mask, int count) => [.. Enumerable.Range(0, count).Where(b => (mask & (1 << b)) != 0)];

    // Get-DecisionMultiSelectCode.
    public static string Code(IEnumerable<int> indexes)
    {
        var ordered = indexes.Distinct().Order().ToList();
        return ordered.Count == 0 ? "" : "#" + string.Join(",", ordered.Select(i => i + 1));
    }

    // Test-DecisionMultiSelectCodeSafe.
    public static bool CodeSafe(DecisionField field) => !field.Options.Any(o => CodePattern().IsMatch(o));

    // Get-DecisionMultiSelectSpelledChoices.
    public static IReadOnlyList<string> Spelled(DecisionField field)
    {
        var count = field.Options.Count;
        if (count < 2 || count > 20 || Math.Pow(2, count) - 1 > MaxChoices) { return []; }
        var labels = Masks(count).Select(m => string.Join(Separator, Bits(m, count).Select(b => field.Options[b]))).ToList();
        if (labels.Any(l => l.Length > 200)) { return []; }
        return labels.Distinct(StringComparer.Ordinal).Count() != labels.Count ? [] : labels;
    }

    // Get-DecisionMultiSelectCodes.
    public static IReadOnlyList<string> Codes(DecisionField field)
    {
        var count = field.Options.Count;
        if (count < 2 || count > MaxOptions || Math.Pow(2, count) - 1 > MaxCodes || !CodeSafe(field)) { return []; }
        return [.. Masks(count).Select(m => Code(Bits(m, count)))];
    }

    // Get-DecisionMultiSelectEmptyCode.
    public static string EmptyCodeFor(DecisionField field)
    {
        var code = EmptyCode;
        while (field.Options.Contains(code, StringComparer.Ordinal)) { code += "0"; }
        return code;
    }

    // Get-DecisionMultiSelectEmptyChoices.
    public static IReadOnlyList<string> EmptyChoices(DecisionField field)
    {
        if (!field.AllowsEmpty) { return []; }
        if (Spelled(field).Count == 0 && Codes(field).Count == 0) { return []; }
        var carriers = new List<string>();
        if (!field.Options.Contains(EmptyLabel, StringComparer.Ordinal)) { carriers.Add(EmptyLabel); }
        carriers.Add(EmptyCodeFor(field));
        return carriers;
    }

    // Get-DecisionMultiSelectEmptyChoice: the one carrier a card should write for "none".
    public static string EmptyChoice(DecisionField field) => EmptyChoices(field).Count == 0 ? "" : EmptyCodeFor(field);

    // Test-DecisionMultiSelectEmptyChoice.
    public static bool IsEmptyChoice(DecisionField field, string? choice) =>
        !string.IsNullOrEmpty(choice) && EmptyChoices(field).Contains(choice, StringComparer.Ordinal);

    // Get-DecisionMultiSelectChoices: words, then positions, then the empty set.
    public static IReadOnlyList<string> Choices(DecisionField field) => [.. Spelled(field), .. Codes(field), .. EmptyChoices(field)];

    // Get-DecisionMultiSelectLabel.
    public static string Label(DecisionField field, IEnumerable<int> indexes)
    {
        var ordered = indexes.Distinct().Order().ToList();
        if (ordered.Count == 0 || ordered.Any(i => i < 0 || i >= field.Options.Count)) { return ""; }
        return string.Join(Separator, ordered.Select(i => field.Options[i]));
    }

    // Resolve-DecisionMultiSelectIndexes: the positions behind a slot's value, or none when it
    // is not one of this field's answers.
    public static IReadOnlyList<int> Resolve(DecisionField field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return []; }
        var count = field.Options.Count;
        if (count < 2) { return []; }
        if (CodeSafe(field) && CodePattern().IsMatch(value))
        {
            var seen = new SortedSet<int>();
            foreach (var part in value[1..].Split(','))
            {
                if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position)) { return []; }
                var index = position - 1;
                if (index < 0 || index >= count || !seen.Add(index)) { return []; }
            }
            return seen.Count == 0 ? [] : [.. seen];
        }
        if (Spelled(field).Count == 0) { return []; }
        int[]? found = null;
        foreach (var mask in Masks(count))
        {
            var bits = Bits(mask, count);
            if (!string.Equals(string.Join(Separator, bits.Select(b => field.Options[b])), value, StringComparison.Ordinal)) { continue; }
            if (found is not null) { return []; }
            found = bits;
        }
        return found ?? [];
    }

    // Where a multi-select slot starts (Publish-CopilotMqttDecisionFields): on what the prompt
    // already checks, else on "none" where that is an answer, else on the placeholder.
    public static string Start(DecisionField field)
    {
        if (!field.MultiSelect) { return HaDecision.Choose; }
        var offered = Choices(field);
        var checkedIndexes = field.DefaultIndexes.Distinct().Order().ToList();
        if (checkedIndexes.Count > 0)
        {
            var candidates = new List<string>();
            if (CodeSafe(field)) { candidates.Add(Code(checkedIndexes)); }
            candidates.Add(Label(field, checkedIndexes));
            return candidates.FirstOrDefault(c => c.Length > 0 && offered.Contains(c, StringComparer.Ordinal)) ?? HaDecision.Choose;
        }
        return EmptyChoice(field) is { Length: > 0 } empty ? empty : HaDecision.Choose;
    }
}
