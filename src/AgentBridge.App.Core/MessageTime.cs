using System.Globalization;

namespace AgentBridge.App.Core;

// The time on a conversation message (#1619, ui.md 6.4 Time on messages): when it was SENT, as the
// history records it, never when it was received or drawn. Everything takes the clock and the zone
// as arguments, so a test names them and no machine's own settings decide a result.
public static class MessageTime
{
    // Under this a message is "just now"; from it to a minute, "Ns ago".
    public const int JustNowSeconds = 10;

    // The relative or clock form shown beside a message; "" when no time is known, never a guess.
    public static string Label(DateTimeOffset? at, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (at is not { } sent) { return ""; }
        var seconds = (long)Math.Floor((now - sent).TotalSeconds);
        // A time ahead of this machine's clock is a skewed clock, not the future.
        if (seconds < JustNowSeconds) { return "just now"; }
        if (seconds < 60) { return $"{seconds}s ago"; }
        if (seconds < 3600) { return $"{seconds / 60} min ago"; }
        var local = TimeZoneInfo.ConvertTime(sent, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        var clock = local.ToString("t", culture);
        if (local.Date == today.Date) { return clock; }
        var day = local.Year == today.Year ? local.ToString("MMM d", culture) : local.ToString("MMM d, yyyy", culture);
        return $"{day}, {clock}";
    }

    // The full local date and time with seconds, for the reveal and the accessible name:
    // "Mon Nov 9, 2026, 7:41:07 PM UTC-08:00". The offset, not a zone's initials, which a
    // Windows zone name does not give reliably ("Central Europe Standard Time" is not CEST).
    public static string Exact(DateTimeOffset at, TimeZoneInfo zone, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"{local.ToString("ddd MMM d, yyyy, ", culture)}{local.ToString("T", culture)} UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }
}

// The clock, zone and culture a message's time is read against, named so a test fixes all three.
public sealed record MessageClock(DateTimeOffset Now, TimeZoneInfo Zone, CultureInfo Culture)
{
    public static MessageClock Local => new(DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture);

    public MessageStamp Stamp(DateTimeOffset at) => new(MessageTime.Label(at, Now, Zone, Culture), MessageTime.Exact(at, Zone, Culture));
}
