using System.Text.Json;

namespace AgentBridge.Host.Delivery.Copilot;

// Whether what the session recorded is the answer that was typed. Matched only on
// evidence: a record that cannot be compared is not a match, and is never treated as one.
public static class CopilotVerifier
{
    public static (bool Matched, string Reason) Check(CopilotPlan plan, CopilotAnswerRecord record)
    {
        // A call that ended in an error did not take the answer, whatever else was recorded.
        if (!record.Succeeded) { return (false, "the question ended with an error instead of the answer"); }
        if (record.HumanContent is { } content) { return Human(plan, content); }
        return Rendered(plan, record.ResultText);
    }

    // human_response.recorded: the structured answer, one property per answered field.
    static (bool, string) Human(CopilotPlan plan, JsonElement content)
    {
        if (plan.Text is { } words)
        {
            var recorded = content.ValueKind == JsonValueKind.String ? content.GetString()
                : content.ValueKind == JsonValueKind.Object && content.EnumerateObject().ToList() is [{ Value.ValueKind: JsonValueKind.String } only] ? only.Value.GetString()
                : null;
            if (recorded is null) { return (false, "the session recorded the answer in a form the host could not compare"); }
            return Same(recorded, words) ? (true, "") : (false, "the session recorded different words");
        }
        if (content.ValueKind != JsonValueKind.Object) { return (false, "the session recorded the answer in a form the host could not compare"); }
        foreach (var property in content.EnumerateObject())
        {
            if (!plan.Fields.Any(f => f.Name == property.Name)) { return (false, $"the session recorded a field \"{property.Name}\" that was not typed"); }
        }
        foreach (var (name, expected) in plan.Fields)
        {
            var has = content.TryGetProperty(name, out var value);
            if (!Matches(expected, has ? value : null)) { return (false, $"the session recorded a different answer for \"{name}\""); }
        }
        return (true, "");
    }

    static bool Matches(Expected expected, JsonElement? recorded)
    {
        if (expected is Expected.Omitted)
        {
            return recorded is not { } r || r.ValueKind == JsonValueKind.Null || (r.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(r.GetString()));
        }
        if (recorded is not { } v) { return false; }
        return expected switch
        {
            Expected.Option o => CopilotForm.SameScalar(o.Raw, v),
            Expected.Words w => v.ValueKind == JsonValueKind.String && Same(v.GetString()!, w.Text),
            Expected.Number n => CopilotForm.SameScalar(n.Raw, v) || (v.ValueKind == JsonValueKind.String && Same(v.GetString()!, n.Raw.GetRawText())),
            Expected.Flag f => v.ValueKind == (f.Value ? JsonValueKind.True : JsonValueKind.False),
            Expected.Set s => v.ValueKind == JsonValueKind.Array && SameSet(s.Raws, [.. v.EnumerateArray()]),
            _ => false,
        };
    }

    static bool SameSet(IReadOnlyList<JsonElement> wanted, List<JsonElement> recorded)
    {
        if (wanted.Count != recorded.Count) { return false; }
        var used = new bool[recorded.Count];
        foreach (var w in wanted)
        {
            var i = Enumerable.Range(0, recorded.Count).FirstOrDefault(k => !used[k] && CopilotForm.SameScalar(w, recorded[k]), -1);
            if (i < 0) { return false; }
            used[i] = true;
        }
        return true;
    }

    // tool.execution_complete alone. Only a question without fields is confirmed from it,
    // as "User responded: <words>", compared like any typed words. A form, even of one
    // field, is not: its rendering trims, flattens "a=x, b=y" (equally a="x, b=y"), and
    // cannot tell options "x", "x " and "pick=x" apart. Current builds write the
    // structured human_response.recorded for every form, and that is what confirms one.
    static (bool, string) Rendered(CopilotPlan plan, string? text)
    {
        if (plan.Text is null)
        {
            return (false, "the session recorded only a rendering of the form, without the structured answer that could confirm it");
        }
        var trimmed = text?.Trim();
        if (trimmed?.StartsWith("User responded: ", StringComparison.Ordinal) != true)
        {
            return (false, trimmed == "User skipped question"
                ? "the session recorded the question as skipped"
                : "the session recorded the answer in a form the host could not compare");
        }
        return Same(trimmed["User responded: ".Length..], plan.Text) ? (true, "") : (false, "the session recorded different words");
    }
    static bool Same(string recorded, string typed) => InjectableText.Flatten(recorded) == InjectableText.Flatten(typed);
}
