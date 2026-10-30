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
    public async Task Records_written_with_spaces_after_the_colon_still_count_when_skimmed()
    {
        using var f = new Fixture();
        const string id = "34343434-3434-4343-8343-343434343434";
        var events = f.CopilotSession(id, 100, name: null);
        static string Spaced(string line) => line.Replace("\"type\":\"", "\"type\": \"").Replace("\"success\":", "\"success\": ");
        Fixture.Append(events, Spaced(Copilot.User("spaced first prompt", 1)), Spaced(Copilot.Event("session.compaction_complete", new { success = true }, 2)));
        Fixture.Append(events, [.. Enumerable.Range(0, 100).Select(i => Copilot.Assistant("filler " + new string('f', 300), 3 + i))]);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(tailBytes: 4 * 1024);

        await source.RefreshAsync();

        var s = source.Get(id)!;
        Assert.Equal("spaced first prompt", s.Title);
        Assert.Equal(1, s.Activity.Compactions);
    }

    [Fact]
    public async Task An_append_bigger_than_the_tail_window_still_applies_every_event_in_it()
    {
        using var f = new Fixture();
        const string id = "35353535-3535-4353-8353-353535353535";
        var events = f.CopilotSession(id, 100);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("c1", "powershell", new { command = "build" }, 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(tailBytes: 4 * 1024);
        await source.RefreshAsync();

        Fixture.Append(events, [Copilot.ToolDone("c1", 4), .. Enumerable.Range(0, 100).Select(i => Copilot.Assistant("output " + new string('o', 300), 5 + i)), Copilot.TurnEnd(200)]);
        await source.RefreshAsync();

        var s = source.Get(id)!;
        Assert.Equal(ToolStatus.Done, Assert.Single(s.Activity.History!).Status);
        Assert.Equal(SessionStatus.Idle, s.Status);
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

    // The large session the shadow comparison found (#40): its background agents filled the
    // window, so no message of the session's own was in it and no model was shown, where
    // agent-ha-bridge showed the one the session started with.
    [Fact]
    public async Task The_sessions_own_model_is_known_from_its_start_and_model_changes_when_the_window_holds_only_its_agents_messages()
    {
        using var f = new Fixture();
        const string id = "36363636-3636-4363-8363-363636363636";
        var events = f.CopilotSession(id, 100);
        Fixture.Append(events,
            Copilot.Event("session.start", new { sessionId = id, selectedModel = "model-a" }, 0),
            Copilot.Event("session.model_change", new { newModel = "model-b" }, 1),
            Copilot.Event("session.model_change", new { newModel = "agent-model" }, 2, agentId: "agent-1"),
            Copilot.User("go", 3));
        Fixture.Append(events, [.. Enumerable.Range(0, 100).Select(i => Copilot.Assistant("working " + new string('w', 300), 4 + i, model: "agent-model", agentId: "agent-1"))]);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(tailBytes: 4 * 1024);

        await source.RefreshAsync();

        Assert.Equal("model-b", source.Get(id)!.Model);
    }

    // A background shell started hours earlier, outside the window, is still running and
    // still the session's to wait on; agent-ha-bridge, reading every line, counts it.
    [Fact]
    public async Task A_background_shell_started_before_the_window_is_counted_until_it_reports_its_end()
    {
        using var f = new Fixture();
        const string id = "37373737-3737-4373-8373-373737373737";
        var events = f.CopilotSession(id, 100);
        Fixture.Append(events,
            Copilot.User("watch CI", 1),
            Copilot.ToolResult("c1", "<command started in background with shellId: ci-watch>", 2),
            Copilot.ToolResult("c2", "<command with shellId: quick moved to background by the user. Respond to the user.>", 3),
            Copilot.ToolResult("c3", "<command started in background with shellId: stopped-one>", 4),
            Copilot.ShellCompleted("quick", 5),
            Copilot.ToolResult("c4", "<command with id: stopped-one stopped>", 6),
            // Quoting a marker in a command's output starts nothing.
            Copilot.ToolResult("c5", "a log that mentions\n<command started in background with shellId: quoted> in passing", 7));
        Fixture.Append(events, [.. Enumerable.Range(0, 100).Select(i => Copilot.Assistant("filler " + new string('f', 300), 8 + i))]);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(tailBytes: 4 * 1024);

        await source.RefreshAsync();
        Assert.Equal(new Background { Agents = 0, Shells = 1 }, source.Get(id)!.Activity.Background);

        Fixture.Append(events, Copilot.ShellCompleted("ci-watch", 200));
        await source.RefreshAsync();
        Assert.Null(source.Get(id)!.Activity.Background);
    }
}
