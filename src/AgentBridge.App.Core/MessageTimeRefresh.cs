namespace AgentBridge.App.Core;

public sealed class MessageTimeRefresh(MessageStamp stamp)
{
    public string Text { get; private set; } = stamp.Text;

    public TimeSpan? Update(MessageClock clock, bool visible)
    {
        if (!visible) { return null; }
        Text = MessageTime.Label(stamp.At, clock.Now, clock.Zone, clock.Culture);
        var age = clock.Now - stamp.At;
        if (age < TimeSpan.Zero) { return TimeSpan.FromTicks(Math.Min(-age.Ticks, TimeSpan.TicksPerMinute)); }
        var period = age < TimeSpan.FromMinutes(1) ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerMinute;
        // Relative labels follow sent-time boundaries; old clock/date labels follow the viewer's
        // minute boundary, including midnight, even when the sent instant has fractional seconds.
        var elapsed = age < TimeSpan.FromHours(1) ? age.Ticks : clock.Now.UtcTicks;
        return TimeSpan.FromTicks(period - elapsed % period);
    }
}
