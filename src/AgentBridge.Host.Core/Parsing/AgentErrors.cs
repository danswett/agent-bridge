using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// session.attention from an agent's own error records (#800). Read from their structured fields,
// never their wording: the words are the agent's to change, and a phrase that stopped matching
// would leave a signed-out session reading idle again, which is the bug #800 is. An agent gets a
// detector only once a real, redacted sample of its records has been captured.
//
// Claude Code and Codex: TODO(#800). No sample of either's sign-in failure or turn-ending error
// was on VM1 (#800, 2026-11-04), so neither is detected yet, and no string is guessed for them.
internal static class AgentErrors
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
    // this field reaches every client, HA and the phone. The CLI's turn-error notice is shown as
    // the same sentence (CopilotParser.MainText, #846). The words follow ui.md §6.13 (#814):
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
}
