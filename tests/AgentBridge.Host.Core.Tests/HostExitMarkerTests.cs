using System.Text.Json;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Protocol;
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

    [Fact]
    public void A_running_marker_reports_no_exit_record_once_without_changing_the_file()
    {
        HostExitMarker.Begin(PathName, Now);
        var before = File.ReadAllBytes(PathName);
        var lines = new List<string>();
        var journal = new DiagnosticsJournal(time);

        HostExitMarker.ReportPrevious(PathName, lines.Add, journal, Now);

        Assert.Equal(HostExitReason.NoRecord, HostExitMarker.ReadPrevious(PathName)!.Reason);
        Assert.Equal("previous run ended: no exit record (hard kill, crash or power loss)", Assert.Single(lines));
        Assert.Single(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Theory]
    [InlineData(HostExitReason.CleanStop, "clean-stop")]
    [InlineData(HostExitReason.PlannedRestart, "planned-restart")]
    [InlineData(HostExitReason.UnhandledException, "unhandled-exception")]
    [InlineData(HostExitReason.Unknown, "unknown")]
    public void A_finished_marker_reports_its_reason_once_without_changing_the_file(HostExitReason reason, string name)
    {
        HostExitMarker.Begin(PathName, Now, time: time).End(reason, new IOException("private prompt"));
        var before = File.ReadAllBytes(PathName);
        var lines = new List<string>();

        HostExitMarker.ReportPrevious(PathName, lines.Add, new DiagnosticsJournal(time), Now);

        Assert.Equal(reason, HostExitMarker.ReadPrevious(PathName)!.Reason);
        Assert.Equal($"previous run ended: {name}", Assert.Single(lines));
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Fact]
    public void A_missing_file_is_first_start_and_never_claims_a_crash()
    {
        var lines = new List<string>();
        var journal = new DiagnosticsJournal(time);

        HostExitMarker.ReportPrevious(PathName, lines.Add, journal, Now);

        Assert.Null(HostExitMarker.ReadPrevious(PathName));
        Assert.Empty(lines);
        Assert.Empty(journal.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.False(File.Exists(PathName));
    }

    [Fact]
    public void The_previous_run_line_cannot_copy_paths_ids_or_exception_messages_from_a_record()
    {
        File.WriteAllText(PathName, """{"state":"ended","reason":"private path node-id session-id prompt","exceptionType":"private prompt","exitedAt":"2026-11-18T04:00:00Z"}""");
        var lines = new List<string>();

        HostExitMarker.ReportPrevious(PathName, lines.Add, new DiagnosticsJournal(time), Now);

        Assert.Equal("previous run ended: unknown", Assert.Single(lines));
    }
}
