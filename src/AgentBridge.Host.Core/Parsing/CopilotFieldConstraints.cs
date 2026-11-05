using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// The constraints of one field of Copilot's ask_user form, for the input CopilotFieldSchema says
// the CLI draws (#766). Copilot CLI 1.0.105 draws that form with enforceSchemaConstraints off
// (`Uxn`: its elicitation has no source, so `elicitationSource !== JD` is false), so it checks
// none of them and records whatever is typed: the host is the only check. Read here for the
// question a client is shown (QuestionMapper) and for the keys (CopilotForm), alike.
//
// Each applies to the input its keyword is for, as JSON Schema means it: bounds, `integer` and
// `multipleOf` to a number box; length, `pattern` and `format` to a text box; `minItems` and
// `maxItems` to a checkbox list. Problem is set when a keyword is there but cannot be checked
// exactly (a bound that is not a number JavaScript holds exactly, a length that is not a whole
// number, a pattern that cannot be read as JavaScript reads it), or when the default a field
// opens with breaks them: such a field is answered at the machine. Patterns share the budget of
// the whole ask_user they are read for (PatternBudget), so an agent's slow patterns cost at most
// that much however many fields carry them.
public static class CopilotFieldConstraints
{
    public static (FieldConstraints? Constraints, string? Problem) Read(JsonElement p, PatternBudget budget)
    {
        if (p.ValueKind != JsonValueKind.Object) { return (null, null); }
        var (input, _, unread) = CopilotFieldSchema.Read(p);
        if (unread is not null) { return (null, null); }
        FieldConstraints c;
        string? problem = null;
        switch (input)
        {
            case CopilotFieldSchema.Input.Number:
                c = new FieldConstraints
                {
                    Minimum = Number(p, "minimum", ref problem),
                    Maximum = Number(p, "maximum", ref problem),
                    ExclusiveMinimum = Number(p, "exclusiveMinimum", ref problem),
                    ExclusiveMaximum = Number(p, "exclusiveMaximum", ref problem),
                    MultipleOf = Number(p, "multipleOf", ref problem),
                    Integer = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "integer" ? true : null,
                };
                if (c.MultipleOf is { } step && ExactDecimal.Parse(step.GetRawText()) is not { Sign: > 0 }) { problem ??= Uncheckable; }
                break;
            case CopilotFieldSchema.Input.Text:
                c = new FieldConstraints
                {
                    MinLength = Count(p, "minLength", ref problem),
                    MaxLength = Count(p, "maxLength", ref problem),
                    Pattern = Pattern(p, budget, ref problem),
                    Format = Format(p, ref problem),
                };
                break;
            case CopilotFieldSchema.Input.Multi:
                c = new FieldConstraints { MinItems = Count(p, "minItems", ref problem), MaxItems = Count(p, "maxItems", ref problem) };
                break;
            default:
                return (null, null);
        }
        if (problem is not null) { return (null, problem); }
        if (c == new FieldConstraints()) { return (null, null); }
        return Kept(p, input, c, budget) switch
        {
            null => (c, null),
            FieldConstraints.Unchecked => (null, Uncheckable),
            var broken => (null, $"has a default outside its own limits ({broken})"),
        };
    }

    const string Uncheckable = "has limits the host cannot check exactly";

    // The default a text or number box opens with, which is the only answer it can be given from
    // another device (#662), checked as what it would be recorded as: a number box's as the number
    // the CLI holds, a text box's as the words in it. One the host cannot read exactly is refused
    // elsewhere (CopilotForm.Keeps), so it is not judged here.
    static string? Kept(JsonElement p, CopilotFieldSchema.Input input, FieldConstraints c, PatternBudget budget)
    {
        if (!p.TryGetProperty("default", out var def) || def.ValueKind == JsonValueKind.Null) { return null; }
        if (def.ValueKind == JsonValueKind.String && def.GetString()!.Length == 0) { return null; }
        if (input == CopilotFieldSchema.Input.Number)
        {
            var text = def.ValueKind switch { JsonValueKind.Number => def.GetRawText(), JsonValueKind.String => def.GetString(), _ => null };
            return text is not null && CopilotFieldSchema.HeldExactly(text) is not null ? c.NumberProblem(text) : null;
        }
        if (input != CopilotFieldSchema.Input.Text) { return null; }
        var words = def.ValueKind == JsonValueKind.String ? def.GetString()! : def.GetRawText();
        return string.IsNullOrWhiteSpace(words) ? null : c.TextProblem(words, budget);
    }

    static bool Has(JsonElement p, string name, out JsonElement value) =>
        p.TryGetProperty(name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // A bound, as written, when JavaScript holds it exactly: what a client compares is a double.
    static JsonElement? Number(JsonElement p, string name, ref string? problem)
    {
        if (!Has(p, name, out var v)) { return null; }
        if (v.ValueKind != JsonValueKind.Number || CopilotFieldSchema.HeldExactly(v.GetRawText()) is null) { problem ??= Uncheckable; return null; }
        return v.Clone();
    }

    // A non-negative whole number. One past int.MaxValue bounds nothing text or a list can reach,
    // so it is held as int.MaxValue, which means the same.
    static int? Count(JsonElement p, string name, ref string? problem)
    {
        if (!Has(p, name, out var v)) { return null; }
        if (v.ValueKind != JsonValueKind.Number || ExactDecimal.Parse(v.GetRawText()) is not { IsInteger: true, Sign: >= 0 } n)
        {
            problem ??= Uncheckable;
            return null;
        }
        return ExactDecimal.Compare(n, new ExactDecimal(int.MaxValue, 0)) > 0 ? int.MaxValue : (int)(n.Mantissa * System.Numerics.BigInteger.Pow(10, (int)n.Exponent));
    }

    static string? Pattern(JsonElement p, PatternBudget budget, ref string? problem)
    {
        if (!Has(p, "pattern", out var v)) { return null; }
        if (v.ValueKind != JsonValueKind.String || !FieldConstraints.Readable(v.GetString()!, budget)) { problem ??= Uncheckable; return null; }
        return v.GetString();
    }

    // The four formats the CLI knows; any other it would not check either, so neither does this.
    static string? Format(JsonElement p, ref string? problem)
    {
        if (!Has(p, "format", out var v)) { return null; }
        if (v.ValueKind != JsonValueKind.String) { problem ??= Uncheckable; return null; }
        return FieldConstraints.Formats.Contains(v.GetString()) ? v.GetString() : null;
    }
}
