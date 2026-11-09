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
// Whether a Copilot session.error (the main agent's) ends its turn: every one but a model
// call's, retried within the turn, and a hook's (cli-main.js 1.0.105). The parser ends the turn
// and its questions on it (SessionState.FailTurn); the answer path's watch of what is waiting
// does the same on the same records (CopilotPermissionWatch).
public static class CopilotTurnErrors
{
    public static bool EndsTurn(JsonElement data) => Json.Str(data, "errorType") is not ("model_call" or "hook");
}

internal static class AgentErrors
{
    // Copilot CLI's session.error. VM1's three sign-in failures (#800, 2026-11-04 15:06, 16:29
    // and 16:45 PT) read { errorType: "authorization", statusCode: 401, remediation: "sign_in" };
    // any one of the three is taken as sign-in, so a CLI that drops one still reads. The CLI
    // ends the turn on every session.error but a model call's (retried within the turn) and a
    // hook's (cli-main.js 1.0.105), and those are null here: they end nothing.
    public static Attention? Copilot(JsonElement data, DateTimeOffset at)
    {
        if (!CopilotTurnErrors.EndsTurn(data)) { return null; }
        var type = Json.Str(data, "errorType");
        var status = Json.Int(data, "statusCode");
        if (Json.Str(data, "remediation") == "sign_in" || type == "authorization" || status == 401)
        {
            return new Attention { Kind = AttentionKind.SignInNeeded, Since = at, Agent = AgentKind.Copilot, Provider = "github", Message = Template(AttentionKind.SignInNeeded, null, status, AgentKind.Copilot) };
        }
        var errorClass = status switch { 403 => ErrorClass.Auth, 429 => ErrorClass.Rate, _ => ErrorClass.Other };
        // #687: the CLI's own session host stopped acknowledging events (errorType "query", then
        // "execution" on every later session.send, 1.0.105 and later; seen again 2026-11-09 on
        // several sessions). It is the CLI's fault and never recovers until the session is
        // resumed, and a background agent finishing meanwhile can lose its result. The error has
        // no structured field to tell it from any other, so only the classification reads its
        // words; the notice shown is ours, so a session that can't be written to no longer reads
        // as a generic "stopped with an error".
        if (IsHostWedge(data))
        {
            return new Attention { Kind = AttentionKind.Error, Since = at, Agent = AgentKind.Copilot, Message = HostWedgeMessage, ErrorClass = ErrorClass.Other };
        }
        return new Attention
        {
            Kind = AttentionKind.Error,
            Since = at,
            Agent = AgentKind.Copilot,
            Message = Template(AttentionKind.Error, errorClass, status, AgentKind.Copilot),
            ErrorClass = errorClass,
        };
    }

    internal const string HostWedgeMessage = "Copilot's session stopped taking messages. Resume it to recover; a background agent's result may be missing.";

    internal static bool IsHostWedge(JsonElement data) =>
        Json.Str(data, "message") is { } m && m.Contains("session event delivery failed", StringComparison.Ordinal)
        && (m.Contains("did not acknowledge", StringComparison.Ordinal) || m.Contains("did not complete", StringComparison.Ordinal));

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
        // The name titles use (Creative 6675007095, ui.md §6.13): "Claude", never the product's
        // "Claude Code", so a notice and the session's line read the same.
        var name = agent switch { AgentKind.Copilot => "Copilot", AgentKind.Claude => "Claude", AgentKind.Codex => "Codex", _ => "The agent" };
        var code = status is >= 100 and <= 599 ? $" ({status})" : "";
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, words, name) + code + ".";
    }
}
