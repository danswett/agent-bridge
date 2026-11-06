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
    // A sub-agent's question (Question.Agent): its id and name, published beside the question,
    // and the line that says it asks, after the word Delegate (#263), for titles.
    public string? Agent { get; init; }
    public string? AgentName { get; init; }
    public string? Asker { get; init; }
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

    // A session's question as its card and the phones show it. A sub-agent's (subagents.md §7)
    // goes the same way as the session's own, said to be the delegate's: its name leads the text,
    // so the card shows it without knowing about delegates, and agent and agent_name are published
    // beside it. The name is added before the plan's length checks, so it can never be what makes
    // an answerable question one the card would have to cut.
    public static DecisionPlan Plan(Question question, Session session)
    {
        if (question.Agent is not { } agent) { return Plan(question, session.Agent); }
        // The name alone, "" when there is none: "A delegate" is how a line says who asks, not a name.
        var name = DelegateName.Clean(Name(question, session)) is { } clean ? HaPayloads.NeutraliseTemplates(clean) : "";
        var asker = Asker(question, session);
        // What the approval says is judged before who asks leads it, and with the name as the
        // session reported it (Misleads): neutralising template braces adds a zero-width space,
        // which is the card's own. Who asks leads the card's Markdown as literal text, so it cannot
        // open a fence or a link around the prompt.
        var asks = $"{HaPayloads.NeutraliseTemplates(MarkdownLiteral(Titled(question, session)))} asks:\n\n";
        var held = Misleads(question, session) ? Misleading : HeldByHost(question) ?? Unshaped(question, session.Agent) ?? TooLongToShow(question, asks.Length);
        return Plan(question with { Message = asks + Shown(question, held, asks.Length) }, session.Agent, held) with { Agent = agent, AgentName = name, Asker = asker };
    }

    static string? Name(Question question, Session session) => session.Subagents?.FirstOrDefault(a => a.Id == question.Agent)?.Name;

    // Who asked a sub-agent's question, as a line of plain text: "Delegate" and its name from the
    // session's list, else "A delegate" (one the list no longer carries). Cleaned again, as in the
    // subagents attribute, and always after the word Delegate (#263): it leads the question and
    // titles a push, and a look-alike no check catches must still not read "Main agent asks".
    public static string Asker(Question question, Session session) => HaPayloads.NeutraliseTemplates(Titled(question, session));

    // Cleaning drops hidden characters but keeps right-to-left letters and a character reference
    // (&#x202E;), which a phone drawing a push as HTML decodes into the control it names; a name that
    // still holds either is "A delegate" (#220).
    static string Titled(Question question, Session session) =>
        DelegateName.Clean(Name(question, session)) is { } clean && !ShownText.Misleads(clean) ? DelegateName.Titled(clean) : "A delegate";

    // Who asks, with what Markdown would act on backslash-escaped, which CommonMark shows as itself:
    // no fence, code span, emphasis, link, tag or reference, so it cannot open anything around the
    // prompt below it. It always starts with "Delegate" or "A delegate", so nothing in it can make a
    // heading or a list. Plain hyphens, colons and dots are left as they are, since a question's
    // push shows the card's text as plain text.
    static string MarkdownLiteral(string text) => MarkdownActive().Replace(text, @"\$0");

    [GeneratedRegex(@"[\\`*_\[\]<>&~|!#]")]
    private static partial Regex MarkdownActive();
    // An approval a sub-agent asked for is judged with the name that leads it.
    public static bool Misleads(Question question, Session session) =>
        Misleads(question) || (question.Kind == QuestionKind.Approval && question.Agent is not null && Name(question, session) is { } name && ShownText.Misleads(name));

    // The host's question as the card should show it.
    public static DecisionPlan Plan(Question question, AgentKind agent)
    {
        var held = Misleads(question) ? Misleading : HeldByHost(question) ?? Unshaped(question, agent) ?? TooLongToShow(question, 0);
        return Plan(question with { Message = Shown(question, held, 0) }, agent, held);
    }

    // An approval the host itself holds at the machine, or had to cut, is held here for the same
    // reason before it is fenced, so the room for the note saying so is kept and a cut falls inside the
    // block: fenced whole first, it was cut through its closing fence, and the notes were drawn as
    // part of the agent's code. Before its shape, as the app's card does: a cut approval usually ends
    // inside its fence, and the person is told that part of it is missing.
    static string? HeldByHost(Question question) =>
        question.Kind != QuestionKind.Approval ? null
        : question.Truncated == true ? TooLong
        : question.AnswerAtMachine == true ? AtMachine
        : null;

    // An approval allows what runs, not what the card draws: one holding a character drawn
    // somewhere other than where it runs, right-to-left letters that reorder it, or a character
    // reference a renderer would decode to either, is answered at the machine (#220). The host
    // sends hidden characters and right-to-left letters escaped, offering Approve once for them
    // (#253, decision 3), so from it this is a reference, which Markdown would decode. The card shows
    // every approval as its source, where none is decoded, and holds to its rule all the same.
    static bool Misleads(Question question) => question.Kind == QuestionKind.Approval && ShownText.Misleads(question.Message);

    // Drawn as Markdown, the agent's own words, before a host escapes them (#212), could close the
    // command's fence early or show a link's words where `curl ... | sh` runs. The card shows every
    // approval as its source (Shown), and still answers one Markdown would not draw as written
    // (ApprovalShape) in the terminal, as the app's card does.
    // A Codex approval is answered here only as a host sends it from #209, the whole command in one
    // block (ApprovalShape.IsFencedCommand): an older host sent "Needs approval: " and the hook's
    // 300-character preview, cut to 297 and "..." with nothing marking it truncated, so Approve would
    // run more than the card shows (#209 security review).
    static string? Unshaped(Question question, AgentKind agent) =>
        question.Kind != QuestionKind.Approval ? null
        : agent == AgentKind.Codex && !ApprovalShape.IsFencedCommand(question.Message) ? NotWhole
        : !ApprovalShape.DrawsAsWritten(ShownText.Reveal(question.Message, rightToLeft: true)) ? NotAsWritten : null;

    // An approval is never cut on the card (Approve would allow what was not shown): one whose source,
    // fenced, with its notes and who asks, does not fit is answered in the terminal, cut there.
    static string? TooLongToShow(Question question, int leading)
    {
        if (question.Kind != QuestionKind.Approval) { return null; }
        var text = ShownText.Reveal(question.Message, rightToLeft: true);
        var fence = FenceFor(text);
        return leading + Notes(question).Length + text.Length + 2 * (fence.Length + 1) > QuestionMaxChars ? TooLong : null;
    }

    // What an approval's escapes stand for, in words above it (ui.md §6.4): Approve once is offered
    // for it, and an escape can be read past while its character still acts (PowerShell ends a `#`
    // comment at a bare CR, shown as \u000D on the comment's line).
    static string Notes(Question question) =>
        question.Kind == QuestionKind.Approval && ShownText.Notes(question.Message) is { Count: > 0 } notes ? $"_{string.Join(" · ", notes)}_\n\n" : "";

    // And every approval is shown as its source, as the PWA's and the app's cards draw it, those
    // characters as visible escapes, in a code block whose fence nothing in it can close: nothing in a
    // block is decoded or drawn as Markdown. Drawn as Markdown, the agent's own words could still
    // reshape it however the shape check reads it: an intention ending in `~~~` opened a block that
    // swallowed the host's fence, so the command's backtick lines became markers, never drawn, and
    // bash ran them as command substitutions (#220). A held one is cut, if it must be, before it is
    // fenced, to leave room for what leads it (a delegate's name), the notes and the terminal note.
    static string Shown(Question question, string? held, int leading)
    {
        if (question.Kind != QuestionKind.Approval) { return question.Message; }
        var notes = Notes(question);
        if (held is null)
        {
            var whole = ShownText.Reveal(question.Message, rightToLeft: true);
            var f = FenceFor(whole);
            return $"{notes}{f}\n{whole}\n{f}";
        }
        var room = QuestionMaxChars - leading - notes.Length - TerminalNote(held).Length - TruncatedNote.Length;
        var text = ShownText.Reveal(question.Message, rightToLeft: true);
        // One the host cut is already missing a part, whatever else holds it (a misleading
        // reference wins the terminal reason over TooLong), so it is said even when it fits.
        var cut = question.Truncated == true;
        while (true)
        {
            var fence = FenceFor(text);
            var block = $"{fence}\n{text}\n{fence}";
            // Nothing left to cut: the empty block, whatever room there is (a delegate's name
            // is kept to a length that always leaves it more).
            if (block.Length <= room || text.Length == 0) { return notes + (cut ? block + TruncatedNote : block); }
            text = text[..Math.Max(0, text.Length - (block.Length - room))];
            cut = true;
        }
    }

    // Backticks or tildes, whichever needs the shorter fence: one longer than any run of it that
    // starts a line, which is all that could close it.
    static string FenceFor(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimStart(' ')).ToList();
        int Longest(char c) => lines.Select(l => l.Length - l.TrimStart(c).Length).DefaultIfEmpty(0).Max();
        var (ticks, tildes) = (Longest('`'), Longest('~'));
        return ticks <= tildes ? new string('`', Math.Max(3, ticks + 1)) : new string('~', Math.Max(3, tildes + 1));
    }

    static DecisionPlan Plan(Question question, AgentKind agent, string? held)
    {
        if (held is not null) { return Terminal(question, held); }
        if (question.AnswerAtMachine == true || question.Truncated == true)
        {
            return Terminal(question, question.Truncated == true ? TooLong : AtMachine);
        }
        // A question the card would have to cut is never answered from it: an approval, above all,
        // must not authorise what nobody could read. (agent-ha-bridge cuts it and takes the answer.)
        if (question.Message.Length > QuestionMaxChars) { return Terminal(question, TooLong); }
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
        if (message.Length > QuestionMaxChars) { return Terminal(question, TooLong); }
        // Test-DecisionTakesTypedAnswer, and only where the host can deliver words: a lone choice
        // whose prompt offers "Other".
        var typed = TakesTypedAnswer(fields, choices, false)
            && fields is [{ Source: { Type: FieldType.Choice, AllowOther: true } }];
        return new DecisionPlan(question.Id, message, choices, fields, false, false, typed);
    }

    const string TooLong = "it was too long to show here in full, so it is answered where it can be read.";
    const string AtMachine = "this prompt can only be answered at the machine.";
    const string Misleading = "it holds characters that can be drawn other than as they run, so it is answered where it runs.";
    const string NotAsWritten = "it would not be drawn here as it is written, so it is answered where it runs.";
    const string NotWhole = "its command did not come whole in a block, so it may be cut short, and it is answered where it runs.";

    // What a push says in place of such an approval.
    public const string MisleadingPush = "An approval is waiting at the machine. It holds characters that can be drawn other than as they run, so it is not shown here; answer it in the terminal.";

    // Shown with nothing to answer, and the note saying why kept inside the card's limit: a long
    // question is cut before the note, never the note itself.
    static DecisionPlan Terminal(Question question, string why)
    {
        var note = TerminalNote(why);
        var body = question.Message;
        if (body.Length + note.Length > QuestionMaxChars)
        {
            body = body[..(QuestionMaxChars - note.Length - TruncatedNote.Length)].TrimEnd() + TruncatedNote;
        }
        return new(question.Id, body + note, [], [], false, TerminalOnly: true, TakesTypedAnswer: false);
    }

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
        if (plan.Agent is { } agent)
        {
            attributes["agent"] = agent;
            attributes["agent_name"] = plan.AgentName ?? "";
        }
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
