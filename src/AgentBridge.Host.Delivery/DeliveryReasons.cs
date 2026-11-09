namespace AgentBridge.Host.Delivery;

// Words a reply or answer reason uses for what a person cannot act on (#995).
public static class DeliveryReasons
{
    // In place of an exception's text, which goes to the host's log only (#802): the person's
    // receipt already says where to look, and the exception's words are the host's internals.
    public const string CouldNotDeliver = "the machine couldn't deliver it";

    // What an answer sent again says when the first, typed, could not be confirmed and the
    // session has recorded nothing since (#1093). Nothing is typed again, so it is neither
    // delivered nor refused. It is shown inside the clients' own unconfirmed line ("... Check
    // the conversation. (reason)"), so it says only that this send typed nothing, and why.
    // Wording settled by Creative; kept here alone.
    public const string AnswerNotConfirmed = "nothing was typed again: this repeats an answer already typed once";

    // An answer refused before any key because the host could not keep its words until the
    // session confirms it (#1159 slice 0): as many unconfirmed answers held as it keeps, words
    // whose delete is still pending among them, or a private folder anyone else can read, or no
    // key for the hash the ledger keeps. Nothing is evicted to make room, so it says to answer at
    // the machine.
    public const string CannotHoldAnswer = "the machine can't keep this answer safely until it's confirmed, so nothing was typed; answer at the machine";

    // An injector's detail as a reply's or an answer's reason: a worker that did not start is
    // CouldNotDeliver, any other detail as it is.
    public static string Of(string detail) => detail == Windows.WorkerInjector.NotStarted ? CouldNotDeliver : detail;
}
