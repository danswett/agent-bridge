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
        if (Json.Str(data, "remediation") == "sign_in" || type == "authorization" || status == 401)
        {
            return new Attention { Kind = AttentionKind.SignInNeeded, Since = at, Agent = AgentKind.Copilot, Provider = "github", Message = Template(AttentionKind.SignInNeeded, null, status, AgentKind.Copilot) };
        }
        var errorClass = status switch { 403 => ErrorClass.Auth, 429 => ErrorClass.Rate, _ => ErrorClass.Other };
        return new Attention
        {
            Kind = AttentionKind.Error,
            Since = at,
            Agent = AgentKind.Copilot,
            Message = Template(AttentionKind.Error, errorClass, status, AgentKind.Copilot),
            ErrorClass = errorClass,
        };
    }

    // attention.message (#841, Engineering): composed by the host from the structured fields
    // alone, never from the agent's words. Redacting arbitrary error text is a blocklist that
    // never closed (Basic, _-joined keys, a repeated Bearer, camelCase keys in three rounds), and
    // this field reaches every client, HA and the phone. The words follow ui.md §6.13 (#814):
    // "Needs sign-in", "Not authorised", "Rate limited", "Agent error".
    static readonly Dictionary<(AttentionKind, ErrorClass?), string> Templates = new()
    {
        [(AttentionKind.SignInNeeded, null)] = "{0} needs you to sign in again",
        [(AttentionKind.Error, ErrorClass.Auth)] = "{0} was not authorised",
        [(AttentionKind.Error, ErrorClass.Rate)] = "{0} hit a rate limit",
        [(AttentionKind.Error, ErrorClass.Quota)] = "{0} has used up its quota",
        [(AttentionKind.Error, ErrorClass.Model)] = "{0}'s model returned an error",
        [(AttentionKind.Error, ErrorClass.Network)] = "{0} couldn't reach its service",
        [(AttentionKind.Error, ErrorClass.Other)] = "{0} stopped with an error",
        [(AttentionKind.NotResponding, null)] = "{0} isn't responding",
    };

    internal static string Template(AttentionKind kind, ErrorClass? errorClass, int? status, AgentKind agent)
    {
        var words = Templates.GetValueOrDefault((kind, errorClass)) ?? Templates[(AttentionKind.Error, ErrorClass.Other)];
        var name = agent switch { AgentKind.Copilot => "Copilot", AgentKind.Claude => "Claude Code", AgentKind.Codex => "Codex", _ => "The agent" };
        var code = status is >= 100 and <= 599 ? $" ({status})" : "";
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, words, name) + code + ".";
    }

    // Longer than any error message a CLI writes; matching stops there.
    const int Scanned = 4 * 1024;

    // The CLI's turn-error notice as any client may show it in activity.response (#840), which
    // main sent as written; attention never carries agent text (Template). One line, within
    // Limits.AttentionMessage, with what could be a credential, a sign-in code, an email, a path,
    // a link or a request id taken out as far as these patterns reach: a blocklist, so a best
    // effort here and never relied on for a new field (#841). A message the patterns take too
    // long over is not shown at all, never shown as it was.
    internal static string? Shown(string? message)
    {
        if (Text.NonBlank(message) is not { } text) { return null; }
        if (text.Length > Scanned) { text = Cut(text); }
        try
        {
            foreach (var (pattern, mark) in Redactions) { text = pattern.Replace(text, m => mark(m)); }
            text = Whitespace().Replace(text, " ").Trim();
        }
        catch (RegexMatchTimeoutException) { return null; }
        return text.Length == 0 ? null : Limits.KeepStart(text, Limits.AttentionMessage);
    }

    // The words wholly within Scanned, cut at whitespace (#839): a token cut short no longer
    // matches its pattern ("ghp_" and eight characters slipped through), and a cut through an
    // emoji left half of it. One word filling the whole span is none at all.
    static string Cut(string text)
    {
        var end = Scanned;
        while (end > 0 && !char.IsWhiteSpace(text[end])) { end--; }
        return text[..end];
    }

    // In this order: a keyed secret's quoted value may be a link, and taking the link first left
    // its closing quote to open the next secret's; a link holds paths and tokens; and an id's hex
    // would otherwise read as a code.
    static readonly (Regex Pattern, Func<Match, string> Mark)[] Redactions =
    [
        (Keyed(), _ => "[token]"),
        (Link(), _ => "[link]"),
        (Basic(), BasicMark),
        (Prefixed(), _ => "[token]"),
        (Jwt(), _ => "[token]"),
        (Email(), _ => "[email]"),
        (Uuid(), _ => "[id]"),
        (GitHubRequestId(), _ => "[id]"),
        (QuotedPath(), _ => "[path]"),
        (WindowsPath(), _ => "[path]"),
        (UnixPath(), _ => "[path]"),
        (DeviceCode(), _ => "[code]"),
        (Hex(), _ => "[id]"),
        (Opaque(), _ => "[token]"),
    ];

    const int Timeout = 200;

    [GeneratedRegex(@"\b(?:https?|wss?|file)://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Link();

    // "Bearer <x>", "token=<x>", "api_key: <x>", and JSON's "password":"<x>": whatever follows is
    // the secret, a quoted one whole, spaces and escaped quotes included, to its closing quote or
    // the end of the text. A value is never itself a scheme or keyword (bearer, basic, token,
    // digest, authorization): "Bearer Bearer x" and "token=Bearer x" took the second word for the
    // secret and left the real one after it showing, so such words are passed over to it, but
    // only a whole word: a secret that starts with one (token-12345, basic.x9) is still one. A key joined to its prefix by _ or - (db_password,
    // GITHUB_TOKEN, aws_secret_access_key) is one too: a word boundary missed every such key, as
    // _ is a word character. Only a letter or digit before it makes it part of another word.
    [GeneratedRegex(@"(?<![A-Za-z0-9])bearer(?:\s+(?:bearer|basic|token|digest|authorization)\b)*\s+(?!(?:bearer|basic|token|digest|authorization)(?![^\s,;]))[^\s,;]+|(?<![A-Za-z0-9])(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|private[_-]?key|api[_-]?key|credentials?|token|password|passwd|pwd|secret)(?:[_-][A-Za-z0-9]+)*[""']?\s*[:=]\s*(?:(?:bearer|basic|token|digest|authorization)\s+)*(?:""(?:[^""\\]|\\.)*""?|'(?:[^'\\]|\\.)*'?|(?!(?:bearer|basic|token|digest|authorization)(?![^\s,;]))[^\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Keyed();

    // An Authorization header echoed back, whatever its scheme, and a Basic credential on its own:
    // base64 of a short user:password is too short to read as opaque, and can be letters only
    // ("dXNlcjpwYXNz"). A bare one is told from the word ("basic auth failed" stays) by what it
    // decodes to (BasicMark), never by which characters it happens to use.
    [GeneratedRegex(@"\bauthorization[""']?\s*[:=]\s*[""']?(?:basic|bearer|token|digest)(?:\s+(?:bearer|basic|token|digest|authorization)\b)*\s+(?!(?:bearer|basic|token|digest|authorization)(?![^\s,;""']))[^\s,;""']+|\bbasic\s+(?<b64>(?!(?:bearer|basic|token|digest|authorization)(?![A-Za-z0-9+/=]))[A-Za-z0-9+/]+={0,2})(?![A-Za-z0-9+/=])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Basic();

    // A header is always a credential; a bare "basic <x>" when x is base64 of user:password, padded
    // or not, in whatever encoding (Basic's own default was ISO-8859-1): the colon's byte is the
    // same in all of them. The bare word after it is never basic or authorization, which another
    // match must be free to start at.
    static string BasicMark(Match m)
    {
        if (m.Groups["b64"] is not { Success: true } b64) { return "[token]"; }
        var value = b64.Value.TrimEnd('=');
        if (value.Length % 4 == 1) { return m.Value; }
        try { return Array.IndexOf(Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')), (byte)':') >= 0 ? "[token]" : m.Value; }
        catch (FormatException) { return m.Value; }
    }

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

    // A quoted path, whole: a folder name with a space in it is still the machine's. Its closing
    // quote may be past the cut (Cut) or the line's end, and the rest of it is the path still.
    [GeneratedRegex(@"""(?:[A-Za-z]:[\\/]|\\\\|~[\\/]|/)[^""\r\n]*(?:""|$|(?=[\r\n]))|'(?:[A-Za-z]:[\\/]|\\\\|~[\\/]|/)[^'\r\n]*(?:'|$|(?=[\r\n]))", RegexOptions.CultureInvariant, Timeout)]
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
