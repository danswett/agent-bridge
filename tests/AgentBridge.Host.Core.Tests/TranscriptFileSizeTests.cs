using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Protocol;
using Xunit;

public class TranscriptFileSizeTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(52_428_801L)]
    public void Transcript_size_comes_from_metadata_even_when_content_cannot_be_opened(long bytes)
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "synthetic.jsonl");
        using var locked = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        locked.SetLength(bytes);
        locked.Flush();

        Assert.Equal(((long?)bytes, (string?)null), TranscriptFileSize.Read(path));
    }

    [Theory]
    [InlineData(null, "path-unavailable")]
    [InlineData("missing.jsonl", "not-found")]
    [InlineData("\0", "invalid-path")]
    public void An_unavailable_transcript_has_a_reason_instead_of_a_guessed_size(string? path, string reason)
    {
        using var f = new Fixture();
        if (path == "missing.jsonl") { path = Path.Combine(f.Root, path); }
        Assert.Equal(((long?)null, reason), TranscriptFileSize.Read(path));
    }

    [Theory]
    [InlineData(AgentKind.Copilot)]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public void Transcript_metadata_tracks_partial_bytes_shrink_and_disappearance_for_its_session(AgentKind agent)
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "synthetic.jsonl");
        File.WriteAllText(path, "unfinished");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var discovered = new DiscoveredSession("session-one", agent, path, null, 100);
        var tracker = new SessionTracker(discovered, f.Options(), f.Time, hostId: "host-one");
        var first = tracker.Update(discovered);
        Assert.Equal(10, first.TranscriptBytes);
        Assert.Equal("session-one", first.Id);
        Assert.Equal(new TranscriptObservation(f.Time.GetUtcNow(), "file-size", "host-one"), first.TranscriptObservation);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Same(first, tracker.Update(discovered));

        File.WriteAllText(path, "x");
        var smaller = tracker.Update(discovered);
        Assert.Equal(1, smaller.TranscriptBytes);
        Assert.Equal(f.Time.GetUtcNow(), smaller.TranscriptObservation!.ObservedAt);
        File.AppendAllText(path, "unparsed");
        Assert.Equal(9, tracker.Update(discovered).TranscriptBytes);
        File.Delete(path);
        var missing = tracker.Update(discovered);
        Assert.Null(missing.TranscriptBytes);
        Assert.Equal("not-found", missing.TranscriptBytesUnavailable);
        var noPath = tracker.Update(discovered with { TranscriptPath = null });
        Assert.Equal("path-unavailable", noPath.TranscriptBytesUnavailable);
    }

    [Fact]
    public async Task Published_transcript_sizes_stay_bound_to_the_right_sessions_and_host()
    {
        using var f = new Fixture();
        const string firstId = "88888888-8888-4888-8888-888888888888";
        const string secondId = "99999999-9999-4999-8999-999999999999";
        File.WriteAllText(f.CopilotSession(firstId, 100), "abc");
        File.WriteAllText(f.CopilotSession(secondId, 101), "abcde");
        f.Processes.Run(AgentKind.Copilot, 100);
        f.Processes.Run(AgentKind.Copilot, 101);
        await using var source = new SessionSource(f.Options(), f.Processes, f.Time, hostId: "host-one");
        await source.RefreshAsync();

        Assert.Equal(3, source.Get(firstId)!.TranscriptBytes);
        Assert.Equal(5, source.Get(secondId)!.TranscriptBytes);
        foreach (var session in source.Snapshot())
        {
            var json = JsonSerializer.SerializeToElement(session, ProtocolJson.Options);
            Assert.Equal(session.TranscriptBytes, json.GetProperty("transcript_bytes").GetInt64());
            Assert.Equal("host-one", json.GetProperty("transcript_observation").GetProperty("host_id").GetString());
            Assert.False(json.TryGetProperty("transcript_bytes_unavailable", out _));
        }
    }
}
