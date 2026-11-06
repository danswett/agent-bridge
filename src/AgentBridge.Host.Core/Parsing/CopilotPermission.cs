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
//
// While other requests wait too, Copilot draws one of them, and a probe is too little to say
// which (#690): a probe is a piece of agent-written text, and another request's text can hold
// it. Dialog is then what Copilot's dialog for this prompt must show, row for row, to be taken
// for it (null for a kind whose dialog the host does not know), and Drawn every piece of text
// Copilot was given for it, as written, with any property name that is unsafe (CopilotDialogs).
public sealed record CopilotPermissionPrompt(string RequestId, string? ToolCallId, string Kind, string Message, string Digest, string? AtMachine)
{
    public IReadOnlyList<string> Probes { get; init; } = [];
    public CopilotDialog? Dialog { get; init; }
    public IReadOnlyList<string> Drawn { get; init; } = [];

    // Where Message holds the host's own escapes (ShownText.RevealMarked), for EscapedText.Marks.
    public IReadOnlyList<(int Start, int Length)> Marks { get; init; } = [];

    public string QuestionId => $"{RequestId}#{Digest}";

    public static (string RequestId, string Digest)? ParseQuestionId(string id)
    {
        var at = id.LastIndexOf('#');
        return at <= 0 || at == id.Length - 1 ? null : (id[..at], id[(at + 1)..]);
    }
}

// What Copilot CLI 1.0.105 draws for a permission prompt inside its dialog's frame, above the
// options: its title, a rule, the body's parts a blank row apart (each plain text, or lines in a
// box of their own), and its question. Read from its renderer: the Dialog component (a round
// frame, the title, then a rule) holding the shell prompt (the command in a box, then "Do you
// want to run this command?") or the path prompt ("Allow path access": what it may do, the paths
// in a box, the read-only directories in another, then "Do you want to allow this?").
public sealed record CopilotDialog(string Title, IReadOnlyList<CopilotDialogPart> Body, string Question);

// Boxed: Lines are drawn one to a row, as written, in a box; else Lines[0] is text, wrapped.
public sealed record CopilotDialogPart(bool Boxed, IReadOnlyList<string> Lines);

// What decides, while several permission prompts wait, whether one can be told from the others on
// the session's screen (#690). The host lists a prompt as answerable only when its answer could
// be typed (SessionTracker), and types only then (CopilotPermissionScreen).
public static class CopilotDialogs
{
    // Whether two dialogs could draw the same: as a screen shows them (Visible), every box alike,
    // and the text between two boxes alike as one, once all white space is gone. A box wraps a long
    // line hard at its width, so one line on two rows reads like two lines, and trailing white
    // space is not drawn. Text is wrapped without trimming (1.0.105's `Kve(…, {trim: false, hard:
    // true})`), so a run of spaces as wide as the dialog draws a row of nothing, which reads as the
    // gap between two parts: a warning of "x", 250 spaces and "y" draws as "x\n\ny" does, and
    // counted as one part against two, the screen read for one approved the other (Trust review of
    // #769). Where the text between boxes breaks into parts is never compared, only what it says.
    public static bool Alike(CopilotDialog a, CopilotDialog b) =>
        Squeeze(a.Title) == Squeeze(b.Title) && Squeeze(a.Question) == Squeeze(b.Question) && Segments(a).SequenceEqual(Segments(b));

    // The body as boxes and the text between them, each squeezed; text of nothing but white space
    // draws nothing, so is left out.
    static IEnumerable<(bool Boxed, string Text)> Segments(CopilotDialog dialog)
    {
        string? text = null;
        foreach (var part in dialog.Body)
        {
            if (!part.Boxed) { text = (text ?? "") + Squeeze(string.Concat(part.Lines)); continue; }
            if (text is { Length: > 0 }) { yield return (false, text); }
            text = null;
            yield return (true, Squeeze(string.Concat(part.Lines)));
        }
        if (text is { Length: > 0 }) { yield return (false, text); }
    }

    // Whether a part drawn as text holds a run of spaces that can wrap onto a row of its own, which
    // the screen cannot tell from the gap between two parts, so this dialog would not be found on
    // its own screen. Eight is narrower than any dialog Copilot draws.
    public static bool Unfindable(CopilotDialog dialog) =>
        dialog.Body.Any(p => !p.Boxed && p.Lines.Any(l => l.Contains(WideGap, StringComparison.Ordinal)));

    const string WideGap = "        ";

