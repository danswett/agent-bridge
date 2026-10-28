using System.Text;

namespace AgentBridge.Protocol;

// session.stop: ends a session, as agent-ha-bridge's End session button does. Request is the
// client's UUID for this press, with session.answer's semantics. Confirm is the token a
// first press of a session that is not idle was given back, sent with the second press.
public sealed record StopRequest
{
    public required string Id { get; init; }
    public required string Request { get; init; }
    public string? Confirm { get; init; }
}

// Confirm and ConfirmSeconds come with the confirm outcome only; Forced with ended, when the
// host had to terminate a process that did not exit on its own.
public sealed record StopResult(StopOutcome Outcome, string? Reason = null)
{
    public string? Confirm { get; init; }
    public int? ConfirmSeconds { get; init; }
    public bool? Forced { get; init; }
}

// An outcome a client does not know reads as Failed: never as Ended, and never as Confirm,
// which would invite a press that confirms nothing.
[UnknownAs(Failed)]
public enum StopOutcome { Ended, Confirm, Stale, Busy, Refused, Failed }

// Whether a session.stop can be acted on at all. Whether the session can be ended now (its
// process, the platform, agent-ha-bridge) is the host's.
public static class Stops
{
    // How long a first press waits for the second, as agent-ha-bridge's StopConfirmSeconds.
    public const int ConfirmSeconds = 10;

    // A token is what a host issued: base64url, nothing a client composes.
    public const int MaxConfirmLength = 64;

    public static string? Check(StopRequest stop)
    {
        if (string.IsNullOrEmpty(stop.Id) || Encoding.UTF8.GetByteCount(stop.Id) > 256) { return "the session id is missing or too long"; }
        if (stop.Confirm is { } c && (c.Length == 0 || c.Length > MaxConfirmLength || !c.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_')))
        {
            return "the confirmation is not one this host issued";
        }
        return null;
    }
}
