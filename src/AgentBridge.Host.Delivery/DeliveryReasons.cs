namespace AgentBridge.Host.Delivery;

// Words a reply or answer reason uses for what a person cannot act on (#995).
public static class DeliveryReasons
{
    // In place of an exception's text, which goes to the host's log only (#802): the person's
    // receipt already says where to look, and the exception's words are the host's internals.
    public const string CouldNotDeliver = "the machine couldn't deliver it";

    // What an answer sent again says when the first, typed, could not be confirmed and the
    // session has recorded nothing since (#1093). Nothing is typed again, so it is neither
    // delivered nor refused: the person looks at the session before sending it once more. The
    // wording is for Creative to settle; it is kept here alone.
    public const string AnswerNotConfirmed = "the bridge could not confirm this answer reached the session; check the session before sending it again";

    // An injector's detail as a reply's or an answer's reason: a worker that did not start is
    // CouldNotDeliver, any other detail as it is.
    public static string Of(string detail) => detail == Windows.WorkerInjector.NotStarted ? CouldNotDeliver : detail;
}
