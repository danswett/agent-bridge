using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Protocol;
using Xunit;

public class TranscriptSizeTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string Other = "99999999-9999-4999-8999-999999999999";
    const string Host = "synthetic-host";

    [Fact]
    public void Size_is_file_metadata_even_when_content_cannot_be_opened()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "synthetic.jsonl");
        File.WriteAllBytes(path, [0xff, 0x00, 0xfe, 0x01]);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var sample = TranscriptSize.Read(path, Host, Fixture.T0);

        Assert.Equal(4L, sample.Bytes);
        Assert.Null(sample.Unavailable);
        Assert.Equal(Fixture.T0, sample.Provenance.ObservedAt);
        Assert.Equal("file-size", sample.Provenance.Source);
        Assert.Equal(Host, sample.Provenance.HostId);
    }

    [Theory]
    [InlineData(null, "path-unavailable")]
    [InlineData("missing.jsonl", "file-not-found")]
    [InlineData("", "invalid-path")]
    public void Unavailable_size_is_absent_and_has_an_explicit_reason(string? file, string reason)
    {
        using var f = new Fixture();
        var path = file is null or "" ? file : Path.Combine(f.Root, file);

        var sample = TranscriptSize.Read(path, Host, Fixture.T0);

        Assert.Null(sample.Bytes);
        Assert.Equal(reason, sample.Unavailable);
        Assert.Equal(Host, sample.Provenance.HostId);
        Assert.Equal(Fixture.T0, sample.Provenance.ObservedAt);
    }

    [Fact]
    public void A_directory_is_not_reported_as_an_empty_transcript()
    {
        using var f = new Fixture();
        var sample = TranscriptSize.Read(f.Root, Host, Fixture.T0);
        Assert.Null(sample.Bytes);
        Assert.NotEmpty(sample.Unavailable!);
    }

    [Fact]
    public async Task Published_sizes_are_bound_to_their_sessions_and_survive_list_and_wire_forms()
    {
        using var f = new Fixture();
        var first = f.CopilotSession(Id, 100);
        var second = f.CopilotSession(Other, 101);
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(second, [1, 2, 3, 4, 5]);
        f.Processes.Run(AgentKind.Copilot, 100);
        f.Processes.Run(AgentKind.Copilot, 101);
        await using var source = Source(f);

        await source.RefreshAsync();

        Assert.Equal(3L, source.Get(Id)!.TranscriptBytes);
        Assert.Equal(5L, source.Get(Other)!.TranscriptBytes);
        foreach (var session in source.List().Sessions)
        {
            Assert.Equal(session.Id == Id ? 3L : 5L, session.TranscriptBytes);
            var json = JsonSerializer.SerializeToElement(session, ProtocolJson.Options);
            Assert.Equal(session.TranscriptBytes, json.GetProperty("transcript_bytes").GetInt64());
            Assert.False(json.TryGetProperty("transcript_bytes_unavailable", out _));
            var provenance = json.GetProperty("transcript_bytes_provenance");
            Assert.Equal("file-size", provenance.GetProperty("source").GetString());
            Assert.Equal(Host, provenance.GetProperty("host_id").GetString());
            Assert.Equal(f.Time.GetUtcNow(), provenance.GetProperty("observed_at").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task Size_changes_without_parsable_events_and_missing_files_clear_old_measurements()
    {
        using var f = new Fixture();
        var path = f.CopilotSession(Id, 100);
        File.WriteAllBytes(path, [1, 2, 3]);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = Source(f);
        await source.RefreshAsync();
        var before = source.Get(Id)!;
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await source.RefreshAsync();
        Assert.Equal(before, source.Get(Id));

        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        await source.RefreshAsync();
        Assert.Equal(5L, source.Get(Id)!.TranscriptBytes);
        Assert.Equal(f.Time.GetUtcNow(), source.Get(Id)!.TranscriptBytesProvenance!.ObservedAt);
        File.Delete(path);
        await source.RefreshAsync();
        var missing = source.Get(Id)!;
        Assert.Null(missing.TranscriptBytes);
        Assert.Equal("file-not-found", missing.TranscriptBytesUnavailable);
        var json = JsonSerializer.SerializeToElement(missing, ProtocolJson.Options);
        Assert.False(json.TryGetProperty("transcript_bytes", out _));
        Assert.Equal("file-not-found", json.GetProperty("transcript_bytes_unavailable").GetString());

        File.WriteAllBytes(path, [1]);
        await source.RefreshAsync();
        Assert.Equal(1L, source.Get(Id)!.TranscriptBytes);
        Assert.Null(source.Get(Id)!.TranscriptBytesUnavailable);
    }

    [Fact]
    public void An_observed_empty_file_is_distinct_from_unavailable()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "empty.jsonl");
        File.WriteAllBytes(path, []);
        var sample = TranscriptSize.Read(path, Host, Fixture.T0);
        Assert.Equal(0L, sample.Bytes);
        Assert.Null(sample.Unavailable);
    }

    static SessionSource Source(Fixture f) => new(new SessionSourceOptions
    {
        CopilotHome = f.CopilotHome,
        ClaudeHome = f.ClaudeHome,
        CodexHome = f.CodexHome,
        HostId = Host,
        UseFileSystemWatcher = false,
    }, f.Processes, f.Time);
}
