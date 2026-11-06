namespace AgentBridge.Host.Delivery;

// Words a reply or answer reason uses for what a person cannot act on (#995).
public static class DeliveryReasons
{
    // In place of an exception's text, which goes to the host's log only (#802): the person's
    // receipt already says where to look, and the exception's words are the host's internals.
    public const string CouldNotDeliver = "the machine couldn't deliver it";

    // An injector's detail as a reply's or an answer's reason: a worker that did not start is
    // CouldNotDeliver, any other detail as it is.
    public static string Of(string detail) => detail == Windows.WorkerInjector.NotStarted ? CouldNotDeliver : detail;
}
