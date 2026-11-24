namespace AgentBridge.Host.Delivery;

// What a person is told when a reply is held because the session is still starting (#1018). The
// clients show a reason as "The machine refused this reply: {reason}.", so a reason starts lower
// case, has no full stop, and says "the machine" rather than "host". All of the words are
// Creative's (#1018, PR #1048 comment 6695650655), kept here so a swap is one line each.
public static class FirstReplyWords
{
    // The bridge cannot tell yet, and says which check failed (#3538), so a sender or a person can act. Nothing is typed.
    // The window cannot be read:
    public const string ScreenUnreadable = "the machine could not read the session's screen to tell it is ready for a reply. Send it again in a moment";

    // The window is read but the prompt's box and footer are not drawn:
    public const string NoInputBox = "the machine does not see the session's input box yet, so it can't take a reply. Send it again in a moment";

    // A launch the bridge follows, not yet seen ready. It names Needs you only where the session
    // will appear there with a way to answer (remote folder trust on, a device approved): a reason
    // never promises a place that isn't coming.
    public static string StillStarting(string label, bool needsYouWhereTrustAsks) => needsYouWhereTrustAsks
        ? $"{label} is still starting, so it can't take a reply yet. If it asks whether to trust its folder, that shows in Needs you. Otherwise, send it again in a moment"
        : $"{label} is still starting, so it can't take a reply yet. Send it again in a moment";

    // Followed until the bridge gave up, and never seen ready. Its own activity after launch,
    // or the agent exiting, releases it (LaunchService.HeldForReply).
    public static string NotSeenReady(string label) => $"{label} never showed it was ready for a reply and may be asking something in its window. Answer it on that machine, or start a new session";

    // The session is working (its own transcript shows activity since this launch) and has a question or permission
    // request open, so a reply could answer it: not "starting" (#3538), which a working session was told for minutes.
    public static string WaitingForAnAnswer(string label) =>
        $"{label} is waiting for an answer to a question or permission request, so it can't take a reply yet. Answer that first, then send this again";

    // The folder-trust question is showing. Answered from the phone (#947) where the bridge allows
    // that for this session, else only at the machine.
    public static string AskingTrust(string label, bool answerableFromHere) => answerableFromHere
        ? $"{label} is asking whether to trust its folder. Answer that first, then send this again"
        : $"{label} is asking whether to trust its folder, and a reply would answer it. Answer it on that machine";

    // Any other question the window asks at startup (hooks, Bypass Permissions): only the machine answers it.
    public static string AskingAtMachine(string label) =>
        $"{label} is asking a question in its window, and a reply would answer it. Answer it on that machine";
}
