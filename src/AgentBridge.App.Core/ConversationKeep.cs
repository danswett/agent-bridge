namespace AgentBridge.App.Core;

// Keeping the reading place when older history is drawn above it (#4243).
public static class ConversationKeep
{
    /// <summary>
    /// Where the view is for working out a prepend's correction: where an earlier correction is still taking it (<paramref name="pendingTarget"/>) when that is AHEAD of the view, else where it is now.
    /// Counting from the offset an animation has got to puts the new target below the one already in flight, the "only move forward" test then does nothing, and that prepend's height is lost for good.
    /// A pending target behind the view is not a place the view is going to keep, and one that is not a finite number is not a place at all: both leave the offset (as it was).
    /// </summary>
    public static double PrependBaseline(double? pendingTarget, double offset) => pendingTarget is { } p && double.IsFinite(p) && p > offset ? p : offset;
}
