namespace AgentBridge.App.Core;

public readonly record struct LatestReturn(bool Pinned, bool Returning)
{
    public LatestReturn Scrolled(bool intermediate, double offset, double extent)
    {
        if (!Returning) { return this; }
        // Growth can move the end before the return animation's final frame.
        var arrived = extent - offset <= 0.5;
        return new(Pinned || arrived, !arrived && intermediate);
    }
}
