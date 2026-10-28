using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

internal static class Text
{
    // One display line: the first non-blank line, with runs of whitespace collapsed.
    public static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        foreach (var raw in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            var sb = new StringBuilder(raw.Length);
            var space = false;
            foreach (var c in raw.Trim())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) { sb.Append(' '); }
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }
        return null;
    }

    // A title: the whole text on one line, whitespace collapsed, cut to the limit.
    public static string? Title(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Limits.KeepStart(collapsed, Limits.Title);
    }

    public static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

// The one line a client shows for a tool call: its `description` argument when it has
// one (Copilot's shell tools and Claude's Bash and Task carry one written for people),
// else a short form of the argument that says what it acts on.
internal static class ToolSummary
{
    static readonly string[] MainArguments =
    [
        "command", "cmd", "file_path", "path", "notebook_path", "pattern", "query", "url",
        "prompt", "question", "message", "skill", "agent_id", "shellId", "name", "input",
    ];

    public static string? From(JsonElement? arguments)
    {
        if (arguments is not { } args) { return null; }
        if (args.ValueKind == JsonValueKind.String)
        {
            // Codex passes function arguments as a JSON string, and custom tools a raw one.
            var raw = args.GetString();
            if (raw is { Length: > 0 } && raw.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    return From(doc.RootElement);
                }
                catch (JsonException) { }
            }
            return Cut(raw);
        }
        if (args.ValueKind != JsonValueKind.Object) { return null; }
        if (Json.Str(args, "description") is { } description && Cut(description) is { } d) { return d; }
        foreach (var name in MainArguments)
        {
            if (Json.Get(args, name) is { } value && Cut(Describe(value)) is { } s) { return s; }
        }
        foreach (var property in args.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && Cut(property.Value.GetString()) is { } s) { return s; }
        }
        return null;
    }

    // Codex runs commands as argv arrays, usually ["bash", "-lc", "<script>"]: the script
    // is what a person wants to see.
    static string? Describe(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) { return value.GetString(); }
        if (value.ValueKind != JsonValueKind.Array) { return null; }
        var parts = value.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList();
        if (parts.Count >= 3 && parts[1] is "-lc" or "-c" or "/c" or "-Command") { return parts[^1]; }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    public static string? Cut(string? text) => Limits.KeepStart(Text.OneLine(text), Limits.ToolSummary);
}
