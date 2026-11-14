namespace AgentBridge.App;

static class ConversationEndPin
{
    public static double? Correction(bool pinned, double offset, double extent, double? pending) =>
        pinned && Math.Abs(extent - offset) > 0.5
        && (pending is not { } asked || Math.Abs(extent - asked) > 0.5) ? extent : null;
}
