using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// Maps each agent's way of asking onto the contract's Question and Field.
internal static class QuestionMapper
{
    public static readonly IReadOnlyList<Option> ApprovalOptions = [new("approve", "Approve"), new("deny", "Deny")];

    // A permission prompt: a fixed approve-or-deny choice.
    public static Question Approval(string id, string message, DateTimeOffset askedAt) => new()
    {
        Id = id,
        Kind = QuestionKind.Approval,
        Message = Limits.KeepStart(message, Limits.QuestionMessage)!,
        Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = ApprovalOptions, Required = true }],
        AskedAt = askedAt,
    };

    // Copilot's ask_user. Current builds pass `message` and `requestedSchema` (a JSON
    // Schema form); older ones passed `question` and a `choices` list, sometimes as a
    // JSON string. Without reading both, a multiple choice became a free-text box titled
    // "needs your input".
    public static Question FromCopilotAskUser(string id, JsonElement arguments, DateTimeOffset askedAt)
    {
        var message = Json.Str(arguments, "message") ?? Json.Str(arguments, "question") ?? "";
        IReadOnlyList<Field> fields = [];
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
        }
        return new Question
        {
            Id = id,
            Kind = QuestionKind.Form,
            Message = Limits.KeepStart(message, Limits.QuestionMessage)!,
            Fields = fields,
            AskedAt = askedAt,
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

    static Field SchemaField(string name, JsonElement p, bool required)
    {
        var type = Json.Str(p, "type");
        FieldType kind;
        IReadOnlyList<Option>? options = null;
        if (type == "array")
        {
            kind = FieldType.Multi;
            options = Json.Obj(p, "items") is { } items ? Options(items) : null;
        }
        else if (Options(p) is { Count: > 0 } choices)
        {
            kind = FieldType.Choice;
            options = choices;
        }
        else
        {
            kind = type switch
            {
                "boolean" => FieldType.Boolean,
                "number" or "integer" => FieldType.Number,
                _ => FieldType.Text,
            };
        }
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

    // Options from `enum` (labelled by `enumNames` when present), or from `oneOf` /
    // `anyOf` entries of `{const, title}`. The value is what the agent records as the
    // answer and the label what to show; they differ exactly when a form is written the
    // richer way, which is when confusing them matters.
    static List<Option>? Options(JsonElement p)
    {
        if (Json.Arr(p, "enum") is { } values)
        {
            var labels = Json.Arr(p, "enumNames") is { } n ? n.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList() : [];
            var i = 0;
            var list = new List<Option>();
            foreach (var v in values.EnumerateArray())
            {
                var label = i < labels.Count ? labels[i] : null;
                list.Add(new Option(Scalar(v), string.IsNullOrEmpty(label) ? null : label));
                i++;
            }
            return list;
        }
        var entries = Json.Arr(p, "oneOf") ?? Json.Arr(p, "anyOf");
        if (entries is not { } e) { return null; }
        var options = new List<Option>();
        foreach (var entry in e.EnumerateArray())
        {
            var title = Json.Str(entry, "title");
            var value = Json.Get(entry, "const") is { } c ? Scalar(c) : title;
            if (value is null) { continue; }
            options.Add(new Option(value, title is not null && title != value ? title : null));
        }
        return options;
    }

    static string Scalar(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();

    // Claude's AskUserQuestion: { questions: [ { question, header, multiSelect, options:
    // [ { label, description } ] } ] }. Claude records the answer under the question text
    // and always offers "Other", so each field takes free text too. Option descriptions
    // carry real content but have no place on an Option, so they are listed under the
    // message, as the old bridge did.
    public static Question FromClaudeAskUserQuestion(string id, JsonElement input, DateTimeOffset askedAt)
    {
        var fields = new List<Field>();
        var questions = new List<string>();
        var notes = new StringBuilder();
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
                fields.Add(new Field
                {
                    Name = text,
                    Title = Json.Str(q, "header"),
                    Description = Json.Str(q, "header") is null ? null : text,
                    Type = Json.Bool(q, "multiSelect") == true ? FieldType.Multi : FieldType.Choice,
                    Options = options,
                    AllowOther = true,
                    Required = true,
                });
            }
        }
        var message = questions.Count == 0 ? "Claude needs an answer." : string.Join("\n\n", questions);
        if (notes.Length > 0) { message += "\n" + notes; }
        return new Question
        {
            Id = id,
            Kind = QuestionKind.Form,
            Message = Limits.KeepStart(message, Limits.QuestionMessage)!,
            Fields = fields,
            AskedAt = askedAt,
        };
    }
}
