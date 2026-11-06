namespace AgentBridge.Host.Delivery;

// What a person is told when a reply is held because the session is still starting (#1018). The
// clients show a reason as "The machine refused this reply: {reason}.", so a reason starts lower
// case, has no full stop, and says "the machine" rather than "host". StillStarting and
// StillStartingLaunched are Creative's words (#1018 comment 6695090764). The rest are
// PLACEHOLDERS until Creative words them (a question drawn in the window, or a launch never seen
// ready), so a swap is one line each.
public static class FirstReplyWords
{
    // The bridge cannot tell yet: the window is not drawn, or cannot be read. Nothing is typed.
    public const string WaitingToStart = "the session is still starting, so it can't take a reply yet. Send it again in a moment";

    // A launch the bridge follows, not yet seen ready. It names Needs you only where the session
    // will appear there with a way to answer (remote folder trust on, a device approved): a reason
    // never promises a place that isn't coming.
    public static string StillStarting(string label, bool needsYouWhereTrustAsks) => needsYouWhereTrustAsks
        ? $"{label} is still starting, so it can't take a reply yet. If it asks whether to trust its folder, that shows in Needs you. Otherwise, send it again in a moment"
        : $"{label} is still starting, so it can't take a reply yet. Send it again in a moment";

    // Followed until the bridge gave up, and never seen ready. Only a prompt typed there, or the
    // agent exiting, releases it (LaunchService.HeldForReply), so sending again would not help.
    public static string NotSeenReady(string label) => $"{label} never showed it was ready for a reply and may be asking a question in its window. Answer it at the machine, or start a new session";

    // The folder-trust question is showing. Answered from the phone (#947) where the bridge allows
    // that for this session, else only at the machine.
    public static string AskingTrust(string label, bool answerableFromHere) => answerableFromHere
        ? $"{label} is asking whether to trust its folder. Answer that first, then send this again"
        : $"{label} is asking whether to trust its folder, which a reply would answer; answer it at the machine";

    // Any other question the window asks at startup (hooks, Bypass Permissions): only the machine answers it.
    public static string AskingAtMachine(string label) =>
        $"{label} is asking a question in its window, which a reply would answer; answer it at the machine";
}
