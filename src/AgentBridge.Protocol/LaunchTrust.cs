// Bridge Protocol v1, folder trust: a launched agent asking whether to trust its folder, shown to
// the account's devices and, where the machine allows it, answered by a person from one of them.
// The contract is docs/protocol/v1-launch.md ("Folder trust"); these records mirror it and
// serialize to exactly that JSON with ProtocolJson.Options.
namespace AgentBridge.Protocol;

public static class TrustProtocol
{
    // Offered on a channel through the relay by a host that takes launch.trust.answer. Whether a
    // given question can be answered is the question's own answerable, which says why not.
    public const string Capability = "launch.trust";
    public const string Answer = "launch.trust.answer";

    public const string Trust = "trust";
    public const string Quit = "quit";

    // The signed command's action names the choice, so a command signed for one cannot be sent
    // as the other.
    public static string Action(string choice) => $"launch.trust:{choice}";

    // The agent's own words, as read from its window, are cut at this.
    public const int MaxText = 2048;
}

// A pending folder-trust question: launch.options' trust, and a launch result's trust.
public sealed record TrustQuestion
{
    // Names this question while it waits; answered by this id.
    public required string Id { get; init; }
    // The launcher that started the agent (copilot, agency, claude, codex), its agent and label.
    public required string Agent { get; init; }
    public required AgentKind Kind { get; init; }
    public required string Label { get; init; }
    // The workspace the launch named, and the exact folder the agent was started in.
    public string? Workspace { get; init; }
    public required string Folder { get; init; }
    // When trusting applies to an enclosing repository instead (Codex): that folder, and the
    // agent's note saying so.
    public string? Root { get; init; }
    public string? RootNote { get; init; }
    // The agent's own warning, as its window shows it. Null when it could not be read.
    public string? Warning { get; init; }
    public required DateTimeOffset AskedAt { get; init; }
    // Whether launch.trust.answer can answer it now; when not, why not and how to change that.
    public required bool Answerable { get; init; }
    public string? AtMachine { get; init; }
}

// launch.trust.answer: the question, the choice, and a command a person's device signed for
// exactly that choice on this machine (action launch.trust:<choice>, target.session the question).
public sealed record TrustAnswerRequest
{
    public required string Question { get; init; }
    public required string Choice { get; init; }
    public required SignedCommand Command { get; init; }
}

public sealed record TrustAnswerResult(TrustAnswerOutcome Outcome, string? Reason = null)
{
    // With started: the session, now registered.
    public string? Session { get; init; }
}

// started: trusted, and the session was seen to start. declined: quit, and the agent was seen to
// close. unconfirmed: something may have been typed, but neither was seen; answer at the machine.
// refused: nothing was typed. An outcome a client does not know reads as unconfirmed.
[UnknownAs(Unconfirmed)]
public enum TrustAnswerOutcome { Started, Declined, Unconfirmed, Refused }
