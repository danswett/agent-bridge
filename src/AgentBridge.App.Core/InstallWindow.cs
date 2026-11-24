using System.Globalization;

namespace AgentBridge.App.Core;

// When what installs unasked may go (#4468; owner, Nov 24 ~08:15 PT: "Window 02:00-05:00 PT"): between Start and End,
// local time in Zone at the instant asked, so daylight saving moves it with the clock. End is excluded; a window whose
// End is before its Start runs over midnight, and one whose Start is its End is all day. The instant comes from the
// caller's TimeProvider, never the wall clock. A person's press is never held by it (AutoUpdater). Held is the person's
// hold (Settings, per machine): nothing unasked at any hour, whatever the hours are.
public sealed record InstallWindow(TimeOnly Start, TimeOnly End, TimeZoneInfo Zone, bool Held = false)
{
    public static readonly TimeOnly DefaultStart = new(2, 0);
    public static readonly TimeOnly DefaultEnd = new(5, 0);
    public const string DefaultZone = "America/Los_Angeles";

    // A value that can't be read is the default, never a window open at any hour.
    public static InstallWindow Of(AppPreferences prefs) =>
        new(Time(prefs.InstallWindowStart, DefaultStart), Time(prefs.InstallWindowEnd, DefaultEnd), ZoneOf(prefs.InstallWindowZone), prefs.InstallHold);

    public bool IsOpen(DateTimeOffset at)
    {
        if (Held) { return false; }
        if (Start == End) { return true; }
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);
        return Start < End ? local >= Start && local < End : local >= Start || local < End;
    }

    public bool AllDay => !Held && Start == End;

    public string ZoneLabel => IsPacific(Zone) ? "Pacific Time" : Zone.Id;

    // "02:00-05:00 Pacific Time", as the waiting update says it.
    public string Label => Held ? "held" : $"{Start:HH\\:mm}-{End:HH\\:mm} {ZoneLabel}";

    static TimeOnly Time(string? value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : fallback;

    static TimeZoneInfo ZoneOf(string? id) => Find(id) ?? Find(DefaultZone) ?? Find("Pacific Standard Time")
        ?? throw new TimeZoneNotFoundException($"neither {DefaultZone} nor Pacific Standard Time is known here");

    static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) { return null; }
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    static bool IsPacific(TimeZoneInfo zone) => zone.Id is DefaultZone or "Pacific Standard Time"
        || TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) && windows == "Pacific Standard Time";
}
