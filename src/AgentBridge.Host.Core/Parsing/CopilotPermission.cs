using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// One Copilot permission prompt, read from its permission.requested event: what a person is
// asked to allow, written out in full for a client, and the identity an approval binds to.
//
// The question id is "<requestId>#<digest>". The digest is a hash of:
//   - the message as it was built from Copilot's own words, before anything in it was escaped
//     for display;
//   - each name the message shows only escaped and on one line (a tool's title or name, an
//     access kind), as Copilot wrote it, when that name holds a character drawn other than as
//     it runs;
//   - and whether the prompt is held at the machine at all.
// So an approval names both the request and exactly what would be approved: a host that finds
// the request now reads differently (or another request in its place) types nothing, and no
// prompt answered here shares an id with one that is not. Hashing the escaped text instead would
// let a prompt holding a real U+202E and one holding the six characters "\u202E" share one id.
//
// Message is what clients show: every character that is not drawn where it runs (a bidi
// control, a zero-width character, a control but newline and tab) and every right-to-left
// letter as a visible escape (ShownText, #220). Approve once is still offered for it: the
// owner's "escape and allow" rule (#253, decision 3).
//
// AtMachine says why the prompt is not offered to other devices at all: a kind the host
// does not drive, or a request to run outside Copilot's sandbox.
//
// Probes are short pieces of what the prompt is about (the start of the command, a file or
// path's last segment, a URL's host, a tool's name) that the CLI draws above its options. A
// host finds them on the session's screen to know the prompt there is this one, and holds
// those rows with the options when it types, so a prompt for something else drawn in the
// same place, with the same options, stops the key.
public sealed record CopilotPermissionPrompt(string RequestId, string? ToolCallId, string Kind, string Message, string Digest, string? AtMachine)
{
    public IReadOnlyList<string> Probes { get; init; } = [];

    public string QuestionId => $"{RequestId}#{Digest}";

    public static (string RequestId, string Digest)? ParseQuestionId(string id)
    {
        var at = id.LastIndexOf('#');
        return at <= 0 || at == id.Length - 1 ? null : (id[..at], id[(at + 1)..]);
    }
}

public static partial class CopilotPermission
{
    // The kinds whose prompt is a plain approve-once / deny choice in every Copilot build seen
    // (1.0.10x): shell commands, file edits, file reads, path access, URL fetches, MCP tools
    // and custom tools. Anything else (hooks, memory, workflows, extensions) stays at the
    // machine.
    static readonly HashSet<string> Driven = new(StringComparer.Ordinal) { "commands", "write", "read", "path", "url", "mcp", "custom-tool" };

    // Lines end in \n on every platform: a CR is a character not drawn where it runs (ShownText),
    // and Windows' default line ending would end every line of a tool's arguments in its escape.
    static readonly JsonWriterOptions Indented = new() { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
        // What a person reads must be what runs. A bidi control or a zero-width character is drawn
        // somewhere other than where it runs (or not at all), and right-to-left letters reorder
        // what is drawn around them, so each is shown as a visible escape (#220), and Approve once
        // is still offered (#253, decision 3). The digest stays over what Copilot wrote, all of it.
        // Shown from no more than a question can carry: a transcript line can be 32 MiB, and each
        // escape is six characters or more, so escaping all of it first could make it six times that
        // before the question cut it (QuestionMapper.Approval, which says when it was cut).
        return new CopilotPermissionPrompt(request, toolCallId, kind, ShownText.RevealWithin(message, Limits.QuestionMessage, rightToLeft: true), Digest(kind, message, p, atMachine is not null), atMachine) { Probes = Probes(kind, p) };
    }

