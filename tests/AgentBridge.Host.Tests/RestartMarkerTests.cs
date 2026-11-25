using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

// The cause of a planned restart (#1964): written by the host that is leaving, read once by the next one. Anything else
// (no marker, an old one, a damaged one) is not a cause: it stays "not recorded", never a guess.
public sealed class RestartMarkerTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    readonly string folder = Path.Combine(Path.GetTempPath(), "abm-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }

    string Path_ => Path.Combine(folder, RestartMarker.FileName);

    [Theory]
    [InlineData(DiagnosticsRestartCause.App)]
    [InlineData(DiagnosticsRestartCause.Device)]
    [InlineData(DiagnosticsRestartCause.Watchdog)]
    public void A_written_cause_is_read_once_and_the_marker_is_gone(DiagnosticsRestartCause cause)
    {
        RestartMarker.Write(folder, cause, Now.AddSeconds(-5));

        Assert.Equal(cause, RestartMarker.Consume(folder, Now));
        Assert.False(File.Exists(Path_));
        Assert.Null(RestartMarker.Consume(folder, Now));
    }

    [Fact]
    public void No_marker_is_no_cause()
    {
        Assert.Null(RestartMarker.Consume(folder, Now));
    }

    [Fact]
    public void A_marker_older_than_ten_minutes_or_from_the_future_is_no_cause_and_is_removed()
    {
        RestartMarker.Write(folder, DiagnosticsRestartCause.App, Now.AddMinutes(-11));
        Assert.Null(RestartMarker.Consume(folder, Now));
        Assert.False(File.Exists(Path_));

        RestartMarker.Write(folder, DiagnosticsRestartCause.App, Now.AddMinutes(5));
        Assert.Null(RestartMarker.Consume(folder, Now));
        Assert.False(File.Exists(Path_));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"cause":"crash","at":"2026-11-24T11:59:55+00:00"}""")]
    [InlineData("""{"cause":"unknown","at":"2026-11-24T11:59:55+00:00"}""")]
    [InlineData("""{"cause":7,"at":"2026-11-24T11:59:55+00:00"}""")]
    [InlineData("""{"cause":"app"}""")]
    [InlineData("""[]""")]
    public void A_damaged_or_unknown_marker_is_no_cause(string text)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path_, text);

        Assert.Null(RestartMarker.Consume(folder, Now));
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void A_marker_that_cannot_be_written_never_stops_a_restart()
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path_);

        RestartMarker.Write(folder, DiagnosticsRestartCause.App, Now);

        Assert.Null(RestartMarker.Consume(folder, Now));
    }

    [Fact]
    public void An_oversize_marker_is_not_read()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path_, """{"cause":"app","at":"2026-11-24T11:59:55+00:00","x":"%""".Replace("%", new string('a', 5000)) + "\"}");

        Assert.Null(RestartMarker.Consume(folder, Now));
    }
}
