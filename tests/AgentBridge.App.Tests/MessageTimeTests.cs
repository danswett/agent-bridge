using System.Globalization;
using AgentBridge.App.Core;
using Xunit;

// The time on a message (#1619, ui.md 6.4 Time on messages). The cases are the table both the PWA and
// this app's formatters answer, so the two read the same. Zone, culture and "now" are always named.
public sealed class MessageTimeTests
{
    static readonly TimeZoneInfo Pacific = FindPacific();
    static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");

    static TimeZoneInfo FindPacific()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
    }

    // Local Pacific wall time to an instant, so a case reads as the viewer sees it.
    static DateTimeOffset Pt(int y, int mo, int d, int h, int mi, int s = 0)
    {
        var wall = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, Pacific.GetUtcOffset(wall));
    }

    // ICU writes a narrow no-break space before AM/PM; Windows writes a plain one.
    static string Norm(string s) => s.Replace('\u202f', ' ').Replace('\u00a0', ' ');

    static string Label(DateTimeOffset? at, DateTimeOffset now, CultureInfo? culture = null) =>
        Norm(MessageTime.Label(at, now, Pacific, culture ?? US));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(9, "just now")]
    [InlineData(10, "10s ago")]
    [InlineData(59, "59s ago")]
    [InlineData(60, "1 min ago")]
    [InlineData(3599, "59 min ago")]
    public void Under_an_hour_reads_relative(int seconds, string expected)
    {
        var now = Pt(2026, 11, 9, 19, 41);

        Assert.Equal(expected, Label(now.AddSeconds(-seconds), now));
    }

    [Fact]
    public void From_sixty_minutes_it_is_a_clock_time()
    {
        var now = Pt(2026, 11, 9, 20, 41);

        Assert.Equal("7:41 PM", Label(Pt(2026, 11, 9, 19, 41), now));
    }

    [Fact]
    public void Another_day_adds_the_date_and_another_year_the_year()
    {
        var now = Pt(2026, 11, 10, 9, 0);

        Assert.Equal("Nov 9, 7:41 PM", Label(Pt(2026, 11, 9, 19, 41), now));
        Assert.Equal("Nov 9, 2025, 7:41 PM", Label(Pt(2025, 11, 9, 19, 41), now));
    }

    [Fact]
    public void The_relative_window_wins_over_the_calendar_day()
    {
        var sent = Pt(2026, 11, 9, 23, 50);

        Assert.Equal("20 min ago", Label(sent, Pt(2026, 11, 10, 0, 10)));
        Assert.Equal("Nov 9, 11:50 PM", Label(sent, Pt(2026, 11, 10, 0, 50)));
    }

    [Fact]
    public void New_years_eve_viewed_on_new_years_day_names_the_year()
    {
        Assert.Equal("Dec 31, 2026, 11:50 PM", Label(Pt(2026, 12, 31, 23, 50), Pt(2027, 1, 1, 0, 50)));
    }

    [Fact]
    public void Relative_time_counts_real_elapsed_time_across_the_clocks_changing()
    {
        // 2026-03-08 spring forward: 1:30 AM PST is 40 real minutes before 3:10 AM PDT.
        var springSent = new DateTimeOffset(2026, 3, 8, 9, 30, 0, TimeSpan.Zero);
        Assert.Equal("40 min ago", Label(springSent, springSent.AddMinutes(40)));
        // 2026-11-01 fall back: 1:30 AM PDT, two real hours later, is 1:30 AM PST again.
        var fallSent = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero);
        Assert.Equal("1:30 AM", Label(fallSent, fallSent.AddMinutes(61)));
        Assert.Equal("1:30 AM", Label(fallSent, fallSent.AddMinutes(120)));
    }

    [Fact]
    public void A_time_in_the_future_reads_just_now()
    {
        var now = Pt(2026, 11, 9, 19, 41);

        Assert.Equal("just now", Label(now.AddHours(5), now));
        Assert.Equal("just now", Label(now.AddDays(400), now));
    }

    [Fact]
    public void A_missing_time_shows_nothing()
    {
        Assert.Equal("", Label(null, Pt(2026, 11, 9, 19, 41)));
    }

    [Fact]
    public void A_24_hour_locale_reads_a_24_hour_clock()
    {
        var now = Pt(2026, 11, 9, 21, 0);

        Assert.Equal("19:41", Label(Pt(2026, 11, 9, 19, 41), now, CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("7:41 PM", Label(Pt(2026, 11, 9, 19, 41), now, US));
    }

    [Fact]
    public void The_exact_time_has_the_weekday_date_seconds_and_offset()
    {
        var exact = Norm(MessageTime.Exact(Pt(2026, 11, 9, 19, 41, 7), Pacific, US));

        Assert.Equal("Mon Nov 9, 2026, 7:41:07 PM UTC-08:00", exact);
    }

    [Fact]
    public void The_exact_time_does_not_change_as_the_clock_moves()
    {
        var sent = Pt(2026, 11, 9, 19, 41, 7);
        var early = new MessageClock(sent.AddSeconds(3), Pacific, US).Stamp(sent);
        var late = new MessageClock(sent.AddMinutes(30), Pacific, US).Stamp(sent);

        Assert.NotEqual(early.Text, late.Text);
        Assert.Equal(early.Exact, late.Exact);
    }
}
