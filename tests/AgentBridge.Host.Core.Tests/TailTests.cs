using System.Text;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Transcripts;
using AgentBridge.Protocol;
using Xunit;

public class TailTests
{
    sealed class Recorder : ILineSink
    {
        public readonly List<string> Parsed = [];
        public readonly List<string> Skimmed = [];
        public int Resets;
        public void Reset() { Resets++; Parsed.Clear(); Skimmed.Clear(); }
        public void Skim(ReadOnlyMemory<byte> line) => Skimmed.Add(Encoding.UTF8.GetString(line.Span));
        public void Parse(ReadOnlyMemory<byte> line) => Parsed.Add(Encoding.UTF8.GetString(line.Span));
    }

    static TranscriptTail Tail(Fixture f, string path, long tail = 1024 * 1024, long skim = long.MaxValue, int maxLine = 1024 * 1024) =>
        new(path, new SessionSourceOptions
        {
            CopilotHome = f.CopilotHome,
            ClaudeHome = f.ClaudeHome,
            CodexHome = f.CodexHome,
            InitialTailBytes = tail,
            MaxSkimBytes = skim,
            MaxLineBytes = maxLine,
        });

    [Fact]
    public void After_the_first_read_only_appended_lines_are_read()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "t.jsonl");
        File.WriteAllText(path, "one\ntwo\n");
        var tail = Tail(f, path);
        var sink = new Recorder();

        Assert.Equal(TailOutcome.Restarted, tail.Read(sink));
        Fixture.Append(path, "three");
        Assert.Equal(TailOutcome.Appended, tail.Read(sink));
        Assert.Equal(TailOutcome.Unchanged, tail.Read(sink));

        Assert.Equal(["one", "two", "three"], sink.Parsed);
        Assert.Equal(1, sink.Resets);
    }

    [Fact]
    public void A_partial_last_line_waits_until_it_is_finished_and_is_read_once_whole()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "t.jsonl");
        File.WriteAllText(path, "one\n{\"half\":");
        var tail = Tail(f, path);
        var sink = new Recorder();

        tail.Read(sink);
        Assert.Equal(["one"], sink.Parsed);
        File.AppendAllText(path, "true}\r\n");
        tail.Read(sink);

        Assert.Equal(["one", "{\"half\":true}"], sink.Parsed);
    }

    [Fact]
    public void A_split_multibyte_character_at_the_end_is_not_decoded_until_its_line_is_complete()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "t.jsonl");
        var bytes = Encoding.UTF8.GetBytes("é😀\n");
        File.WriteAllBytes(path, bytes[..3]);
        var tail = Tail(f, path);
        var sink = new Recorder();

        tail.Read(sink);
        using (var s = new FileStream(path, FileMode.Append)) { s.Write(bytes.AsSpan(3)); }
        tail.Read(sink);

        Assert.Equal(["é😀"], sink.Parsed);
    }

    [Fact]
    public void A_transcript_that_shrank_is_read_again_from_the_start()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "t.jsonl");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        var tail = Tail(f, path);
        var sink = new Recorder();
        tail.Read(sink);

        File.WriteAllText(path, "new\n");

        Assert.Equal(TailOutcome.Restarted, tail.Read(sink));
        Assert.Equal(["new"], sink.Parsed);
        Assert.Equal(2, sink.Resets);
    }

    [Fact]
    public void A_transcript_replaced_by_one_at_least_as_long_is_read_again_from_the_start()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "t.jsonl");
        File.WriteAllText(path, "aaa\nbbb\n");
        var tail = Tail(f, path);
        var sink = new Recorder();
        tail.Read(sink);

        File.WriteAllText(path, "ccc\nddd\neee\n");

        Assert.Equal(TailOutcome.Restarted, tail.Read(sink));
        Assert.Equal(["ccc", "ddd", "eee"], sink.Parsed);
    }

    [Fact]
    public void A_big_transcript_is_parsed_only_in_its_tail_and_skimmed_before_it_with_the_edge_line_seen_once()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "t.jsonl");
        var lines = Enumerable.Range(0, 1000).Select(i => $"line-{i:D4}-" + new string('x', 90)).ToArray();
        Fixture.Append(path, lines);
        var tail = Tail(f, path, tail: 10_000);
        var sink = new Recorder();

        tail.Read(sink);

        Assert.InRange(sink.Parsed.Count, 90, 101);
        Assert.Equal(lines, sink.Skimmed.Concat(sink.Parsed));
    }

    [Fact]
    public void When_the_skim_budget_runs_out_the_tail_starts_at_the_next_whole_line()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "t.jsonl");
        var lines = Enumerable.Range(0, 1000).Select(i => $"line-{i:D4}-" + new string('x', 90)).ToArray();
        Fixture.Append(path, lines);
        var tail = Tail(f, path, tail: 10_000, skim: 1_000);
        var sink = new Recorder();

        tail.Read(sink);

        Assert.All(sink.Parsed, l => Assert.StartsWith("line-", l));
        Assert.Equal(lines[^1], sink.Parsed[^1]);
        Assert.Equal(lines[..sink.Skimmed.Count], sink.Skimmed);
        Assert.InRange(sink.Skimmed.Count, 9, 11);
    }

    [Fact]
    public void A_line_longer_than_the_limit_is_skipped_and_the_lines_after_it_are_read()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "t.jsonl");
        Fixture.Append(path, "before", new string('y', 5000), "after");
        var tail = Tail(f, path, maxLine: 1000);
        var sink = new Recorder();

        tail.Read(sink);

        Assert.Equal(["before", "after"], sink.Parsed);
    }

    [Fact]
    public void A_missing_transcript_reads_as_missing_and_is_picked_up_once_it_appears()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "later.jsonl");
        var tail = Tail(f, path);
        var sink = new Recorder();

        Assert.Equal(TailOutcome.Missing, tail.Read(sink));
        Fixture.Append(path, "first");
        tail.Read(sink);

        Assert.Equal(["first"], sink.Parsed);
    }

    [Fact]
    public async Task A_huge_Copilot_transcript_takes_its_title_turns_and_compactions_from_the_skim_and_its_state_from_the_tail()
    {
        using var f = new Fixture();
        const string id = "33333333-3333-4333-8333-333333333333";
        var events = f.CopilotSession(id, 100, name: null);
        var lines = new List<string> { Copilot.Event("session.start", new { sessionId = id }, 0), Copilot.User("the very first prompt", 1) };
        for (var i = 0; i < 300; i++)
        {
            lines.Add(Copilot.TurnStart(2 + i));
            lines.Add(Copilot.Assistant("reply " + i + new string('z', 400), 2 + i));
            lines.Add(Copilot.TurnEnd(2 + i));
            if (i % 100 == 99) { lines.Add(Copilot.Event("session.compaction_complete", new { success = true }, 2 + i)); }
            if (i % 100 == 50) { lines.Add(Copilot.Event("session.compaction_complete", new { success = false }, 2 + i)); }
            lines.Add(Copilot.User("prompt " + i, 2 + i));
        }
        lines.Add(Copilot.TurnStart(400));
        lines.Add(Copilot.Assistant("the latest answer", 400));
        lines.Add(Copilot.TurnEnd(401));
        Fixture.Append(events, [.. lines]);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(tailBytes: 8 * 1024);

        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.True(new FileInfo(events).Length > 100_000);
        Assert.Equal("the very first prompt", s.Title);
        Assert.Equal(301, s.Activity.TurnCount);
        Assert.Equal(3, s.Activity.Compactions);
        Assert.Equal("the latest answer", s.Activity.Response);
        Assert.Equal("prompt 299", s.Activity.UserMessage);
        Assert.Equal(SessionStatus.Idle, s.Status);
    }
}
