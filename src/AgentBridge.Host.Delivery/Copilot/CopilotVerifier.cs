using System.Text.Json;

namespace AgentBridge.Host.Delivery.Copilot;

// Whether what the session recorded is the answer that was typed. Matched only on
// evidence: a record that cannot be compared is not a match, and is never treated as one.
public static class CopilotVerifier
{
    public static (bool Matched, string Reason) Check(CopilotPlan plan, CopilotAnswerRecord record)
    {
        if (record.HumanContent is { } content) { return Human(plan, content); }
        if (!record.Succeeded) { return (false, "the question ended with an error instead of the answer"); }
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

    // tool.execution_complete alone: "User responded: <words>" or "User selected: <option>",
    // which is trusted only where it can be read one way. A form of several fields renders
    // as "a=x, b=y", which is equally a="x, b=y" with b left out, so only its structured
    // record (human_response.recorded, which current builds write) can confirm it.
    static (bool, string) Rendered(CopilotPlan plan, string? text)
    {
        var rest = Strip(text?.Trim());
        if (rest is null)
        {
            return (false, text?.Trim() == "User skipped question"
                ? "the session recorded the question as skipped"
                : "the session recorded the answer in a form the host could not compare");
        }
        if (plan.Text is { } words) { return Same(rest, words) ? (true, "") : (false, "the session recorded different words"); }
        if (plan.Fields is not [var (name, value)])
        {
            return (false, "the session recorded only a rendering of the form, without the structured answer that could confirm it");
        }
        if (Spell(value) is not { } spelling) { return (false, "the session's record of this answer cannot be compared"); }
        // Read bare, as the CLI renders a lone field. Also accepting "name=value" let an
        // option spelled "pick=x" pass for the option "x".
        return Same(rest, spelling) ? (true, "") : (false, $"the session recorded a different answer for \"{name}\"");
    }

    static string? Strip(string? text)
    {
        foreach (var prefix in (string[])["User responded: ", "User selected: "])
        {
            if (text?.StartsWith(prefix, StringComparison.Ordinal) == true) { return text[prefix.Length..]; }
        }
        return null;
    }

    // How a value reads in the rendering, or null when it has no single spelling.
    static string? Spell(Expected expected) => expected switch
    {
        Expected.Omitted => "",
        Expected.Option o => CopilotForm.Scalar(o.Raw),
        Expected.Words w => w.Text,
        Expected.Number n => n.Raw.GetRawText(),
        Expected.Flag f => f.Value ? "true" : "false",
        _ => null,
    };

    static bool Same(string recorded, string typed) => InjectableText.Flatten(recorded) == InjectableText.Flatten(typed);
}
