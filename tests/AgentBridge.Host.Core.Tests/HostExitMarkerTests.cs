using System.Text.Json;
using AgentBridge.Host.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class HostExitMarkerTests : IDisposable
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-18T04:00:00Z");
    readonly string root = Directory.CreateTempSubdirectory("host-exit").FullName;
    readonly FakeTimeProvider time = new(Now);
    string PathName => Path.Combine(root, "host-exit.json");
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Begin_atomically_records_the_running_process_and_start_instant()
    {
        HostExitMarker.Begin(PathName, Now);

        using var json = JsonDocument.Parse(File.ReadAllText(PathName));
        Assert.Equal("running", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(Now, json.RootElement.GetProperty("startedAt").GetDateTimeOffset());
        Assert.False(File.Exists(PathName + ".new"));
    }

    [Fact]
    public void A_clean_stop_replaces_running_with_the_reason_and_exit_instant()
    {
        var marker = HostExitMarker.Begin(PathName, Now, time: time);
        time.Advance(TimeSpan.FromSeconds(7));

        Assert.True(marker.End(HostExitReason.CleanStop));

        using var json = JsonDocument.Parse(File.ReadAllText(PathName));
        Assert.Equal("ended", json.RootElement.GetProperty("state").GetString());
        Assert.Equal("clean-stop", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(time.GetUtcNow(), json.RootElement.GetProperty("exitedAt").GetDateTimeOffset());
    }

    [Fact]
    public void An_unhandled_exception_keeps_only_its_type_and_survives_exit_fallbacks()
    {
        var marker = HostExitMarker.Begin(PathName, Now);
        marker.End(HostExitReason.CleanStop);
        marker.End(HostExitReason.UnhandledException, new IOException("private prompt and path"));
        marker.End(HostExitReason.Unknown);
        marker.End(HostExitReason.PlannedRestart);

        using var json = JsonDocument.Parse(File.ReadAllText(PathName));
        Assert.Equal("unhandled-exception", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("System.IO.IOException", json.RootElement.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("private", json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"state\":\"ended\",\"reason\":\"private prompt\"}")]
    public void A_torn_or_invalid_record_is_unknown_without_throwing(string text)
    {
        File.WriteAllText(PathName, text);

        Assert.Equal(HostExitReason.Unknown, HostExitMarker.Read(PathName)!.Reason);
    }

    [Fact]
    public void An_unreadable_record_is_unknown_without_throwing()
    {
        Directory.CreateDirectory(PathName);

        Assert.Equal(HostExitReason.Unknown, HostExitMarker.Read(PathName)!.Reason);
    }

    [Fact]
    public void A_failed_replace_preserves_the_complete_previous_record_and_reports_failure()
    {
        var failures = new List<string>();
        var marker = HostExitMarker.Begin(PathName, Now, failures.Add);
        var before = File.ReadAllBytes(PathName);
        HostExitMarker.BeforeReplace.Value = () => throw new IOException("private path");
        try { Assert.False(marker.End(HostExitReason.CleanStop)); }
        finally { HostExitMarker.BeforeReplace.Value = null; }

        Assert.Equal(before, File.ReadAllBytes(PathName));
        Assert.False(File.Exists(PathName + ".new"));
        Assert.Contains("IOException", Assert.Single(failures));
        Assert.DoesNotContain("private", failures[0]);
    }
}
