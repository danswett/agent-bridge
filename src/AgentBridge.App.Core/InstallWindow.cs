using System.Globalization;

namespace AgentBridge.App.Core;

// When what installs unasked may go (#4468; owner, Nov 24 ~08:15 PT: "Window 02:00-05:00 PT"): between Start and End,
// local time in Zone at the instant asked, so daylight saving moves it with the clock. End is excluded; a window whose
// End is before its Start runs over midnight, and one whose Start is its End is all day, which only both times set and
// readable can give. The instant comes from the caller's TimeProvider, never the wall clock. A person's press is never
// held by it (AutoUpdater). Held is the person's hold (Settings, per machine): nothing unasked at any hour, whatever the
// hours are. With no time zone this machine knows, it is closed and says why (Problem), never a throw.
public sealed record InstallWindow(TimeOnly Start, TimeOnly End, TimeZoneInfo? Zone, string? Problem = null, bool Held = false)
{
    public static readonly TimeOnly DefaultStart = new(2, 0);
    public static readonly TimeOnly DefaultEnd = new(5, 0);
    public const string DefaultZone = "America/Los_Angeles";

    // Both times, or neither: one unset or unreadable is the whole default pair, since falling back one time at a time can
    // make Start equal End, which is all day (#4509's review). An unreadable zone is the default zone. Find is for tests.
    public static InstallWindow Of(AppPreferences prefs, Func<string, TimeZoneInfo?>? find = null)
    {
        find ??= FindZone;
        var (start, end) = Time(prefs.InstallWindowStart) is { } s && Time(prefs.InstallWindowEnd) is { } e ? (s, e) : (DefaultStart, DefaultEnd);
        var zone = (string.IsNullOrWhiteSpace(prefs.InstallWindowZone) ? null : find(prefs.InstallWindowZone)) ?? find(DefaultZone) ?? find("Pacific Standard Time");
        var window = zone is null ? new InstallWindow(start, end, null, "no time zone for it is known on this machine") : new InstallWindow(start, end, zone);
        return window with { Held = prefs.InstallHold };
    }

    public bool IsOpen(DateTimeOffset at)
    {
        if (Held || Zone is null) { return false; }
        if (Start == End) { return true; }
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);
        return Start < End ? local >= Start && local < End : local >= Start || local < End;
    }

    // Both set and equal (Of gives no other way to it): all day. A hold is not.
    public bool AllDay => !Held && Start == End;

    public string ZoneLabel => Zone is null || IsPacific(Zone) ? "Pacific Time" : Zone.Id;

    // "02:00-05:00 Pacific Time", as the waiting update says it; closed, it says why; held, it says so.
    public string Label => Held ? "held" : $"{Start:HH\\:mm}-{End:HH\\:mm} {(Zone is null ? $"Pacific Time, closed: {Problem}" : ZoneLabel)}";

    static TimeOnly? Time(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    static bool IsPacific(TimeZoneInfo zone) => zone.Id is DefaultZone or "Pacific Standard Time"
        || TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) && windows == "Pacific Standard Time";
}
