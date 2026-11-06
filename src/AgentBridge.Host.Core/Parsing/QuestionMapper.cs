using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// Maps each agent's way of asking onto the contract's Question and Field.
internal static class QuestionMapper
{
    // Approve is once: this request, never a standing grant (Codex's y, Copilot's plain Yes).
    public static readonly IReadOnlyList<Option> ApprovalOptions = [new("approve", "Approve once"), new("deny", "Deny")];

    // The contract's message limit. A question whose message had to be cut says so, so a
    // client shows it read-only and sends the person to the machine for the rest.
    static string Cut(string message) => Limits.KeepStart(message, Limits.QuestionMessage)!;

    static bool? IsCut(string message) => Cut(message).Length < message.Length ? true : null;
    // A permission prompt: a fixed approve-or-deny choice. atMachine marks one no host can
    // answer from another device, so a client shows it without the buttons. Whatever agent
    // asked, a character not drawn where it runs, and a right-to-left letter, is shown as a
    // visible escape, and Approve once is still offered (#220; the owner's #253 decision 3): what
    // a reader is shown is then what runs. A client that cannot draw it exactly as written holds
    // it at the machine itself.
    //
    // An approval the host escaped anything in carries Escaped (#803): where its escapes are, the
    // digest of the message as sent, and approve, the machine's permissions.escapedApproval, which
    // says whether Approve may come as a confirmation from an approved device or only at the machine.
    // marks: given when message is already shown so (CopilotPermissionPrompt), and then not escaped
    // again.
    public static Question Approval(string id, string message, DateTimeOffset askedAt, bool atMachine = false,
        string approve = EscapedApproval.Confirm, IReadOnlyList<(int Start, int Length)>? marks = null)
    {
        // Escaped from no more than it can carry (RevealWithin): still cut, and said to be, if it is over.
        var (shown, escapes) = marks is null ? ShownText.RevealWithinMarked(message, Limits.QuestionMessage, rightToLeft: true) : (message, marks);
        var cut = Cut(shown);
        // More escapes than a question describes is a message not described exactly: Approve is the
        // machine's (TooMany), never confirmed. Not truncated, which would refuse Deny too: the whole
        // message came, and denying it is safe from anywhere (#836 review).
        var many = escapes.Count > EscapedApproval.MaxMarks;
        return new()
        {
            Id = id,
            Kind = QuestionKind.Approval,
            Message = cut,
            Truncated = IsCut(shown),
            Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = ApprovalOptions, Required = true }],
            AskedAt = askedAt,
            AnswerAtMachine = atMachine ? true : null,
            Escaped = escapes.Count == 0 ? null : EscapedApproval.For(cut, escapes.Take(EscapedApproval.MaxMarks), many ? EscapedApproval.TooMany : approve),
        };
    }

    // A Codex approval from its hook's marker (#209): the whole command, literal, in a fence longer
    // than any run of backticks inside it, under the tool that asked. A marker that does not carry
    // the whole command (agent-ha-bridge's hook and older hosts wrote only a 300-character preview,
    // and a command past the hook's CommandMax is kept to its start) is truncated, so no client
    // approves what it cannot show; one with no command at all (a tool that runs none) shows the
    // tool and is answered at the machine.
    //
    // Its id is the marker's DecisionId and a digest of what is shown (CodexApprovalId, #958),
    // worked out from the content alone; atMachine (agent-ha-bridge already typed into it) is
    // laid on after, so it holds the approval without changing which one it is. approve is the
    // machine's permissions.escapedApproval, carried in Escaped for one the host escaped
    // anything in (#803); a confirmation names this id, so it too approves only what was shown.
    public static Question CodexApproval(string decisionId, string? tool, string? command, bool cut, string preview, DateTimeOffset askedAt, bool atMachine,
        string approve = EscapedApproval.Confirm)
    {
        var shown = CodexApprovalShown(tool, command, cut, preview, askedAt, approve);
        var q = shown with { Id = CodexApprovalId.For(decisionId, tool, command, cut, preview, shown) };
        return atMachine ? q with { AnswerAtMachine = true } : q;
    }

    static Question CodexApprovalShown(string? tool, string? command, bool cut, string preview, DateTimeOffset askedAt, string approve)
    {
        const string id = "";
        var who = $"Allow **{CopilotPermission.Plain(string.IsNullOrWhiteSpace(tool) ? "Codex" : tool)}**?";
        if (command is not { Length: > 0 })
        {
            var legacy = preview.StartsWith("Needs approval: ", StringComparison.Ordinal) ? preview["Needs approval: ".Length..] : preview;
            var held = Approval(id, legacy.Length > 0 ? $"{who}\n\n{CopilotPermission.Fence(legacy)}" : who, askedAt, approve: approve);
            return tool is null && command is null ? held with { Truncated = true } : held with { AnswerAtMachine = true };
        }
        var q = Approval(id, $"{who}\n\n{CopilotPermission.Fence(command)}", askedAt, approve: approve);
        if (cut) { q = q with { Truncated = true }; }
        // Whatever the command, what is sent is that shape, which every client checks before it
        // offers Approve; one that is not would be answered nowhere else, so it is held here. One
        // already truncated is refused for that, and is left so: cut past the message's limit, it
        // has lost its closing fence, and held here too, a client said only that it is answered at
        // the machine, not that part of it is missing.
        if (q.Truncated != true && !ApprovalShape.IsFencedCommand(q.Message)) { q = q with { AnswerAtMachine = true }; }
        return q;
    }

    // Copilot's ask_user. Current builds pass `message` and `requestedSchema` (a JSON
    // Schema form); older ones passed `question` and a `choices` list, sometimes as a
    // JSON string. Without reading both, a multiple choice became a free-text box titled
    // "needs your input".
    public static Question FromCopilotAskUser(string id, JsonElement arguments, DateTimeOffset askedAt)
    {
        var message = Json.Str(arguments, "message") ?? Json.Str(arguments, "question") ?? "";
        IReadOnlyList<Field> fields = [];
        var atMachine = false;
        var choices = ChoiceList(Json.Get(arguments, "choices"));
        if (choices.Count > 0)
        {
            fields =
            [
                new Field
                {
                    Name = "answer",
                    Type = FieldType.Choice,
                    Options = [.. choices.Select(c => new Option(c))],
                    // The old tool took free text unless told otherwise.
                    AllowOther = Json.Bool(arguments, "allow_freeform") ?? true,
                    Required = true,
                },
            ];
        }
        else if (Json.Get(arguments, "requestedSchema") is { } schema)
        {
            (fields, var drivable) = SchemaForm(schema);
            // Every Copilot option list ends in "Other (type your answer)", but words reach
            // it only in a question that is that one list: the Down past its last option
            // opens Other there, and in a tabbed form or a checkbox list it does not.
            // Elsewhere the person is told Other is typed at the machine, so a missing Other
            // is not taken for one the agent never offered (#146).
            fields = fields is [{ Type: FieldType.Choice } only]
                ? [only with { AllowOther = true }]
                : [.. fields.Select(f => f.Type is FieldType.Choice or FieldType.Multi && f.Options is { Count: > 0 } ? f with { OtherAtMachine = true } : f)];
            // A field the CLI draws as something other than its schema says, or that cannot be
            // told, is never typed into, so a client shows the question to be answered at the
            // machine rather than offer what the host would then refuse (#662).
            atMachine = !drivable;
        }
        return new Question
        {
            Id = id,
            Kind = QuestionKind.Form,
            Message = Cut(message),
            Truncated = IsCut(message),
            Fields = fields,
            AskedAt = askedAt,
            AnswerAtMachine = atMachine ? true : null,
        };
    }

    static List<string> ChoiceList(JsonElement? value)
    {
        if (value is not { } v) { return []; }
        if (v.ValueKind == JsonValueKind.String)
        {
            var text = v.GetString() ?? "";
            try
            {
                using var doc = JsonDocument.Parse(text);
                return ChoiceList(doc.RootElement);
            }
            catch (JsonException) { return string.IsNullOrWhiteSpace(text) ? [] : [text]; }
        }
        if (v.ValueKind != JsonValueKind.Array) { return []; }
        return [.. v.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.GetRawText()).Where(c => !string.IsNullOrWhiteSpace(c))];
    }

    // One Field per schema property, in order. A schema that arrived as text (the model
    // wrote it as a string) is parsed first.
    public static IReadOnlyList<Field> SchemaFields(JsonElement schema) => SchemaForm(schema).Fields;

    // The fields, and whether every one reads as one input Copilot CLI is known to draw for it
    // with constraints the host can check. Each property is read once, and its patterns share one
    // budget for the whole ask_user: read twice with 250 ms a match, 200 slow patterns stalled the
    // refresh of every session for minutes (#844 review).
    static (IReadOnlyList<Field> Fields, bool Drivable) SchemaForm(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(schema.GetString() ?? "");
                return SchemaForm(doc.RootElement);
            }
            catch (JsonException) { return ([], true); }
        }
        var whole = CopilotFieldConstraints.FormUnchecked(schema) is null;
        if (Json.Obj(schema, "properties") is not { } properties) { return ([], whole); }
        var required = Json.Arr(schema, "required") is { } r
            ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var patterns = new PatternBudget();
        var fields = new List<Field>();
        var drivable = whole;
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) { continue; }
            var read = CopilotFieldSchema.Read(property.Value);
            var (limits, uncheckable) = CopilotFieldConstraints.Read(property.Value, patterns);
            var field = SchemaField(property.Name, property.Value, required.Contains(property.Name), read, limits);
            drivable &= read.Problem is null && uncheckable is null && !BlankRequiredDefault(field);
            fields.Add(field);
        }
        return (fields, drivable);
    }

    // A required text box that opens with only spaces in it has no answer from another device: its
    // default is the one answer it can be given there (#662), and Answers.Check refuses that as
    // blank (FieldConstraints.BlankOnceTyped, control characters included), since it might be
    // recorded trimmed to nothing. Offered anyway, every answer to the form was refused (review of
    // #844). An empty default opens nothing, so it stays answerable.
    static bool BlankRequiredDefault(Field field) =>
        field is { Required: true, Type: FieldType.Text, Default: { ValueKind: JsonValueKind.String } d }
        && d.GetString()!.Length > 0 && FieldConstraints.BlankOnceTyped(d.GetString()!);
    // Each field as the input Copilot CLI draws for it (CopilotFieldSchema), which is not
    // always what its keywords suggest: a number with an `enum` is still a number box, and a
    // string with only `anyOf` a text box.
    static Field SchemaField(string name, JsonElement p, bool required, (CopilotFieldSchema.Input Input, IReadOnlyList<CopilotFieldSchema.Choice> Choices, string? Problem) read, FieldConstraints? limits)
    {
        var (input, choices, _) = read;
        var kind = input switch
        {
            CopilotFieldSchema.Input.Multi => FieldType.Multi,
            CopilotFieldSchema.Input.Choice => FieldType.Choice,
            CopilotFieldSchema.Input.Boolean => FieldType.Boolean,
            CopilotFieldSchema.Input.Number => FieldType.Number,
            _ => FieldType.Text,
        };
        IReadOnlyList<Option>? options = kind is FieldType.Multi or FieldType.Choice && choices.Count > 0
            ? [.. choices.Select(c => new Option(Scalar(c.Value), c.Label is { } label && label != Scalar(c.Value) ? label : null))]
            : null;
        return new Field
        {
            Name = name,
            Title = Json.Str(p, "title"),
            Description = Json.Str(p, "description"),
            Type = kind,
            Options = options,
            Default = Json.Get(p, "default") is { } d ? Json.Detach(d) : null,
            Required = required ? true : null,
            Constraints = limits,
        };
    }

    static string Scalar(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();

    // Claude's AskUserQuestion: { questions: [ { question, header, multiSelect, options:
    // [ { label, description } ] } ] }. Claude records the answer under the question text.
    // Option descriptions carry real content but have no place on an Option, so they are
    // listed under the message, as the old bridge did.
    //
    // Claude's prompt also takes free text ("Type something"), but no field offers it:
    // agent-ha-bridge's only walk to it overshoots the list by two rows and was never run
    // against a real question, and a wrong walk answers in the person's name. A prompt
    // the keys cannot drive at all (no questions, more than the tool's four, or an option
    // past the ninth that a multi-select can only tick by its digit) is answered at the
    // machine, as agent-ha-bridge leaves it to the terminal.
    public static Question FromClaudeAskUserQuestion(string id, JsonElement input, DateTimeOffset askedAt)
    {
        var fields = new List<Field>();
        var questions = new List<string>();
        var notes = new StringBuilder();
        var drivable = true;
        if (Json.Arr(input, "questions") is { } list)
        {
            foreach (var q in list.EnumerateArray())
            {
                var text = Json.Str(q, "question");
                if (string.IsNullOrWhiteSpace(text)) { continue; }
                questions.Add(text);
                var options = new List<Option>();
                if (Json.Arr(q, "options") is { } opts)
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        var label = o.ValueKind == JsonValueKind.String ? o.GetString() : Json.Str(o, "label");
                        if (string.IsNullOrWhiteSpace(label)) { continue; }
                        options.Add(new Option(label));
                        if (Json.Str(o, "description") is { Length: > 0 } description)
                        {
                            notes.Append("\n- **").Append(label).Append("**: ").Append(description);
                        }
                    }
                }
                var multi = Json.Bool(q, "multiSelect") == true;
                if (options.Count == 0 || (multi && options.Count > 9) || fields.Any(f => f.Name == text)) { drivable = false; }
                fields.Add(new Field
                {
                    Name = text,
                    Title = Json.Str(q, "header"),
                    Description = Json.Str(q, "header") is null ? null : text,
                    Type = multi ? FieldType.Multi : FieldType.Choice,
                    Options = options,
                    Required = true,
                });
            }
        }
        if (fields.Count is 0 or > 4) { drivable = false; }
        var message = questions.Count == 0 ? "Claude needs an answer." : string.Join("\n\n", questions);
        if (notes.Length > 0) { message += "\n" + notes; }
        return new Question
        {
            Id = id,
            Kind = QuestionKind.Form,
            Message = Cut(message),
            Truncated = IsCut(message),
            Fields = fields,
            AskedAt = askedAt,
            AnswerAtMachine = drivable ? null : true,
        };
    }
}
