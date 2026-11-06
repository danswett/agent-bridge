namespace AgentBridge.Host.Delivery;

// What a person is told when a reply is held because the session is still starting (#1018). The
// words are placeholders until Creative's wording lands (#1018, the held-state wording): they are
// all here so that a swap is one line each. A held reply says what to do on the phone, never
// "at the machine", except where only the machine can answer what the window asks.
public static class FirstReplyWords
{
    // The bridge cannot tell yet: the window is not drawn, or cannot be read. Nothing is typed.
    public const string WaitingToStart = "Waiting to start. Send again in a moment";

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
