using System.Text;
using System.Text.Json;

namespace AgentBridge.Protocol;

// Whether a session.answer fits the question it answers. Shared so every host refuses the
// same answers for the same reasons, before anything is typed into a session.
public static class Answers
{
    // Null when the answer fits; otherwise why not, for a refused outcome. Whether the
    // session is still asking this question (stale) is the host's to decide, not this.
    public static string? Check(Question question, AnswerRequest answer)
    {
        if (question.Truncated == true) { return "the question was too large to show in full; answer it at the machine"; }
        // Per-text limits alone don't bound the request: several text fields, each within
        // its own limit, can add up to more than a host promised to take.
        if (JsonSerializer.SerializeToUtf8Bytes(answer, ProtocolJson.Options).Length > Limits.AnswerRequest)
        {
            return $"the answer is larger than {Limits.AnswerRequest / 1024} KiB";
        }
        if (question.Fields.Count == 0)
        {
            if (answer.Fields is { Count: > 0 }) { return "this question takes a text answer, not fields"; }
            return TextProblem(answer.Text, "the answer") ?? (string.IsNullOrWhiteSpace(answer.Text) ? "the answer is empty" : null);
        }
        if (answer.Text is not null) { return "this question takes fields, not a text answer"; }
        var given = answer.Fields ?? new Dictionary<string, JsonElement>();
        foreach (var name in given.Keys)
        {
            if (!question.Fields.Any(f => f.Name == name)) { return $"the question has no field \"{name}\""; }
        }
        foreach (var field in question.Fields)
        {
            if (!given.TryGetValue(field.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (field.Required == true) { return $"\"{Label(field)}\" needs an answer"; }
                continue;
            }
            if (ValueProblem(field, value) is { } problem) { return $"\"{Label(field)}\": {problem}"; }
        }
        return given.Count == 0 ? "the answer is empty" : null;
    }

    static string? ValueProblem(Field field, JsonElement value) => field.Type switch
    {
        FieldType.Choice => ChoiceProblem(field, value),
        FieldType.Multi => value.ValueKind != JsonValueKind.Array ? "expected a list of choices" : MultiProblem(field, value),
        FieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "expected true or false",
        FieldType.Number => value.ValueKind == JsonValueKind.Number ? null : "expected a number",
        _ => value.ValueKind == JsonValueKind.String ? TextProblem(value.GetString(), "the text") : "expected text",
    };

    static string? ChoiceProblem(Field field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return field.Options?.Any(o => o.Value == value.GetString()) == true ? null : "not one of the choices";
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("other", out var other))
        {
            if (field.AllowOther != true) { return "this choice takes no other answer"; }
            if (other.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(other.GetString())) { return "the other answer is empty"; }
            if (value.EnumerateObject().Count() != 1) { return "unexpected properties beside other"; }
            return TextProblem(other.GetString(), "the other answer");
        }
        return "expected a choice";
    }

    static string? MultiProblem(Field field, JsonElement value)
    {
        var others = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) { others++; }
            if (ChoiceProblem(field, item) is { } problem) { return problem; }
        }
        return others > 1 ? "at most one other answer" : null;
    }

    static string? TextProblem(string? text, string what) =>
        text is not null && Encoding.UTF8.GetByteCount(text) > Limits.AnswerText ? $"{what} is longer than {Limits.AnswerText / 1024} KiB" : null;

    static string Label(Field field) => field.Title ?? field.Name;
}
