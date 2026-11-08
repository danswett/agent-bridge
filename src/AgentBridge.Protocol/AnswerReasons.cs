namespace AgentBridge.Protocol;

// Reasons a client says in fixed words of its own, the same in every client: a field's
// OtherUnavailable, and an answer refused or retired for it (AnswerResult.Code). A host sends a
// refusal's code with the sentence as its reason, so a client that knows neither still says why;
// a retired answer's reason is the host's own account of it (FormReset).
public static class AnswerReasons
{
    // A Copilot option list's Other, which the CLI offers and the host cannot deliver yet: a live
    // session takes the words in a box drawn apart from the form, which no key is held to (#908).
    // Temporary: an exception to #876 ("offer Other exactly when the CLI does") until #908 lands.
    public const string OtherNotSendable = "otherNotSendable";

    // On a stale result only: the host reset a part-answered form (one Esc) and the session did
    // not ask the same question again, so the answer was never delivered and its card is out of
    // date (#1123; docs/host-answers.md, "A form left part answered"). Without it, a client read
    // "stale" as answered elsewhere, when nothing of it reached the agent.
    public const string FormReset = "formReset";

    const string OtherNotSendableText = "Your own answer can't be sent from here yet. Pick an option, or answer at the machine.";
    const string FormResetText = "The session moved on from this question. Your answer was not delivered.";

    // How a client heads such a refusal, before the sentence.
    public const string NotSent = "Not sent · ";

    public static bool Known(string? code) => code is OtherNotSendable or FormReset;

    // A code a field may carry as its OtherUnavailable: only OtherNotSendable.
    public static bool OnField(string? code) => code == OtherNotSendable;

    // The sentence for a known code.
    public static string Text(string code) => code switch
    {
        OtherNotSendable => OtherNotSendableText,
        FormReset => FormResetText,
        _ => throw new ArgumentException($"unknown answer reason {code}", nameof(code)),
    };

    // The code a refusal's reason says, if it is one of these sentences; otherwise null. Only
    // OtherNotSendable is a refusal's: FormReset is never derived from a reason.
    public static string? Code(string? reason) => reason == OtherNotSendableText ? OtherNotSendable : null;
}
