namespace AgentBridge.Protocol;

// Reasons a client says in fixed words of its own, the same in every client: a field's
// OtherUnavailable, and an answer refused for it (AnswerResult.Code). A host sends the code with
// the sentence as its reason, so a client that knows neither still says why.
public static class AnswerReasons
{
    // A Copilot option list's Other, which the CLI offers and the host cannot deliver yet: a live
    // session takes the words in a box drawn apart from the form, which no key is held to (#908).
    // Temporary: an exception to #876 ("offer Other exactly when the CLI does") until #908 lands.
    public const string OtherNotSendable = "otherNotSendable";

    const string OtherNotSendableText = "Your own answer can't be sent from here yet. Pick an option, or answer at the machine.";

    // How a client heads such a refusal, before the sentence.
    public const string NotSent = "Not sent · ";

    public static bool Known(string? code) => code == OtherNotSendable;

    // The sentence for a known code.
    public static string Text(string code) => code == OtherNotSendable ? OtherNotSendableText : throw new ArgumentException($"unknown answer reason {code}", nameof(code));

    // The code a refusal's reason says, if it is one of these sentences; otherwise null.
    public static string? Code(string? reason) => reason == OtherNotSendableText ? OtherNotSendable : null;
}