    static List<string> Probes(string kind, JsonElement p)
    {
        static string? Start(string? text) =>
            text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } line ? line[..Math.Min(40, line.Length)].TrimEnd() : null;
        static string? Last(string? path) =>
            path?.TrimEnd('\\', '/') is { Length: > 0 } trimmed ? trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..] : null;
        IEnumerable<string?> found = kind switch
        {
            "commands" => [Start(Json.Str(p, "fullCommandText"))],
            "write" => [Last(Json.Str(p, "fileName"))],
            "read" => [Last(Json.Str(p, "path"))],
            "path" => Json.Arr(p, "paths") is { } paths ? paths.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => Last(e.GetString())) : [],
            "url" => [Uri.TryCreate(Json.Str(p, "url"), UriKind.Absolute, out var u) ? u.Host : Start(Json.Str(p, "url"))],
            "mcp" => [Json.Str(p, "toolTitle"), Json.Str(p, "toolName")],
            "custom-tool" => [Json.Str(p, "toolName")],
            _ => [],
        };
        return [.. found.OfType<string>().Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    static string? Message(string kind, JsonElement p)
    {
        var intention = Json.Str(p, "intention");
        switch (kind)
        {
            case "commands" when Json.Str(p, "fullCommandText") is { } command:
                return Join("Allow **shell**?", intention, Fence(command));
            case "write" when Json.Str(p, "fileName") is { } file:
                {
                    // The change as the CLI shows it, else the whole new file; an edit whose
                    // content cannot be shown is not approved from elsewhere.
                    var change = Json.Str(p, "diff") is { Length: > 0 } diff ? Fence(diff, "diff")
                        : Json.Str(p, "newFileContents") is { } contents ? Fence(contents) : null;
                    return change is null ? null : Join("Allow **edit**?", intention, Fence(file), change);
                }
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
        return Fence(Controls(Encoding.UTF8.GetString(buffer.ToArray())), "json");
    }

    // JSON writes a carriage return, a backspace and a form feed in an argument as \r, \b and \f, which
    // no reader of escapes takes for the characters they are: the notes and the Needs you lane read
    // the \u000D a host writes for one (ShownText.Unreveal, hidden-text.js), so a CR in a tool's
    // argument, which PowerShell ends a `#` comment at, went without a word (#220). Each is written so;
    // an escaped backslash before an r (\\r) is two characters of text, and stays as it is.
    static string Controls(string json) => !json.Contains('\\') ? json : JsonEscape().Replace(json, m => m.Value switch
    {
        @"\r" => @"\u000D",
        @"\b" => @"\u0008",
        @"\f" => @"\u000C",
        _ => m.Value,
    });

    [GeneratedRegex(@"\\(?:u[0-9A-Fa-f]{4}|.)", RegexOptions.Singleline)]
    private static partial Regex JsonEscape();

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

    // A name inside **bold**: markup characters would change what the line says. Hidden
    // characters are shown escaped first, so collapsing the name to one line cannot drop them.
    static string Plain(string text) => Text.OneLine(ShownText.Reveal(text, rightToLeft: true))?.Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`") ?? "";

    // The names a message shows escaped and on one line, whose own text the identity holds as well
    // when it holds a character drawn other than as it runs: a custom tool named with a real U+202E
    // and one named with the six characters "\u202E" are shown alike, as are titles
    // "Get file\nsafe" and "Get file\n" with Hebrew letters (one line of each is shown), but they
    // are not the same prompt. Any other prompt's id is as it was.
    static readonly string[] Named = ["toolTitle", "toolName", "accessKind"];

    // An ordinary prompt's identity is "<kind>\n<message>", as it always was. One with more to it
    // (held at the machine, or a misleading name) or a NUL in it is written so no other can spell it:
    // a NUL first, which the ordinary form never has, then each part with its length. Appended as
    // text after the message, "\n\0at-machine" could be written by the message itself: a custom
    // tool described as "run\n\0at-machine" had the id of "run" run outside the sandbox.
    static string Digest(string kind, string message, JsonElement p, bool atMachine)
    {
        var named = Named.Select(name => (name, raw: Json.Str(p, name))).Where(x => x.raw is { } raw && ShownText.Misleads(raw)).ToList();
        string identity;
        if (!atMachine && named.Count == 0 && !kind.Contains('\0') && !message.Contains('\0')) { identity = $"{kind}\n{message}"; }
        else
        {
            var b = new StringBuilder("\0identity/2");
            void Part(string text) => b.Append('\0').Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);
            Part(kind);
            Part(message);
            Part(atMachine ? "at-machine" : "");
            foreach (var (name, raw) in named) { Part(name); Part(raw!); }
            identity = b.ToString();
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
