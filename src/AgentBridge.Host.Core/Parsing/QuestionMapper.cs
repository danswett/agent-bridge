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
    public static Question Approval(string id, string message, DateTimeOffset askedAt, bool atMachine = false)
    {
        // Escaped from no more than it can carry (RevealWithin): still cut, and said to be, if it is over.
        var shown = ShownText.RevealWithin(message, Limits.QuestionMessage, rightToLeft: true);
        return new()
        {
            Id = id,
            Kind = QuestionKind.Approval,
            Message = Cut(shown),
            Truncated = IsCut(shown),
            Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = ApprovalOptions, Required = true }],
            AskedAt = askedAt,
            AnswerAtMachine = atMachine ? true : null,
        };
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
            fields = SchemaFields(schema);
            // Every Copilot option list ends in "Other (type your answer)", but words reach
            // it only in a question that is that one list: the Down past its last option
            // opens Other there, and in a tabbed form or a checkbox list it does not.
            if (fields is [{ Type: FieldType.Choice } only]) { fields = [only with { AllowOther = true }]; }
            // A field the CLI draws as something other than its schema says, or that cannot be
            // told, is never typed into, so a client shows the question to be answered at the
            // machine rather than offer what the host would then refuse (#662).
            atMachine = !Drivable(schema);
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
    public static IReadOnlyList<Field> SchemaFields(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(schema.GetString() ?? "");
                return SchemaFields(doc.RootElement);
            }
            catch (JsonException) { return []; }
        }
        if (Json.Obj(schema, "properties") is not { } properties) { return []; }
        var required = Json.Arr(schema, "required") is { } r
            ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var fields = new List<Field>();
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) { continue; }
            fields.Add(SchemaField(property.Name, property.Value, required.Contains(property.Name)));
        }
        return fields;
    }

    // Whether every field of a schema reads as one input Copilot CLI is known to draw for it.
    static bool Drivable(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(schema.GetString() ?? "");
                return Drivable(doc.RootElement);
            }
            catch (JsonException) { return true; }
        }
        if (Json.Obj(schema, "properties") is not { } properties) { return true; }
        return properties.EnumerateObject().All(p => p.Value.ValueKind != JsonValueKind.Object || CopilotFieldSchema.Read(p.Value).Problem is null);
    }

    // Each field as the input Copilot CLI draws for it (CopilotFieldSchema), which is not
    // always what its keywords suggest: a number with an `enum` is still a number box, and a
    // string with only `anyOf` a text box.
    static Field SchemaField(string name, JsonElement p, bool required)
    {
        var (input, choices, _) = CopilotFieldSchema.Read(p);
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
