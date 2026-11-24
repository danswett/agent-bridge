using AgentBridge.App.Core;
using Xunit;

// #4468 (owner, Nov 24 ~08:15 PT: "Window 02:00-05:00 PT"): what installs unasked goes only inside the update window,
// 02:00-05:00 Pacific Time by default, read in that zone at the given instant so daylight saving moves it with the clock.
public sealed class InstallWindowTests
{
    static readonly InstallWindow Default = InstallWindow.Of(new AppPreferences());

    static DateTimeOffset Utc(string at) => DateTimeOffset.Parse(at + "Z", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    // A standard-time day (PST, UTC-8): 02:00-05:00 is 10:00-13:00 UTC, the start in and the end out.
    [InlineData("2026-11-24T09:59:59", false)]
    [InlineData("2026-11-24T10:00:00", true)]
    [InlineData("2026-11-24T12:59:59", true)]
    [InlineData("2026-11-24T13:00:00", false)]
    // A summer day (PDT, UTC-7): the same hours an hour earlier in UTC.
    [InlineData("2026-07-01T08:59:59", false)]
    [InlineData("2026-07-01T09:00:00", true)]
    [InlineData("2026-07-01T11:59:59", true)]
    [InlineData("2026-07-01T12:00:00", false)]
    // Spring forward, 2026-03-08: 02:00 PST becomes 03:00 PDT at 10:00 UTC, so 02:xx never happens and the window
    // opens at 03:00 PDT; it still ends at 05:00 PDT.
    [InlineData("2026-03-08T09:30:00", false)]
    [InlineData("2026-03-08T10:00:00", true)]
    [InlineData("2026-03-08T11:59:59", true)]
    [InlineData("2026-03-08T12:00:00", false)]
    // Fall back, 2026-11-01: 01:00-02:00 happens twice (PDT, then PST from 09:00 UTC); neither is in the window, which
    // opens at 02:00 PST.
    [InlineData("2026-11-01T08:30:00", false)]
    [InlineData("2026-11-01T09:30:00", false)]
    [InlineData("2026-11-01T10:00:00", true)]
    [InlineData("2026-11-01T12:59:59", true)]
    [InlineData("2026-11-01T13:00:00", false)]
    public void The_default_window_is_02_to_05_Pacific_Time_including_daylight_saving_days(string utc, bool open) =>
        Assert.Equal(open, Default.IsOpen(Utc(utc)));

    [Fact]
    public void The_default_says_its_hours_and_zone() => Assert.Equal("02:00-05:00 Pacific Time", Default.Label);

    [Theory]
    [InlineData("2:00", "05:00", "America/Los_Angeles")]
    [InlineData("02:00", "25:00", "America/Los_Angeles")]
    [InlineData("02:00", "05:00", "Nowhere/Never")]
    public void A_value_that_cannot_be_read_is_the_default_never_a_window_open_at_any_hour(string start, string end, string zone)
    {
        var window = InstallWindow.Of(new AppPreferences { InstallWindowStart = start, InstallWindowEnd = end, InstallWindowZone = zone });
        Assert.Equal(Default.IsOpen(Utc("2026-11-24T11:00:00")), window.IsOpen(Utc("2026-11-24T11:00:00")));
        Assert.False(window.IsOpen(Utc("2026-11-24T20:00:00")));
    }

    static double HoursOpen(InstallWindow window) =>
        Enumerable.Range(0, 24 * 60).Count(m => window.IsOpen(Utc("2026-11-24T00:00:00").AddMinutes(m))) / 60.0;

    // #4509's review (P1): one time unset or unreadable is the WHOLE default pair. Falling back per time could make the start
    // equal the end, which is all day: the reader's probes, in hours open a day.
    [Theory]
    [InlineData("05:00", null)]
    [InlineData("05:00", "25:00")]
    [InlineData("garbage", "02:00")]
    [InlineData(null, "02:00")]
    [InlineData("02:00", null)]
    public void A_half_set_or_unreadable_window_is_the_whole_default_never_open_all_day(string? start, string? end)
    {
        var window = InstallWindow.Of(new AppPreferences { InstallWindowStart = start, InstallWindowEnd = end });

        Assert.Equal(3, HoursOpen(window));
        Assert.Equal("02:00-05:00 Pacific Time", window.Label);
    }

    // Both times set and readable are used as they are: a window over midnight, and the same start and end, all day.
    [Theory]
    [InlineData("05:00", "02:00", 21)]
    [InlineData("00:00", "00:00", 24)]
    public void Both_times_set_and_readable_are_used_as_they_are(string start, string end, double hours) =>
        Assert.Equal(hours, HoursOpen(InstallWindow.Of(new AppPreferences { InstallWindowStart = start, InstallWindowEnd = end })));
    // #4509's review (P3): a machine that knows no time zone for the window gets it closed, saying why, never a throw
    // from AutoUpdater's state.
    [Fact]
    public void With_no_time_zone_known_the_window_is_closed_and_says_why()
    {
        var window = InstallWindow.Of(new AppPreferences(), _ => null);

        Assert.Equal(0, HoursOpen(window));
        Assert.Equal("02:00-05:00 Pacific Time, closed: no time zone for it is known on this machine", window.Label);
    }
    [Fact]
    public void The_same_start_and_end_is_all_day()
    {
        var allDay = InstallWindow.Of(new AppPreferences { InstallWindowStart = "00:00", InstallWindowEnd = "00:00" });
        Assert.All(new[] { "2026-11-24T00:00:00", "2026-11-24T07:59:59", "2026-11-24T20:00:00" }, at => Assert.True(allDay.IsOpen(Utc(at))));
    }

    [Theory]
    [InlineData("2026-11-24T23:30:00", true)]
    [InlineData("2026-11-25T00:30:00", true)]
    [InlineData("2026-11-25T01:00:00", false)]
    [InlineData("2026-11-24T22:59:59", false)]
    public void A_window_over_midnight_is_open_on_both_sides_of_it(string utc, bool open)
    {
        var night = InstallWindow.Of(new AppPreferences { InstallWindowStart = "23:00", InstallWindowEnd = "01:00", InstallWindowZone = "UTC" });
        Assert.Equal(open, night.IsOpen(Utc(utc)));
    }
}
