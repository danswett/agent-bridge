using System.Globalization;
using AgentBridge.App.Core;
using Xunit;

public sealed class MessageTimeRefreshTests
{
    static readonly DateTimeOffset Sent = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
    static MessageClock Clock(DateTimeOffset now) => new(now, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public void A_stored_stamp_crosses_second_minute_and_clock_boundaries_without_another_history_event()
    {
        var stamp = Clock(Sent).Stamp(Sent);
        var refresh = new MessageTimeRefresh(stamp);
        foreach (var (age, expected, delay) in new[]
        {
            (9.5, "just now", 0.5), (10.0, "10s ago", 1.0), (59.5, "59s ago", 0.5),
            (60.0, "1 min ago", 60.0), (119.5, "1 min ago", 0.5),
            (120.0, "2 min ago", 60.0), (3599.5, "59 min ago", 0.5),
        })
        {
            Assert.Equal(TimeSpan.FromSeconds(delay), refresh.Update(Clock(Sent.AddSeconds(age)), visible: true));
            Assert.Equal(expected, refresh.Text);
        }
        Assert.Equal(TimeSpan.FromMinutes(1), refresh.Update(Clock(Sent.AddHours(1)), visible: true));
        Assert.Equal(MessageTime.Label(Sent, Sent.AddHours(1), TimeZoneInfo.Utc, Clock(Sent).Culture), refresh.Text);
        Assert.Equal(Sent, stamp.At);
        Assert.Equal("just now", stamp.Text);
        Assert.Equal(Clock(Sent).Stamp(Sent), stamp);
        Assert.Equal(Clock(Sent.AddHours(1)).Stamp(Sent).Exact, stamp.Exact);
    }

    [Fact]
    public void Hidden_labels_do_not_change_or_schedule_work_and_resume_with_the_current_time()
    {
        var refresh = new MessageTimeRefresh(Clock(Sent).Stamp(Sent));

        Assert.Null(refresh.Update(Clock(Sent.AddMinutes(2)), visible: false));
        Assert.Equal("just now", refresh.Text);
        Assert.Equal(TimeSpan.FromMinutes(1), refresh.Update(Clock(Sent.AddMinutes(2)), visible: true));
        Assert.Equal("2 min ago", refresh.Text);
        Assert.Null(refresh.Update(Clock(Sent.AddMinutes(10)), visible: false));
        Assert.Equal("2 min ago", refresh.Text);
        refresh.Update(Clock(Sent.AddMinutes(10)), visible: true);
        Assert.Equal("10 min ago", refresh.Text);
    }

    [Fact]
    public void Old_labels_refresh_at_the_viewers_midnight_and_future_times_do_not_tick_each_second()
    {
        var sent = Sent.AddMilliseconds(250);
        var refresh = new MessageTimeRefresh(Clock(sent).Stamp(sent));
        var midnight = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMilliseconds(250), refresh.Update(Clock(midnight.AddMilliseconds(-250)), visible: true));
        refresh.Update(Clock(midnight), visible: true);
        Assert.StartsWith("Nov 10,", refresh.Text);
        Assert.Equal(TimeSpan.FromMinutes(1), refresh.Update(Clock(sent.AddHours(-2)), visible: true));
        Assert.Equal("just now", refresh.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(250), refresh.Update(Clock(sent.AddMilliseconds(-250)), visible: true));
    }
}
