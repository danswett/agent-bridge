using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// session.attention from an agent's own error records (#800). Read from their structured fields,
// never their wording: the words are the agent's to change, and a phrase that stopped matching
// would leave a signed-out session reading idle again, which is the bug #800 is. An agent gets a
// detector only once a real, redacted sample of its records has been captured.
//
// Claude Code and Codex: TODO(#800). No sample of either's sign-in failure or turn-ending error
// was on VM1 (#800, 2026-11-04), so neither is detected yet, and no string is guessed for them.
internal static partial class AgentErrors
{
    // Copilot CLI's session.error. VM1's three sign-in failures (#800, 2026-11-04 15:06, 16:29
    // and 16:45 PT) read { errorType: "authorization", statusCode: 401, remediation: "sign_in" };
    // any one of the three is taken as sign-in, so a CLI that drops one still reads. The CLI
    // ends the turn on every session.error but a model call's (retried within the turn) and a
    // hook's (cli-main.js 1.0.105), and those are null here: they end nothing.
    public static Attention? Copilot(JsonElement data, DateTimeOffset at)
    {
        var type = Json.Str(data, "errorType");
        if (type is "model_call" or "hook") { return null; }
        var status = Json.Int(data, "statusCode");
        var message = Shown(Json.Str(data, "message"));
        if (Json.Str(data, "remediation") == "sign_in" || type == "authorization" || status == 401)
        {
            return new Attention { Kind = AttentionKind.SignInNeeded, Since = at, Agent = AgentKind.Copilot, Provider = "github", Message = message };
        }
        return new Attention
        {
            Kind = AttentionKind.Error,
            Since = at,
            Agent = AgentKind.Copilot,
            Message = message,
            ErrorClass = status switch { 403 => ErrorClass.Auth, 429 => ErrorClass.Rate, _ => ErrorClass.Other },
        };
    }

    // Longer than any error message a CLI writes; matching stops there.
    const int Scanned = 4 * 1024;

    // The agent's words as any client may show them: one line, within Limits.AttentionMessage,
    // with nothing in it that could be a credential, a sign-in code, an account's email, a path
    // on the machine, a link or a request id. The message reaches the phone, the app and HA alike
    // (rule 5: no token in any of them), so a link is never sent either: the only
    // link a sign-in shows is the allowlisted one the device flow relays. A message the patterns
    // take too long over is not shown at all, never shown as it was.
    internal static string? Shown(string? message)
    {
        if (Text.NonBlank(message) is not { } text) { return null; }
        if (text.Length > Scanned) { text = text[..Scanned]; }
        try
        {
            foreach (var (pattern, mark) in Redactions) { text = pattern.Replace(text, mark); }
            text = Whitespace().Replace(text, " ").Trim();
        }
        catch (RegexMatchTimeoutException) { return null; }
        return text.Length == 0 ? null : Limits.KeepStart(text, Limits.AttentionMessage);
    }

    // In this order: a keyed secret's quoted value may be a link, and taking the link first left
    // its closing quote to open the next secret's; a link holds paths and tokens; and an id's hex
    // would otherwise read as a code.
    static readonly (Regex Pattern, string Mark)[] Redactions =
    [
        (Keyed(), "[token]"),
        (Link(), "[link]"),
        (Basic(), "[token]"),
        (Prefixed(), "[token]"),
        (Jwt(), "[token]"),
        (Email(), "[email]"),
        (Uuid(), "[id]"),
        (GitHubRequestId(), "[id]"),
        (QuotedPath(), "[path]"),
        (WindowsPath(), "[path]"),
        (UnixPath(), "[path]"),
        (DeviceCode(), "[code]"),
        (Hex(), "[id]"),
        (Opaque(), "[token]"),
    ];

    const int Timeout = 200;

    [GeneratedRegex(@"\b(?:https?|wss?|file)://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Link();

    // "Bearer <x>", "token=<x>", "api_key: <x>", and JSON's "password":"<x>": whatever follows is
    // the secret, a quoted one whole, spaces and escaped quotes included, to its closing quote or
    // the end of the text.
    [GeneratedRegex(@"\bbearer\s+[^\s,;]+|\b(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|token|password|passwd|secret|api[_-]?key)[""']?\s*[:=]\s*(?:""(?:[^""\\]|\\.)*""?|'(?:[^'\\]|\\.)*'?|[^\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Keyed();

    // An Authorization header echoed back, whatever its scheme, and a Basic credential on its own:
    // base64 of a short user:password is too short to read as opaque. A digit, +, / or = tells it
    // from the word ("basic auth failed" stays), unless the header names it.
    [GeneratedRegex(@"\bauthorization[""']?\s*[:=]\s*[""']?(?:basic|bearer|token|digest)\s+[^\s,;""']+|\bbasic\s+(?=[A-Za-z0-9+/]*[0-9+/=])[A-Za-z0-9+/]+={0,2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Basic();

    // GitHub's, OpenAI's and Anthropic's (sk-, sk-ant-) and Slack's token forms.
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,}|sk-[A-Za-z0-9_-]{16,}|xox[abprs]-[A-Za-z0-9-]{10,})", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Prefixed();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Uuid();

    // x-github-request-id's colon-separated hex ("C3F4:2B8A1:9F3E0C:A1B2C3:672A1B3C"); a clock
    // time's two-digit groups are too short to be one.
    [GeneratedRegex(@"\b[0-9A-Fa-f]{3,}(?::[0-9A-Fa-f]{3,}){3,}\b", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex GitHubRequestId();

    // A quoted path, whole: a folder name with a space in it is still the machine's.
    [GeneratedRegex(@"""(?:[A-Za-z]:[\\/]|\\\\|~[\\/]|/)[^""\r\n]*""|'(?:[A-Za-z]:[\\/]|\\\\|~[\\/]|/)[^'\r\n]*'", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex QuotedPath();

    [GeneratedRegex(@"\b[A-Za-z]:[\\/][^\s""'<>|]*|\\\\[^\s""'<>|]+", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex WindowsPath();

    // A home-relative path, or any absolute one of two parts or more.
    [GeneratedRegex(@"(?<![\w.])~[\\/][^\s""'<>|]*|(?<![\w.:/\\])/[^\s/""'<>|]+/[^\s""'<>|]*", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex UnixPath();

    // A device flow's user code (ABCD-1234), which only the person may enter.
    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex DeviceCode();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{16,}\b", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Hex();

    // Any long run of token characters with a digit in it: no word of a message looks like that.
    [GeneratedRegex(@"\b(?=[A-Za-z_-]*\d)[A-Za-z0-9_-]{24,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Opaque();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Whitespace();
}
