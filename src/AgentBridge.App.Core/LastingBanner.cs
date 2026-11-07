namespace AgentBridge.App.Core;

// The banner rule (ui.md §7, #1064): a banner is for a state that lasts. It shows only once its
// state has stood for Grace, so one that comes and goes inside the grace is never drawn, and once
// shown it stays at least Min, so a state that flickers away cannot make the banner flash. Pure:
// the caller says what is wanted and when, on each change and on its own tick.
public sealed class LastingBanner
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Min = TimeSpan.FromSeconds(10);

    DateTimeOffset? wantedSince;
    DateTimeOffset? shownAt;

    public bool Shown => shownAt is not null;

    // Whether the banner is shown now, given whether its state holds at `now`.
    public bool Update(bool wanted, DateTimeOffset now)
    {
        if (wanted)
        {
            wantedSince ??= now;
            if (shownAt is null && now - wantedSince >= Grace) { shownAt = now; }
        }
        else
        {
            wantedSince = null;
            if (shownAt is { } at && now - at >= Min) { shownAt = null; }
        }
        return Shown;
    }
}
