using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentBridge.Host.Core.Parsing;

// One Copilot permission prompt, read from its permission.requested event: what a person is
// asked to allow, written out in full for a client, and the identity an approval binds to.
//
// The question id is "<requestId>#<digest>", the digest a hash of the message shown. So an
// approval names both the request and exactly what the person read: a host that finds the
// request now reads differently (or another request in its place) types nothing.
//
// AtMachine says why the prompt is not offered to other devices at all: a kind the host
// does not drive, or a request to run outside Copilot's sandbox.
public sealed record CopilotPermissionPrompt(string RequestId, string? ToolCallId, string Kind, string Message, string Digest, string? AtMachine)
{
    public string QuestionId => $"{RequestId}#{Digest}";

    public static (string RequestId, string Digest)? ParseQuestionId(string id)
    {
        var at = id.LastIndexOf('#');
        return at <= 0 || at == id.Length - 1 ? null : (id[..at], id[(at + 1)..]);
    }
}

public static class CopilotPermission
{
    // The kinds whose prompt is a plain approve-once / deny choice in every Copilot build seen
    // (1.0.10x): shell commands, file edits, file reads, path access, URL fetches, MCP tools
    // and custom tools. Anything else (hooks, memory, workflows, extensions) stays at the
    // machine.
    static readonly HashSet<string> Driven = new(StringComparer.Ordinal) { "commands", "write", "read", "path", "url", "mcp", "custom-tool" };

    static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // data: a permission.requested event's data. promptRequest is what the CLI draws, so it is
    // preferred; older builds wrote only permissionRequest, whose shell kind is the commands
    // prompt.
    public static CopilotPermissionPrompt? Describe(JsonElement data)
    {
        var request = Json.Str(data, "requestId");
        var prompt = Json.Obj(data, "promptRequest");
        var permission = Json.Obj(data, "permissionRequest");
        if (request is null || (prompt ?? permission) is not { } p) { return null; }
        var kind = Json.Str(p, "kind") ?? "";
        if (prompt is null && kind == "shell") { kind = "commands"; }
        var toolCallId = Json.Str(p, "toolCallId") ?? (permission is { } q ? Json.Str(q, "toolCallId") : null);

        string? atMachine = null;
        var message = Message(kind, p);
        if (!Driven.Contains(kind)) { atMachine = "this kind of permission prompt"; }
        else if (message is null) { atMachine = "a permission prompt whose details could not be read"; }
        else if (Json.Bool(p, "requestSandboxBypass") == true || (permission is { } r && Json.Bool(r, "requestSandboxBypass") == true))
        {
            atMachine = "a request to run outside Copilot's sandbox";
        }

        message ??= Fallback(p, kind);
        return new CopilotPermissionPrompt(request, toolCallId, kind, message, Digest(kind, message), atMachine);
    }

    static string? Message(string kind, JsonElement p)
    {
        var intention = Json.Str(p, "intention");
        switch (kind)
        {
            case "commands" when Json.Str(p, "fullCommandText") is { } command:
                return Join("Allow **shell**?", intention, Fence(command));
            case "write" when Json.Str(p, "fileName") is { } file:
                return Join("Allow **edit**?", intention, Fence(file), Json.Str(p, "diff") is { Length: > 0 } diff ? Fence(diff, "diff") : null);
            case "read" when Json.Str(p, "path") is { } path:
                return Join("Allow **read**?", intention, Fence(path));
            case "path" when Json.Arr(p, "paths") is { } list:
                {
                    var paths = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                    if (paths.Count == 0) { return null; }
                    var access = Json.Str(p, "accessKind") is { Length: > 0 } a ? a : "path";
                    return Join($"Allow **{access}** access?", Fence(string.Join('\n', paths)));
                }
            case "url" when Json.Str(p, "url") is { } url:
                return Join("Allow **URL** access?", intention, Fence(url));
            case "mcp" when Json.Str(p, "toolName") is { } tool:
                {
                    var server = Json.Str(p, "serverName") ?? "?";
                    var title = Json.Str(p, "toolTitle") is { Length: > 0 } t ? t : tool;
                    return Join($"Allow **{Plain(title)}**?", Fence($"{server}/{tool}"), Args(p));
                }
            case "custom-tool" when Json.Str(p, "toolName") is { } tool:
                return Join($"Allow **{Plain(tool)}**?", Json.Str(p, "toolDescription"), Args(p));
            default:
                return null;
        }
    }

    // What was shown before this host drove permission prompts, for a kind it does not.
    static string Fallback(JsonElement p, string kind)
    {
        var what = Json.Str(p, "toolTitle") ?? Json.Str(p, "toolName") ?? (kind.Length > 0 ? kind : "a tool");
        var detail = ToolSummary.From(Json.Get(p, "args")) ?? Text.OneLine(Json.Str(p, "fullCommandText") ?? Json.Str(p, "intention"));
        return detail is null ? $"Allow **{Plain(what)}**?" : $"Allow **{Plain(what)}**?\n\n{Fence(detail)}";
    }

    static string? Args(JsonElement p)
    {
        if (Json.Get(p, "args") is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } args) { return null; }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Indented)) { args.WriteTo(writer); }
        return Fence(Encoding.UTF8.GetString(buffer.ToArray()), "json");
    }

    static string Join(params string?[] parts) => string.Join("\n\n", parts.Where(s => !string.IsNullOrWhiteSpace(s)));

    // A code block that the text cannot close early: its fence is longer than any run of
    // backticks inside.
    static string Fence(string text, string language = "")
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{text}\n{fence}";
    }

    // A name inside **bold**: markup characters would change what the line says.
    static string Plain(string text) => Text.OneLine(text)?.Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`") ?? "";

    static string Digest(string kind, string message)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{message}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