    // The dialog as a screen can show it, which is how it is read there (CopilotPermissionScreen):
    // a part drawn as text is the runs of its rows between blank ones, each read as a part of its
    // own, and one of nothing but white space draws nothing at all. Counted as written, a warning of
    // white space alone made two requests that draw the same, one with it and one without, compare
    // unlike, and the screen read for one approved the other (Copilot review of #769); Alike now
    // compares the text between boxes whole. A box is drawn with its edges whatever it holds, so is
    // kept as it is.
    public static CopilotDialog Visible(CopilotDialog dialog) => dialog with
    {
        Body = [.. dialog.Body.SelectMany(p => p.Boxed ? [p] : Runs(p.Lines.Count > 0 ? p.Lines[0] : "").Select(run => new CopilotDialogPart(false, [run])))],
    };

    // Text's runs of lines that are not blank, each joined back together.
    static IEnumerable<string> Runs(string text)
    {
        var run = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length > 0) { run.Add(line); continue; }
            if (run.Count > 0) { yield return string.Join('\n', run); run.Clear(); }
        }
        if (run.Count > 0) { yield return string.Join('\n', run); }
    }

    // Text that could draw a part of Copilot's own frame (a line-drawing character), be drawn
    // other than as given (a tab), or hide what is drawn (ShownText.Misleads).
    public static bool Unsafe(string text) => text.Any(c => c is >= '\u2500' and <= '\u257F' or '\t') || AgentBridge.Protocol.ShownText.Misleads(text);

    // Whether the first of several waiting prompts can be told from the others on screen: Copilot
    // draws it first, its dialog is known, every other has something drawn to check it against,
    // nothing given for any of them is unsafe, and none would draw the same. As delivery checks it
    // (CopilotPermissionScreen.Crowded), so nothing is listed as answerable that it refuses.
    public static bool Identifiable(CopilotPermissionPrompt mine, IEnumerable<CopilotPermissionPrompt> others)
    {
        var rest = others.ToList();
        if (rest.Any(o => DrawOrder(o.Kind) < DrawOrder(mine.Kind))) { return false; }
        if (mine.Dialog is not { } dialog || Unfindable(dialog) || mine.Drawn.Any(Unsafe)) { return false; }
        return rest.All(o => o.Drawn.Count > 0 && !o.Drawn.Any(Unsafe) && (o.Dialog is not { } theirs || !Alike(dialog, theirs)));
    }

    // Which waiting prompt Copilot CLI 1.0.105 draws. It keeps a queue for each sort of prompt,
    // each in the order asked, and takes each queue's first as that queue's current one at once
    // (cli-main.js `GCn`: permissionRequestQueue, hookPermissionQueue, pathConfirmationQueue,
    // urlConfirmationQueue, filled by the permission handler by kind). Of the current ones, it
    // draws the permission request (shell, edit, read, MCP, tool, anything else) before a hook's,
    // a hook's before a path's, and a path's before a URL's (`cxo`: `if(r)` … `if(l)` … `if(p)` …
    // `if(g)`), whichever was asked first. So the earliest waiting request is drawn only when none
    // waiting draws ahead of it: a path request asked before a shell one is drawn after it. Listed
    // as answerable then, its controls could never deliver (Quality's review of #769).
    public static int DrawOrder(string kind) => kind switch { "hook" => 1, "path" => 2, "url" => 3, _ => 0 };

    static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
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
        var (shown, marks) = ShownText.RevealWithinMarked(message, Limits.QuestionMessage, rightToLeft: true);
        return new CopilotPermissionPrompt(request, toolCallId, kind, shown, Digest(kind, message, p, atMachine is not null), atMachine)
        {
            Probes = Probes(kind, p),
            Marks = marks,
            Dialog = prompt is { } drawn ? Dialog(kind, drawn) : null,
            Drawn = [.. Strings(prompt), .. Strings(permission)],
        };
    }

    // The dialog Copilot draws for the prompt as its promptRequest describes it; null for a kind
    // whose dialog is not known here, or one drawn with more than the request says (a sandbox
    // bypass).
    static CopilotDialog? Dialog(string kind, JsonElement p)
    {
        static List<string> Each(JsonElement p, string name) =>
            Json.Arr(p, name) is { } list ? [.. list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).OfType<string>()] : [];
        static CopilotDialogPart Text(string text) => new(false, [text]);
        switch (kind)
        {
            case "commands" when Json.Str(p, "fullCommandText") is { } command && Json.Bool(p, "requestSandboxBypass") != true:
                {
                    List<CopilotDialogPart> body = [];
                    if (Json.Str(p, "warning") is { Length: > 0 } warning) { body.Add(Text(warning)); }
                    body.Add(new CopilotDialogPart(true, command.Split('\n')));
                    return new CopilotDialog(Json.Str(p, "intention") ?? "", body, "Do you want to run this command?");
                }
            case "path":
                {
                    var paths = Each(p, "paths");
                    var directories = Each(p, "readOnlyDirectories");
                    if (paths.Count == 0) { return null; }
                    var access = Json.Str(p, "accessKind") switch { "read" => "read", "write" => "write", _ => "read or write" };
                    List<CopilotDialogPart> body =
                    [
                        Text($"This action may {access} the following {(paths.Count == 1 ? "path" : "paths")} outside the paths currently allowed for this session."),
                        new(true, paths),
                    ];
                    if (access == "read" && directories.Count > 0)
                    {
                        var one = directories.Count == 1;
                        body.Add(Text($"File-tool read-only access covers {(one ? "this directory" : "these directories")} and {(one ? "its" : "their")} descendants:"));
                        body.Add(new CopilotDialogPart(true, directories));
                    }
                    return new CopilotDialog("Allow path access", body, "Do you want to allow this?");
                }
            default:
                return null;
        }
    }

    // Every string in a request, at any depth, but the ones naming its kind or tool call, which
    // no dialog draws as written: the most a dialog could draw of it. And every property name, at
    // any depth, that is unsafe (CopilotDialogs.Unsafe): Copilot's MCP and custom-tool dialogs can
    // draw an argument's name as well as its value, and read by value alone a name holding a frame
    // or hidden character went unchecked (#769). Only an unsafe one is kept, so a request with
    // nothing else drawn still has nothing to check the screen against.
    static List<string> Strings(JsonElement? request)
    {
        var found = new List<string>();
        if (request is { } r) { Walk(r, null); }
        return found;

        void Walk(JsonElement e, string? name)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String when name is not ("kind" or "toolCallId" or "accessKind"):
                    if (e.GetString() is { Length: > 0 } s) { found.Add(s); }
                    break;
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (CopilotDialogs.Unsafe(p.Name)) { found.Add(p.Name); }
                        Walk(p.Value, p.Name);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) { Walk(item, name); }
                    break;
            }
        }
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
        // The agent's own words, never Markdown (Literal).
        var intention = Literal(Json.Str(p, "intention"));
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
                    return Join($"Allow **{Plain(access)}** access?", Fence(string.Join('\n', paths)));
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
                {
                    // With its arguments, the description is the agent's words before them. Without
                    // any, it is all the prompt shows of what runs, so it is shown as written, in a
                    // block: escaped as prose, "Deploy: prod" was drawn "Deploy\: prod" by every card
                    // that shows an approval as its source, and with no block at all the cards held
                    // it at the machine as a command in prose (ApprovalShape).
                    var args = Args(p);
                    var description = Json.Str(p, "toolDescription");
                    return Join($"Allow **{Plain(tool)}**?", args is null ? (string.IsNullOrWhiteSpace(description) ? null : Fence(description)) : Literal(description), args);
                }
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
    // no reader of escapes takes for the characters they are, and may write others (a bidi control,
    // half a surrogate pair) as \uXXXX: the notes and the Needs you lane read the \u000D a host writes
    // for one (ShownText.Unreveal, hidden-text.js), so a CR in a tool's argument, which PowerShell ends
    // a `#` comment at, went without a word (#220). Each such character is put back as itself, so the
    // message's own escaping shows it as one of the host's escapes, and marks it (EscapedText, #803);
    // an escaped backslash before an r (\\r) is two characters of text, and stays as it is, as does an
    // escape of anything the escaping would not show (\u0022).
    static string Controls(string json) => !json.Contains('\\') ? json : JsonEscape().Replace(json, m =>
    {
        static bool Escaped(string s) => ShownText.HasHidden(s) || ShownText.HasRightToLeft(s);
        if (m.Groups["high"].Success)
        {
            var pair = char.ConvertFromUtf32(char.ConvertToUtf32(
                (char)int.Parse(m.Groups["high"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
                (char)int.Parse(m.Groups["low"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
            return Escaped(pair) ? pair : m.Value;
        }
        if (m.Groups["hex"].Success)
        {
            var one = ((char)int.Parse(m.Groups["hex"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)).ToString();
            return Escaped(one) ? one : m.Value;
        }
        return m.Value switch
        {
            @"\r" => "\r",
            @"\b" => "\b",
            @"\f" => "\f",
            _ => m.Value,
        };
    });

    [GeneratedRegex(@"\\u(?<high>[dD][89abAB][0-9A-Fa-f]{2})\\u(?<low>[dD][c-fC-F][0-9A-Fa-f]{2})|\\u(?<hex>[0-9A-Fa-f]{4})|\\.", RegexOptions.Singleline)]
    private static partial Regex JsonEscape();

    static string Join(params string?[] parts) => string.Join("\n\n", parts.Where(s => !string.IsNullOrWhiteSpace(s)));

    // A code block that the text cannot close early: its fence is longer than any run of
    // backticks inside.
    internal static string Fence(string text, string language = "")
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

    // A name inside **bold**: its first line, literal (Literal), whitespace collapsed as
    // Text.OneLine does, but a character the message's escaping shows (ShownText) is kept as the
    // text it is, never collapsed or trimmed as a space: escaped with the rest of the message, it is
    // one of the host's own marks (EscapedText, #803). A line of nothing else is kept as it is, so
    // a name of only such characters still shows their escapes rather than nothing.
    internal static string Plain(string text) => FirstLine(text) is { } line ? Literal(line) ?? line : "";

    static string? FirstLine(string text)
    {
        static bool Space(char c) => char.IsWhiteSpace(c) && !ShownText.HasHidden(c.ToString());
        foreach (var raw in text.Split('\n'))
        {
            var start = 0;
            var end = raw.Length;
            while (start < end && Space(raw[start])) { start++; }
            while (end > start && Space(raw[end - 1])) { end--; }
            if (start == end) { continue; }
            var sb = new StringBuilder(end - start);
            var space = false;
            for (var i = start; i < end; i++)
            {
                if (Space(raw[i])) { space = true; continue; }
                if (space) { sb.Append(' '); }
                space = false;
                sb.Append(raw[i]);
            }
            return sb.ToString();
        }
        return null;
    }

    // The agent's own words (an intention, a tool's title or description) drawn as the words they
    // are, never as Markdown (#212). Joined in as they were, an intention holding a line that
    // opens a fence made the command's own fence close it, so the command was drawn as Markdown
    // ([safe](https://other.example) showing only "safe"), under Approve once. So every character
    // that starts an inline construct anywhere (code, emphasis, a link, an image or an address
    // made a link, raw HTML, an entity, a table cell, the extras' strikethrough, sub- and
    // superscript, inserted and marked text) is backslash-escaped, as is a line's first character
    // where it would start a block (a heading, a quote, a list, a rule or a setext underline), and
    // a line's leading whitespace, which would make it indented code, is dropped. What is left is
    // paragraphs of text.
    internal static string? Literal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var b = new StringBuilder(text.Length + 16);
        foreach (var raw in text.Split('\n'))
        {
            if (b.Length > 0) { b.Append('\n'); }
            var line = raw.TrimStart(' ', '\t');
            // Whether the line so far is all digits, kept as it goes: copying the prefix to check it at
            // every '.' or ')' made a line of 100,000 periods copy some 10 GB.
            var digits = true;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var escape = c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '&' or '|' or '~' or '^' or '+' or '=' or '!' or '#' or ':'
                    || (i == 0 && c == '-')
                    // An ordered list's marker: digits then . or ) at a line's start.
                    || ((c is '.' or ')') && i > 0 && digits)
                    // A bare address (https://, mailto:, www.) is not made a link either: a link
                    // drawn from it would end where the renderer guesses, escapes and all.
                    || (c == '.' && i >= 3 && line.AsSpan(i - 3, 3).Equals("www", StringComparison.OrdinalIgnoreCase));
                if (escape) { b.Append('\\'); }
                b.Append(c);
                digits &= char.IsAsciiDigit(c);
            }
        }
        return b.ToString();
    }

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
        return IdentityHash(identity);
    }

    // The digest of an approval's identity, as a question id carries it: the first 8 bytes of its
    // SHA-256 over its WTF-8, lowercase hex. A Codex approval's id is bound the same way (CodexApprovalId).
    internal static string IdentityHash(string identity) =>
        Convert.ToHexStringLower(SHA256.HashData(Wtf8(identity)).AsSpan(0, 8));

    // UTF-8, with half a surrogate pair written as its own three bytes (WTF-8) rather than as U+FFFD:
    // a tool's arguments can hold one (\uD800 in JSON, put back by Controls), and two prompts that
    // differ only there are not the same prompt. Text without one is exactly its UTF-8.
    static byte[] Wtf8(string text)
    {
        var bytes = new List<byte>(text.Length + 16);
        Span<byte> one = stackalloc byte[4];
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var r, out var n) == System.Buffers.OperationStatus.Done)
            {
                var written = r.EncodeToUtf8(one);
                for (var k = 0; k < written; k++) { bytes.Add(one[k]); }
                i += n;
                continue;
            }
            var c = text[i];
            bytes.Add((byte)(0xE0 | (c >> 12)));
            bytes.Add((byte)(0x80 | ((c >> 6) & 0x3F)));
            bytes.Add((byte)(0x80 | (c & 0x3F)));
            i++;
        }
        return [.. bytes];
    }
}
