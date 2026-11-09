using System.Globalization;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

// Claude's and Codex's delegates (docs/design/subagents.md §3, #174), replayed from transcripts
// recorded on a disposable VM and redacted: tests/fixtures/claude holds a Claude Code 2.1.316
// session that started two Agent delegates, one of which started a third, and a Bash command
// with run_in_background, with each delegate's own transcript and record beside it;
// tests/fixtures/codex a Codex CLI 0.166.0 session that spawned one sub-agent thread and waited
// for it, with that thread's own rollout. Their shapes and field sets are the recorded ones;
// every text, id, path and time in them was replaced.
public class ClaudeSubagentTests
{
    const string Id = "0e0fb277-3adb-4aaa-9031-9ef818be1a11";
    const string Files = "ae97dc47d3b3c41aa";
    const string Nester = "a0f6636826f55f9b9";
    const string Nested = "ad3e07523907ee045";
    const string Shell = "v8lv8q118";
    const string Prompt = "Start two delegates in parallel, one of which starts its own, and run a command in the background; summarise when they finish.";

    static string FixturePath(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "fixtures", "claude", .. parts]);

    // Replays the recorded session up to a time after Fixture.T0: the session's transcript, and
    // each delegate's record and transcript from when its first entry was written.
    internal sealed class Replay : IAsyncDisposable
    {
        readonly Fixture _f = new();
        readonly string _transcript;
        readonly string _beside;
        readonly string[] _lines;
        readonly Dictionary<string, (string Meta, string[] Lines)> _agents = [];
        readonly Dictionary<string, int> _written = [];
        // Agents whose files are not written yet, whatever the time.
        public readonly HashSet<string> Held = [];
        int _next;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Replay(Func<string, string?>? edit = null, long tailBytes = 4 * 1024 * 1024, Func<string[], long>? skim = null)
        {
            _transcript = _f.ClaudeTranscript(Id);
            _beside = Path.Combine(Path.GetDirectoryName(_transcript)!, Id, "subagents");
            _f.ClaudeRegistration(Id, 900, _transcript);
            _f.Processes.Run(AgentKind.Claude, 900);
            _lines = [.. File.ReadAllLines(FixturePath(Id + ".jsonl")).Select(l => edit is null ? l : edit(l)).OfType<string>().SelectMany(l => l.Split('\n'))];
            foreach (var meta in Directory.GetFiles(FixturePath(Id, "subagents"), "*.meta.json"))
            {
                var name = Path.GetFileName(meta)[..^".meta.json".Length];
                _agents[name] = (File.ReadAllText(meta), File.ReadAllLines(Path.Combine(Path.GetDirectoryName(meta)!, name + ".jsonl")));
                _written[name] = 0;
            }
            Source = skim is null ? _f.Source(tailBytes) : _f.Source(tailBytes, maxSkim: skim(_lines));
        }

        public async Task<Session> Through(double second)
        {
            var until = Fixture.T0.AddSeconds(second);
            var batch = new List<string>();
            while (_next < _lines.Length && !(At(_lines[_next]) >= until)) { batch.Add(_lines[_next++]); }
            Fixture.Append(_transcript, [.. batch]);
            foreach (var (name, (meta, lines)) in _agents)
            {
                if (Held.Contains(name)) { continue; }
                var from = _written[name];
                var to = from;
                while (to < lines.Length && At(lines[to]) < until) { to++; }
                if (to == from) { continue; }
                Directory.CreateDirectory(_beside);
                if (from == 0) { File.WriteAllText(Path.Combine(_beside, name + ".meta.json"), meta); }
                Fixture.Append(Path.Combine(_beside, name + ".jsonl"), lines[from..to]);
                _written[name] = to;
            }
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        // Writes a held agent's files as far as the replay has reached.
        public async Task<Session> Release(string agent, double second)
        {
            Held.Remove("agent-" + agent);
            return await Through(second);
        }

        // A delegate's record written before anything of its own transcript is.
        public async Task<Session> RecordOnly(string agent)
        {
            Directory.CreateDirectory(_beside);
            File.WriteAllText(Path.Combine(_beside, "agent-" + agent + ".meta.json"), _agents["agent-" + agent].Meta);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        // Records written together, each before anything of its agent's transcript: a recorded
        // agent's own, or one given (meta), and the pass that reads them.
        public async Task<Session> Records(params (string Agent, string? Meta)[] records)
        {
            Directory.CreateDirectory(_beside);
            foreach (var (agent, meta) in records) { File.WriteAllText(Path.Combine(_beside, "agent-" + agent + ".meta.json"), meta ?? _agents["agent-" + agent].Meta); }
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        // Another pass, nothing written.
        public async Task<Session> Refresh()
        {
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        // A delegate's record and its transcript's first line (changed by edit), the newline not
        // written yet.
        public async Task<Session> FirstLineOnly(string agent, Func<string, string>? edit = null)
        {
            var (meta, lines) = _agents["agent-" + agent];
            Directory.CreateDirectory(_beside);
            File.WriteAllText(Path.Combine(_beside, "agent-" + agent + ".meta.json"), meta);
            File.WriteAllText(Path.Combine(_beside, "agent-" + agent + ".jsonl"), edit is null ? lines[0] : edit(lines[0]));
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        // Entries beyond the recording, in the session's own transcript.
        public async Task<Session> Append(params string[] lines)
        {
            Fixture.Append(_transcript, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        static DateTimeOffset? At(string line) =>
            JsonNode.Parse(line)!["timestamp"]?.GetValue<string>() is { } t ? DateTimeOffset.Parse(t, CultureInfo.InvariantCulture) : null;

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            _f.Dispose();
        }
    }

    static Subagent Entry(Session s, string id) => Assert.Single(s.Subagents!, a => a.Id == id);

    [Fact]
    public async Task Delegates_started_with_the_Agent_tool_are_listed_with_their_description_prompt_and_model_and_counted_while_they_run()
    {
        await using var r = new Replay();

        var s = await r.Through(8);

        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal(new Background { Agents = 3, Shells = 1 }, s.Activity.Background);
        var files = Entry(s, Files);
        Assert.Equal((SubagentKind.Background, SubagentStatus.Working, (string?)null), (files.Kind, files.Status, files.Parent));
        Assert.Equal("List the files", files.Name);
        Assert.Equal("List the files in this folder, top level only, and reply with the names.", files.Task);
        Assert.Equal("claude-haiku-5-5", files.Model);
        Assert.Equal(Fixture.T0.AddMilliseconds(4089), files.StartedAt);
        Assert.Null(files.Outcome);
        Assert.Equal(("Start a nested delegate", (string?)null), (Entry(s, Nester).Name, Entry(s, Nester).Parent));
        // The session's model is the main agent's, never a delegate's (#73).
        Assert.Equal("claude-haiku-5-5", s.Model);
    }

    [Fact]
    public async Task A_delegate_started_by_another_is_listed_under_it_from_the_record_written_beside_the_session()
    {
        await using var r = new Replay();

        var before = await r.Through(6.5);
        Assert.DoesNotContain(before.Subagents!, a => a.Id == Nested);

        var s = await r.Through(8);

        var nested = Entry(s, Nested);
        Assert.Equal((Nester, SubagentKind.Background, SubagentStatus.Working), (nested.Parent, nested.Kind, nested.Status));
        Assert.Equal("Show the latest commits", nested.Name);
        Assert.Equal("Show the latest three commits in this folder and reply with them.", nested.Task);
        Assert.Equal(Fixture.T0.AddMilliseconds(6943), nested.StartedAt);
    }

    [Fact]
    public async Task A_task_notification_ends_a_delegates_run_as_done_and_a_finished_delegate_is_not_counted()
    {
        await using var r = new Replay();

        // The list-files delegate's notice was queued at 8.69 s; until Claude delivers it, the
        // queue alone (which carries no mark of who wrote to it) ends nothing.
        var s = await r.Through(9.25);
        Assert.Equal(SessionStatus.Idle, s.Status);
        Assert.Equal(SubagentStatus.Working, Entry(s, Files).Status);

        // Delivered at 9.276 s, marked as Claude's task notification: it stopped when queued.
        s = await r.Through(9.3);
        Assert.Equal(new Background { Agents = 2, Shells = 1 }, s.Activity.Background);
        var files = Entry(s, Files);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (files.Status, files.Outcome));
        Assert.Equal(Fixture.T0.AddMilliseconds(8690), files.EndedAt);
        // Running first, then the finished.
        Assert.Equal(Files, s.Subagents![^1].Id);

        // The nested delegate's notice is queued in the session's transcript, though it is
        // delivered to the delegate that started it.
        s = await r.Through(10.5);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (Entry(s, Nested).Status, Entry(s, Nested).Outcome));
        Assert.Equal(SubagentStatus.Working, Entry(s, Nester).Status);
        Assert.Equal(new Background { Agents = 1, Shells = 1 }, s.Activity.Background);

        s = await r.Through(12);
        Assert.All(s.Subagents!, a => Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (a.Status, a.Outcome)));
        Assert.Equal(new Background { Shells = 1 }, s.Activity.Background);
    }

    [Fact]
    public async Task A_command_run_in_the_background_is_counted_until_its_notification()
    {
        await using var r = new Replay();

        Assert.Equal(1, (await r.Through(15)).Activity.Background!.Shells);

        var s = await r.Through(38);

        Assert.Null(s.Activity.Background);
        Assert.Equal(ToolStatus.Done, Assert.Single(s.Activity.History!, t => t.Name == "Bash").Status);
    }

    [Fact]
    public async Task A_notice_delivered_to_the_main_agent_is_a_turn_of_its_own_and_never_the_persons_message()
    {
        await using var r = new Replay();

        var s = await r.Through(9.3);

        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal(Prompt, s.Activity.UserMessage);

        s = await r.Through(40);
        Assert.Equal(SessionStatus.Idle, s.Status);
        Assert.Equal(Prompt, s.Activity.UserMessage);
        Assert.Equal(1, s.Activity.TurnCount);
    }

    [Fact]
    public async Task Nothing_a_delegate_wrote_reaches_the_sessions_response()
    {
        await using var r = new Replay();
        string[] main =
        [
            "Started both delegates and the background command; I will summarise when they finish.",
            "The first delegate listed two files.",
            "The nested delegate reported the latest commit.",
            "Both delegates finished: two files, and one commit.\nThe background command finished too.",
        ];

        foreach (var second in new[] { 6, 8, 9.3, 10.5, 12, 15, 38, 40 })
        {
            var s = await r.Through(second);
            Assert.True(s.Activity.Response is null || main.Contains(s.Activity.Response), $"at {second} s: {s.Activity.Response}");
            Assert.Null(s.Activity.Source);
            Assert.Null(s.Activity.Own);
            Assert.DoesNotContain("task-notification", s.Activity.UserMessage, StringComparison.Ordinal);
        }
        Assert.Equal(main[^1], (await r.Through(40)).Activity.Response);
    }

    [Fact]
    public async Task A_host_starting_after_everything_lists_each_delegate_as_it_ended()
    {
        await using var r = new Replay();

        var s = await r.Through(200);

        Assert.Equal(SessionStatus.Idle, s.Status);
        Assert.Null(s.Activity.Background);
        Assert.Equal(new[] { Files, Nester, Nested }.Order(StringComparer.Ordinal), s.Subagents!.Select(a => a.Id).Order(StringComparer.Ordinal));
        Assert.All(s.Subagents!, a => Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (a.Status, a.Outcome)));
        Assert.Equal(Nester, Entry(s, Nested).Parent);
        Assert.Equal("You picked beta.", s.Activity.Response);
    }

    [Fact]
    public async Task Delegates_started_and_ended_before_the_window_a_host_parses_are_read_from_the_part_it_skims()
    {
        // Only the last turn, the question, is parsed.
        await using var r = new Replay(tailBytes: 3 * 1024);

        var s = await r.Through(200);

        Assert.Equal(3, s.Subagents!.Count);
        Assert.All(s.Subagents!, a => Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (a.Status, a.Outcome)));
        Assert.Equal(("List the files", "claude-haiku-5-5"), (Entry(s, Files).Name, Entry(s, Files).Model));
        Assert.Equal(Nester, Entry(s, Nested).Parent);
        Assert.Null(s.Activity.Background);
    }

    [Fact]
    public async Task A_delegate_whose_start_was_skimmed_and_whose_end_fell_in_bytes_never_read_is_not_left_running()
    {
        // The skim budget covers the session's start up to the first delegate's launch, the
        // window only the last turn: everything between is never read (found in review).
        await using var r = new Replay(tailBytes: 3 * 1024, skim: lines => Through(lines, l => l.Contains("async_launched", StringComparison.Ordinal)));

        var s = await r.Through(200);

        var files = Assert.Single(s.Subagents!);
        Assert.Equal((Files, SubagentStatus.Idle, (SubagentOutcome?)null), (files.Id, files.Status, files.Outcome));
        Assert.Null(s.Activity.Background);
    }

    // The bytes up to and including the first line that matches, as the replay writes them.
    internal static long Through(string[] lines, Func<string, bool> last) =>
        lines.TakeWhile(l => !last(l)).Concat(lines.SkipWhile(l => !last(l)).Take(1)).Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + 1L);

    [Fact]
    public async Task A_nested_delegate_whose_own_notice_was_never_read_stops_when_the_one_above_it_does()
    {
        // Claude notifies a delegate's end only once none of its own is live.
        await using var r = new Replay(l => l.Contains("\"queue-operation\"", StringComparison.Ordinal) && l.Contains(Nested, StringComparison.Ordinal) ? null : l);

        Assert.Equal(SubagentStatus.Working, Entry(await r.Through(11), Nested).Status);

        var s = await r.Through(12);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (Entry(s, Nested).Status, Entry(s, Nested).Outcome));

        // Read cold, its record arrives after the transcript said its parent had stopped.
        await using var cold = new Replay(l => l.Contains("\"queue-operation\"", StringComparison.Ordinal) && l.Contains(Nested, StringComparison.Ordinal) ? null : l);
        var all = await cold.Through(200);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (Entry(all, Nested).Status, Entry(all, Nested).Outcome));
    }

    [Fact]
    public async Task A_delegate_recorded_before_its_own_transcript_is_written_gets_its_prompt_and_start_once_it_is()
    {
        await using var r = new Replay();
        await r.Through(6.5);

        var early = Entry(await r.RecordOnly(Nested), Nested);
        Assert.Equal((Nester, (string?)null, (DateTimeOffset?)null), (early.Parent, early.Task, early.StartedAt));

        var s = await r.Through(8);

        Assert.Equal("Show the latest three commits in this folder and reply with them.", Entry(s, Nested).Task);
        Assert.Equal(Fixture.T0.AddMilliseconds(6943), Entry(s, Nested).StartedAt);
    }

    // Found in review (#634): with spawnDepth left out (read as 2), a nested agent's record can sort
    // before its parent's, so the pass that reads both lists the parent only. The next pass lists
    // the child, but with the records unchanged the session was not built again, and the child
    // stayed missing until something else changed.
    [Fact]
    public async Task A_nested_delegate_recorded_with_its_parent_and_sorted_before_it_is_listed_on_the_next_pass()
    {
        const string Child = "a0000000000000001";
        await using var r = new Replay();
        await r.Through(6.5);

        var first = await r.Records((Nested, null), (Child, $"{{\"parentAgentId\":\"{Nested}\",\"description\":\"Read the build log\"}}"));
        Assert.Contains(first.Subagents!, a => a.Id == Nested);

        var s = await r.Refresh();

        Assert.Equal((Nested, "Read the build log"), (Entry(s, Child).Parent, Entry(s, Child).Name));
    }

    [Fact]
    public async Task A_notice_delivered_after_a_message_woke_the_delegate_does_not_end_its_new_run()
    {
        // Found in review: the notice is queued, a message wakes the delegate, and only then is
        // the notice delivered to the main agent.
        var wake = JsonNode.Parse(Claude.Assistant(0, "tool_use", Claude.ToolUse("toolu_wake", "SendMessage", new JsonObject { ["to"] = Files, ["summary"] = "List again", ["message"] = "List them again." })))!;
        wake["timestamp"] = Fixture.T0.AddMilliseconds(8700).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        await using var r = new Replay(l => l.Contains("\"operation\":\"enqueue\"", StringComparison.Ordinal) && l.Contains($"<task-id>{Files}</task-id>", StringComparison.Ordinal) ? l + "\n" + wake.ToJsonString() : l);

        var s = await r.Through(9.3);

        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Entry(s, Files).Status, Entry(s, Files).Outcome));
        Assert.Equal(3, s.Activity.Background!.Agents);
    }

    [Fact]
    public async Task A_turn_for_a_notice_leaves_the_prompts_already_answered_counted()
    {
        // Found in review: it is not a prompt in progress, so the count does not go back.
        await using var r = new Replay();

        Assert.Equal(1, (await r.Through(9.25)).Activity.TurnCount);
        var s = await r.Through(9.3);

        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal(1, s.Activity.TurnCount);
    }

    [Fact]
    public async Task A_turn_for_a_notice_the_window_begins_inside_leaves_the_prompts_already_answered_counted()
    {
        // Found in review: the notice itself was only skimmed. The turn's first reply is made one
        // that goes on, so the window holds the turn still running.
        static bool Delivered(string l) => l.Contains("\"type\":\"user\"", StringComparison.Ordinal) && l.Contains("<task-notification>", StringComparison.Ordinal);
        await using var r = new Replay(l => l.Contains("04:00:10.486Z", StringComparison.Ordinal) ? l.Replace("\"stop_reason\":\"end_turn\"", "\"stop_reason\":null", StringComparison.Ordinal) : l,
            tailBytes: 2560, skim: lines => Through(lines, Delivered));

        var s = await r.Through(10.6);

        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal(1, s.Activity.TurnCount);
    }

    [Fact]
    public async Task A_turn_for_a_notice_does_not_show_the_reasoning_of_the_turn_before()
    {
        await using var r = new Replay();
        Assert.Equal("Waiting for the delegates.", (await r.Through(9.25)).Activity.Reasoning);

        var s = await r.Through(9.28);

        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Null(s.Activity.Reasoning);
    }

    [Fact]
    public async Task A_nested_delegate_woken_after_it_stopped_but_before_its_record_is_read_is_running()
    {
        // Found in review: the wake names an agent not known yet, so it was dropped.
        await using var r = new Replay();
        r.Held.Add("agent-" + Nested);
        await r.Through(10.3);
        var wake = JsonNode.Parse(Claude.Assistant(0, "tool_use", Claude.ToolUse("toolu_wake", "SendMessage", new JsonObject { ["to"] = Nested, ["summary"] = "Again", ["message"] = "Show them again." })))!;
        wake["timestamp"] = Fixture.T0.AddMilliseconds(10290).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        await r.Append(wake.ToJsonString());

        var s = await r.Release(Nested, 10.3);

        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Entry(s, Nested).Status, Entry(s, Nested).Outcome));
    }

    [Fact]
    public async Task A_nested_delegate_whose_transcript_has_one_line_with_no_newline_yet_has_its_start_and_prompt()
    {
        // Found in review: a first line was read only once a newline followed it.
        await using var r = new Replay();
        r.Held.Add("agent-" + Nested);
        await r.Through(7);

        var nested = Entry(await r.FirstLineOnly(Nested), Nested);

        Assert.Equal(Fixture.T0.AddMilliseconds(6943), nested.StartedAt);
        Assert.Equal("Show the latest three commits in this folder and reply with them.", nested.Task);
    }

    [Fact]
    public async Task A_nested_delegate_whose_first_line_is_too_long_to_read_whole_is_dated_by_its_own_timestamp()
    {
        // Found in review: it was dated by the file's creation time, which not every file system keeps.
        await using var r = new Replay();
        r.Held.Add("agent-" + Nested);
        await r.Through(7);
        static string Long(string line)
        {
            var e = JsonNode.Parse(line)!;
            e["message"]!["content"] = new string('x', 80 * 1024);
            return e.ToJsonString() + "\n";
        }

        var nested = Entry(await r.FirstLineOnly(Nested, Long), Nested);

        Assert.Equal(Fixture.T0.AddMilliseconds(6943), nested.StartedAt);
        Assert.Null(nested.Task);
    }

    // Found in review of #292: half a surrogate pair ("\uD800") in a nested agent's record or its
    // first prompt threw out of the session's update, so the session was never listed.
    [Fact]
    public async Task A_nested_agents_record_and_first_prompt_holding_half_a_surrogate_pair_are_read_and_the_session_listed_and_updated()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        // The session's own delegate, Nester: only then does the session read nested records.
        var launched = JsonNode.Parse(Claude.ToolResult("agent1", 3))!.AsObject();
        launched["toolUseResult"] = new JsonObject { ["status"] = "async_launched", ["agentId"] = Nester, ["description"] = "Lead", ["prompt"] = "lead" };
        Fixture.Append(transcript,
            Claude.User("go", 1),
            Claude.Assistant(2, "tool_use", Claude.ToolUse("agent1", "Agent", new JsonObject { ["description"] = "Lead", ["prompt"] = "lead" })),
            launched.ToJsonString());
        var beside = Path.Combine(Path.GetDirectoryName(transcript)!, Id, "subagents");
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(beside, $"agent-{Nested}.meta.json"), $"{{\"parentAgentId\":\"{Nester}\",\"spawnDepth\":2,\"description\":\"cut \\uD800\"}}");
        var first = JsonNode.Parse(File.ReadLines(FixturePath(Id, "subagents", $"agent-{Nested}.jsonl")).First())!;
        first["message"]!["content"] = "look HALF";
        File.WriteAllText(Path.Combine(beside, $"agent-{Nested}.jsonl"), first.ToJsonString().Replace("HALF", "\\uD800", StringComparison.Ordinal) + "\n");

        await using var source = f.Source();
        await source.RefreshAsync();
        var listed = source.Get(Id)!;
        Assert.Equal("go", listed.Activity.UserMessage);
        Assert.Contains(listed.Subagents!, a => a.Id == Nested);
        Fixture.Append(transcript, Claude.User("next", 4));
        await source.RefreshAsync();
        Assert.Equal("next", source.Get(Id)!.Activity.UserMessage);

        var read = Assert.Single(new AgentBridge.Host.Core.Parsing.ClaudeSubagents().Read(transcript));
        Assert.Equal(("cut \uFFFD", "look \uFFFD"), (read.Description, read.Prompt));
    }

    [Fact]
    public void A_record_that_could_not_be_read_is_read_again_though_it_has_not_changed()
    {
        // Found in review: a failed read was remembered like a record of no nested agent's. Held
        // open with no sharing, the record cannot be read on Windows; elsewhere it can be anyway.
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        var beside = Path.Combine(Path.GetDirectoryName(transcript)!, Id, "subagents");
        Directory.CreateDirectory(beside);
        var meta = Path.Combine(beside, $"agent-{Nested}.meta.json");
        File.WriteAllText(meta, $"{{\"parentAgentId\":\"{Nester}\",\"spawnDepth\":2}}");
        var records = new AgentBridge.Host.Core.Parsing.ClaudeSubagents();
        using (new FileStream(meta, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { records.Read(transcript); }

        var read = records.Read(transcript);

        Assert.Equal(Nested, Assert.Single(read).Id);
    }

    [Fact]
    public async Task A_nested_delegate_woken_after_the_one_above_it_stopped_is_not_stopped_by_that_ones_delivered_notice()
    {
        // Found in review: the notice of the delegate above was queued before the wake and
        // delivered after it.
        await using var r = new Replay();
        await r.Through(11.34);
        Assert.Equal(SubagentStatus.Working, Entry(await r.Append(Wake(Nested, 11340)), Nested).Status);

        var s = await r.Through(12);

        Assert.Equal(SubagentStatus.Idle, Entry(s, Nester).Status);
        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Entry(s, Nested).Status, Entry(s, Nested).Outcome));
    }

    [Fact]
    public async Task A_nested_delegate_whose_record_is_read_after_the_one_above_it_stopped_and_a_message_woke_it_is_running()
    {
        // Found in review: its own notice not read, it took the end of the delegate above it, and
        // the wake since was lost.
        await using var r = new Replay(l => l.Contains("\"queue-operation\"", StringComparison.Ordinal) && l.Contains($"<task-id>{Nested}</task-id>", StringComparison.Ordinal) ? null : l);
        r.Held.Add("agent-" + Nested);
        await r.Through(12);
        await r.Append(Wake(Nested, 12500));

        var s = await r.Release(Nested, 12);

        Assert.Equal(SubagentStatus.Working, Entry(s, Nested).Status);
    }

    [Fact]
    public void A_record_whose_transcript_could_not_be_read_is_read_again_though_neither_has_changed()
    {
        // Found in review: the record was kept with no start, and nothing had changed to read it
        // again. Held open with no sharing, the transcript cannot be read on Windows.
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        var beside = Path.Combine(Path.GetDirectoryName(transcript)!, Id, "subagents");
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(beside, $"agent-{Nested}.meta.json"), $"{{\"parentAgentId\":\"{Nester}\",\"spawnDepth\":2}}");
        var head = Path.Combine(beside, $"agent-{Nested}.jsonl");
        File.WriteAllText(head, File.ReadLines(FixturePath(Id, "subagents", $"agent-{Nested}.jsonl")).First() + "\n");
        var records = new AgentBridge.Host.Core.Parsing.ClaudeSubagents();
        using (new FileStream(head, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { records.Read(transcript); }

        var read = Assert.Single(records.Read(transcript));

        Assert.Equal(Fixture.T0.AddMilliseconds(6943), read.StartedAt);
    }

    [Fact]
    public void Past_the_most_records_read_the_first_by_name_are_read_whatever_order_they_were_written_in()
    {
        // Found in review: the first the folder listed were read, an order no platform promises.
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        var beside = Path.Combine(Path.GetDirectoryName(transcript)!, Id, "subagents");
        Directory.CreateDirectory(beside);
        var count = AgentBridge.Host.Core.Parsing.ClaudeSubagents.MaxRecords + 4;
        for (var i = 0; i < count; i++)
        {
            // Written later, named earlier, and the latest written last.
            var file = Path.Combine(beside, $"agent-a{count - i:x4}.meta.json");
            File.WriteAllText(file, $"{{\"parentAgentId\":\"{Nester}\",\"spawnDepth\":2}}");
            File.SetLastWriteTimeUtc(file, Fixture.T0.UtcDateTime.AddSeconds(i));
        }

        var read = new AgentBridge.Host.Core.Parsing.ClaudeSubagents().Read(transcript);

        Assert.Equal(AgentBridge.Host.Core.Parsing.ClaudeSubagents.MaxRecords, read.Count);
        Assert.Contains(read, r => r.Id == $"a{1:x4}");
        Assert.DoesNotContain(read, r => r.Id == $"a{count:x4}");
    }

    // A message to an agent from the main agent, at a time after Fixture.T0.
    static string Wake(string to, int ms)
    {
        var wake = JsonNode.Parse(Claude.Assistant(0, "tool_use", Claude.ToolUse("toolu_wake" + ms, "SendMessage", new JsonObject { ["to"] = to, ["summary"] = "Again", ["message"] = "Do it again." })))!;
        wake["timestamp"] = Fixture.T0.AddMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return wake.ToJsonString();
    }

    // The nested delegate's recorded notice, queued ("enqueue") or delivered ("remove"), at another time.
    static string NestedNotice(string operation, int ms)
    {
        var line = JsonNode.Parse(File.ReadAllLines(FixturePath(Id + ".jsonl")).Single(l => l.Contains("\"queue-operation\"", StringComparison.Ordinal)
            && l.Contains($"\"operation\":\"{operation}\"", StringComparison.Ordinal) && l.Contains($"<task-id>{Nested}</task-id>", StringComparison.Ordinal)))!;
        line["timestamp"] = Fixture.T0.AddMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return line.ToJsonString();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_nested_delegate_woken_twice_before_either_notice_is_delivered_is_running_whenever_its_record_is_read(bool readBetween)
    {
        // Found in review: the second notice queued replaced the first's delivery still owed.
        await using var r = new Replay();
        r.Held.Add("agent-" + Nested);
        await r.Through(10.2);
        await r.Append(NestedNotice("enqueue", 10217), Wake(Nested, 10220), NestedNotice("enqueue", 10230), Wake(Nested, 10240));
        if (readBetween) { await r.Release(Nested, 10.2); }

        await r.Append(NestedNotice("remove", 10250), NestedNotice("remove", 10260));
        var s = await r.Release(Nested, 10.2);

        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Entry(s, Nested).Status, Entry(s, Nested).Outcome));
    }

    [Fact]
    public async Task A_nested_delegate_started_in_an_earlier_run_of_the_one_above_it_is_not_counted_when_that_one_is_woken_after_bytes_never_read()
    {
        // Found in review: its end is in what was not read, and the record is read only after
        // the delegate above it was woken again.
        await using var r = new Replay(tailBytes: 1024, skim: lines => Through(lines, l => l.Contains("async_launched", StringComparison.Ordinal) && l.Contains(Nester, StringComparison.Ordinal)));
        r.Held.Add("agent-" + Nested);
        await r.Through(11);
        Assert.Equal(1, (await r.Append(Wake(Nester, 11100))).Activity.Background?.Agents);

        var s = await r.Release(Nested, 11);

        Assert.Equal(SubagentStatus.Working, Entry(s, Nester).Status);
        Assert.Equal((SubagentStatus.Idle, (SubagentOutcome?)null), (Entry(s, Nested).Status, Entry(s, Nested).Outcome));
        Assert.Equal(1, s.Activity.Background!.Agents);
    }

    [Fact]
    public async Task A_nested_delegate_found_between_its_queued_notice_and_its_delivery_is_not_stopped_again_by_the_delivery()
    {
        // Found in review: its record is read only after its notice was queued, a message wakes
        // it, and then the queue's remove (its delivery to the delegate above) arrives. Queued
        // and not delivered, the notice has not ended it yet.
        await using var r = new Replay();
        r.Held.Add("agent-" + Nested);
        await r.Through(10.23);

        var found = Entry(await r.Release(Nested, 10.23), Nested);
        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (found.Status, found.Outcome));

        var wake = JsonNode.Parse(Claude.Assistant(0, "tool_use", Claude.ToolUse("toolu_wake", "SendMessage", new JsonObject { ["to"] = Nested, ["summary"] = "Again", ["message"] = "Show them again." })))!;
        wake["timestamp"] = Fixture.T0.AddMilliseconds(10240).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        Assert.Equal(SubagentStatus.Working, Entry(await r.Append(wake.ToJsonString()), Nested).Status);

        var s = await r.Through(10.3);

        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Entry(s, Nested).Status, Entry(s, Nested).Outcome));
    }

    [Fact]
    public async Task A_delegate_whose_notice_is_not_completed_failed()
    {
        await using var r = new Replay(l => l.Contains($"<task-id>{Files}</task-id>", StringComparison.Ordinal) ? l.Replace("<status>completed</status>", "<status>failed</status>", StringComparison.Ordinal) : l);

        var s = await r.Through(9.3);

        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Failed), (Entry(s, Files).Status, Entry(s, Files).Outcome));
        Assert.Equal(2, s.Activity.Background!.Agents);
    }

    [Fact]
    public async Task A_message_sent_to_a_finished_delegate_makes_it_work_again_until_it_notifies_again()
    {
        // SendMessage with `to`, as the launch's own result says a delegate is continued; this
        // exchange was not recorded, so its entries are built on the recorded ones.
        await using var r = new Replay();
        var done = await r.Through(200);
        Assert.Equal(SubagentStatus.Idle, Entry(done, Files).Status);

        var woken = await r.Append(
            Claude.Assistant(210, "tool_use", Claude.ToolUse("toolu_wake", "SendMessage", new JsonObject { ["to"] = Files, ["summary"] = "List again", ["message"] = "List them again." })),
            Claude.ToolResult("toolu_wake", 211));

        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Entry(woken, Files).Status, Entry(woken, Files).Outcome));
        Assert.Equal(1, woken.Activity.Background!.Agents);

        var again = await r.Append(Notice(Files, 220), Delivery(Files, 221));

        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (Entry(again, Files).Status, Entry(again, Files).Outcome));
        Assert.Equal(Fixture.T0.AddSeconds(220), Entry(again, Files).EndedAt);
        Assert.Null(again.Activity.Background);
    }

    static string NoticeText(string task, string status = "completed") =>
        $"<task-notification>\n<task-id>{task}</task-id>\n<status>{status}</status>\n<summary>Agent \"List the files\" finished</summary>\n<result>README.md</result>\n</task-notification>";

    // A notice queued, as Claude queues one the moment an agent stops, and as it queues a prompt
    // the person types while the agent works.
    static string Notice(string task, int s, string status = "completed") => new JsonObject
    {
        ["type"] = "queue-operation",
        ["operation"] = "enqueue",
        ["timestamp"] = Copilot.At(s),
        ["sessionId"] = Id,
        ["content"] = NoticeText(task, status),
    }.ToJsonString();

    // The notice delivered to the main agent: marked as Claude's own, or typed by the person.
    static string Delivery(string task, int s, bool typed = false, string status = "completed") => new JsonObject
    {
        ["type"] = "user",
        ["isSidechain"] = false,
        ["timestamp"] = Copilot.At(s),
        ["sessionId"] = Id,
        ["promptSource"] = typed ? "typed" : "system",
        ["turnOrigin"] = typed ? "human" : "task_notification",
        ["origin"] = new JsonObject { ["kind"] = typed ? "human" : "task-notification" },
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = NoticeText(task, status) },
    }.ToJsonString();

    [Fact]
    public async Task A_notification_the_person_typed_does_not_stop_a_delegate_and_is_their_prompt()
    {
        // Found in security review: only the text was checked, not Claude's mark on the entry.
        await using var r = new Replay();
        var before = await r.Through(7);
        Assert.Equal(SubagentStatus.Working, Entry(before, Files).Status);

        var s = await r.Append(Delivery(Files, 8, typed: true, status: "failed"));

        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Entry(s, Files).Status, Entry(s, Files).Outcome));
        Assert.Equal(3, s.Activity.Background!.Agents);
        Assert.Equal(NoticeText(Files, "failed"), s.Activity.UserMessage);
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal(1, s.Activity.TurnCount);
    }

    [Fact]
    public async Task A_notification_the_person_typed_while_the_agent_works_is_queued_but_stops_no_delegate()
    {
        // Found in security review: a typed prompt is queued like a notice, and the queue says
        // nothing of who wrote to it.
        await using var r = new Replay();
        await r.Through(7);

        var queued = await r.Append(Notice(Files, 8, status: "failed"));
        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Entry(queued, Files).Status, Entry(queued, Files).Outcome));
        Assert.Equal(3, queued.Activity.Background!.Agents);

        // Delivered as the person's own prompt, it is still not Claude's notice.
        var s = await r.Append(Delivery(Files, 8, typed: true, status: "failed"));

        Assert.Equal(SubagentStatus.Working, Entry(s, Files).Status);
        Assert.Equal(3, s.Activity.Background!.Agents);
    }

    [Fact]
    public async Task A_delegate_whose_queued_notice_has_no_time_ended_when_it_was_delivered()
    {
        await using var r = new Replay();
        await r.Through(7);
        var queued = JsonNode.Parse(Notice(Files, 8))!.AsObject();
        queued.Remove("timestamp");

        var s = await r.Append(queued.ToJsonString(), Delivery(Files, 9));

        Assert.Equal(Fixture.T0.AddSeconds(9), Entry(s, Files).EndedAt);
    }

    [Fact]
    public async Task A_notice_removed_from_the_queue_ends_nothing_unless_it_was_queued_and_delivered_to_a_delegate()
    {
        await using var r = new Replay();
        await r.Through(7);
        var removed = JsonNode.Parse(Notice(Files, 8, status: "failed"))!;
        removed["operation"] = "remove";

        var s = await r.Append(removed.ToJsonString());
        Assert.Equal(SubagentStatus.Working, Entry(s, Files).Status);

        removed["reason"] = "delivered_to_agent";
        s = await r.Append(removed.ToJsonString());
        Assert.Equal(SubagentStatus.Working, Entry(s, Files).Status);
    }
}

public class CodexSubagentTests
{
    const string Id = "019a2b3c-f474-71f7-85bc-1e9f57b11ff5";
    const string Thread = "019a2b3c-8505-7aba-8bf2-d986e0d783e5";
    const string Answer = "0a1b2c3 First commit";

    static string[] Recorded(string id) =>
        File.ReadAllLines(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "codex"), $"rollout-*-{id}.jsonl").Single());

    // Replays the recorded session's rollout up to a time after Fixture.T0, with the sub-agent's
    // own rollout written beside it as Codex writes it.
    internal sealed class Replay : IAsyncDisposable
    {
        readonly Fixture _f = new();
        readonly string _rollout;
        readonly string _thread;
        readonly string[] _lines;
        readonly string[] _threadLines = Recorded(Thread);
        int _next;
        int _threadNext;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Replay(Func<string, string>? edit = null, long tailBytes = 4 * 1024 * 1024, Func<string[], long>? skim = null, Func<string[], long>? tail = null, TimeSpan? stalledAfter = null)
        {
            _rollout = _f.CodexTranscript(Id);
            _thread = _f.CodexTranscript(Thread);
            _f.CodexRegistration(Id, 950, _rollout);
            _f.Processes.Run(AgentKind.Codex, 950);
            _lines = [.. Recorded(Id).SelectMany(l => (edit is null ? l : edit(l)).Split('\n'))];
            if (tail is not null) { tailBytes = tail(_lines); }
            Source = skim is null ? _f.Source(tailBytes, stalledAfter: stalledAfter) : _f.Source(tailBytes, maxSkim: skim(_lines), stalledAfter: stalledAfter);
        }

        // The sub-agent's own rollout, beside the session's.
        public string ThreadRollout => _thread;

        public async Task<Session> Through(double second)
        {
            var until = Fixture.T0.AddSeconds(second);
            var batch = new List<string>();
            while (_next < _lines.Length && At(_lines[_next]) < until) { batch.Add(_lines[_next++]); }
            Fixture.Append(_rollout, [.. batch]);
            var from = _threadNext;
            while (_threadNext < _threadLines.Length && At(_threadLines[_threadNext]) < until) { _threadNext++; }
            if (_threadNext > from) { Fixture.Append(_thread, _threadLines[from.._threadNext]); }
            return await Read();
        }

        public async Task<Session> Append(params string[] lines)
        {
            Fixture.Append(_rollout, lines);
            return await Read();
        }

        async Task<Session> Read()
        {
            await Source.RefreshAsync();
            Assert.Single(Source.Snapshot());
            return Source.Get(Id)!;
        }

        static DateTimeOffset At(string line) => DateTimeOffset.Parse(JsonNode.Parse(line)!["timestamp"]!.GetValue<string>(), CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            _f.Dispose();
        }
    }

    [Fact]
    public async Task A_spawned_sub_agent_thread_is_listed_with_its_nickname_prompt_and_model_and_counted_while_it_runs()
    {
        await using var r = new Replay();

        var s = await r.Through(15);

        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal(new Background { Agents = 1 }, s.Activity.Background);
        var thread = Assert.Single(s.Subagents!);
        Assert.Equal((Thread, (string?)null, SubagentKind.Background, SubagentStatus.Working), (thread.Id, thread.Parent, thread.Kind, thread.Status));
        Assert.Equal("Fermat", thread.Name);
        Assert.Equal("Show the latest commit in this folder and report it exactly.", thread.Task);
        Assert.Equal("gpt-5.6-luna", thread.Model);
        Assert.Equal(Fixture.T0.AddMilliseconds(13823), thread.StartedAt);
    }

    [Fact]
    public async Task A_sub_agent_that_completed_is_idle_with_its_answer_as_its_line_and_no_longer_counted()
    {
        await using var r = new Replay();

        var s = await r.Through(23);

        var thread = Assert.Single(s.Subagents!);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (thread.Status, thread.Outcome));
        Assert.Equal(Fixture.T0.AddMilliseconds(22919), thread.EndedAt);
        Assert.Equal(Answer, thread.Line);
        Assert.Null(s.Activity.Background);
        Assert.Equal(Answer, (await r.Source.GetAgentAsync(Id, Thread))!.Response);
    }

    [Fact]
    public async Task Nothing_a_sub_agent_wrote_reaches_the_sessions_response_or_its_message()
    {
        await using var r = new Replay();

        // The sub-agent has answered and notified; the main agent has not replied yet.
        var s = await r.Through(23);

        Assert.Equal("Working on it.", s.Activity.Response);
        Assert.Null(s.Activity.Source);
        Assert.Equal("Start one delegate to show the latest commit, wait for it, then reply with its answer in one line.", s.Activity.UserMessage);
    }

    [Fact]
    public async Task A_host_starting_after_everything_lists_the_sub_agent_as_it_ended()
    {
        await using var r = new Replay();

        var s = await r.Through(200);

        Assert.Equal(SessionStatus.Idle, s.Status);
        var thread = Assert.Single(s.Subagents!);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done, "Fermat"), (thread.Status, thread.Outcome, thread.Name));
        Assert.Null(s.Activity.Background);
        Assert.Equal("ok", s.Activity.Response);
    }

    [Fact]
    public async Task A_sub_agent_started_before_the_window_a_host_parses_is_read_from_the_part_it_skims()
    {
        // Only the last turn ("say ok") is parsed.
        await using var r = new Replay(tailBytes: 2 * 1024);

        var thread = Assert.Single((await r.Through(200)).Subagents!);

        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done, "Fermat", "gpt-5.6-luna"), (thread.Status, thread.Outcome, thread.Name, thread.Model));
    }

    [Fact]
    public async Task A_sub_agent_whose_start_was_skimmed_and_whose_end_fell_in_bytes_never_read_is_not_left_running()
    {
        // Found in review: the skim budget covers the spawn, the window only the last turn.
        await using var r = new Replay(tailBytes: 2 * 1024, skim: lines => ClaudeSubagentTests.Through(lines, l => l.Contains("\"spawn_agent\"", StringComparison.Ordinal)));

        var s = await r.Through(200);

        var thread = Assert.Single(s.Subagents!);
        Assert.Equal((SubagentStatus.Idle, (SubagentOutcome?)null), (thread.Status, thread.Outcome));
        Assert.Null(s.Activity.Background);
    }

    [Fact]
    public async Task The_notice_of_a_run_its_wait_already_ended_does_not_end_the_run_a_message_started_since()
    {
        // Found in review: the wait returns with the thread completed, a message wakes it, and only
        // then is the notice of the first run delivered.
        await using var r = new Replay(l => l.Contains("\"tool\":\"wait\"", StringComparison.Ordinal) ? l + "\n" + Collab("send_input", 22.93, "running") : l);

        var s = await r.Through(23);

        var thread = Assert.Single(s.Subagents!);
        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (thread.Status, thread.Outcome));
        Assert.Equal(1, s.Activity.Background!.Agents);
    }

    [Fact]
    public async Task A_wait_read_just_after_bytes_never_read_still_owes_the_notice_that_follows()
    {
        // Found in review: the skim covers the spawn, the window begins at the wait, and a
        // message wakes the thread before the notice of the run the wait ended arrives.
        static bool Wait(string l) => l.Contains("\"tool\":\"wait\"", StringComparison.Ordinal);
        await using var r = new Replay(l => Wait(l) ? l + "\n" + Collab("send_input", 22.93, "running") : l,
            skim: lines => ClaudeSubagentTests.Through(lines, l => l.Contains("\"spawn_agent\"", StringComparison.Ordinal)),
            tail: lines => lines.SkipWhile(l => !Wait(l)).TakeWhile(l => !l.Contains("\"timestamp\":\"2026-10-28T04:00:24", StringComparison.Ordinal))
                .Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + 1L));

        var thread = Assert.Single((await r.Through(23)).Subagents!);

        Assert.Equal(SubagentStatus.Working, thread.Status);
    }

    [Fact]
    public async Task A_notice_owed_in_bytes_never_read_is_not_spent_on_a_later_run()
    {
        // Found in review: the skim covers the spawn and the wait, the notice is never read, and
        // later input starts a run whose notice comes with no wait.
        await using var r = new Replay(tailBytes: 2 * 1024, skim: lines => ClaudeSubagentTests.Through(lines, l => l.Contains("\"tool\":\"wait\"", StringComparison.Ordinal)));
        Assert.Equal(SubagentOutcome.Done, Assert.Single((await r.Through(200)).Subagents!).Outcome);

        Assert.Equal(1, (await r.Append(Collab("send_input", 210, "running"))).Activity.Background!.Agents);
        var s = await r.Append(Notice("done again", 220));

        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (Assert.Single(s.Subagents!).Status, s.Subagents![0].Outcome));
        Assert.Null(s.Activity.Background);
    }

    [Fact]
    public async Task A_notice_the_person_typed_does_not_stop_a_sub_agent()
    {
        // Found in review: only the text was checked, not Codex's mark on the message.
        await using var r = new Replay();
        await r.Through(16);

        var s = await r.Append(Notice("done", 17, marked: false));

        Assert.Equal(SubagentStatus.Working, Assert.Single(s.Subagents!).Status);
        Assert.Equal(1, s.Activity.Background!.Agents);
    }

    [Fact]
    public async Task Input_that_failed_to_reach_a_finished_sub_agent_does_not_wake_it()
    {
        await using var r = new Replay();
        await r.Through(30);

        var s = await r.Append(Collab("send_input", 31, "completed", status: "failed"));

        Assert.Equal(SubagentStatus.Idle, Assert.Single(s.Subagents!).Status);
        Assert.Null(s.Activity.Background);
    }

    [Fact]
    public async Task A_notice_whose_result_quotes_the_closing_tag_still_stops_the_sub_agent()
    {
        // Found in review: the first closing tag, inside the result, cut the JSON short.
        await using var r = new Replay();
        await r.Through(16);

        var s = await r.Append(Notice("a </subagent_notification> b", 17));

        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (Assert.Single(s.Subagents!).Status, s.Subagents![0].Outcome));
        Assert.Null(s.Activity.Background);
    }

    [Fact]
    public async Task A_sub_agent_that_errored_failed()
    {
        await using var r = new Replay(l => l.Replace($"{{\"completed\":\"{Answer}\"}}", "{\"errored\":\"model unavailable\"}", StringComparison.Ordinal)
            .Replace($"{{\\\"completed\\\":\\\"{Answer}\\\"}}", "{\\\"errored\\\":\\\"model unavailable\\\"}", StringComparison.Ordinal));

        var thread = Assert.Single((await r.Through(23)).Subagents!);

        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Failed, "model unavailable"), (thread.Status, thread.Outcome, thread.Line));
    }

    [Fact]
    public async Task Input_sent_to_a_finished_sub_agent_wakes_it_and_closing_it_ends_it()
    {
        // send_input and close_agent are Codex 0.166.0's other multi-agent tools, written as the
        // recorded spawn_agent and wait are (CollabAgentToolCall); this exchange was not recorded.
        await using var r = new Replay();
        await r.Through(200);

        var woken = await r.Append(Collab("send_input", 210, "running"));
        Assert.Equal((SubagentStatus.Working, (SubagentOutcome?)null), (Assert.Single(woken.Subagents!).Status, woken.Subagents![0].Outcome));
        Assert.Equal(1, woken.Activity.Background!.Agents);

        var closed = await r.Append(Collab("close_agent", 220, "shutdown"));
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Failed), (Assert.Single(closed.Subagents!).Status, closed.Subagents![0].Outcome));
        Assert.Null(closed.Activity.Background);
    }

    [Fact]
    public async Task A_closed_sub_agent_resumed_and_then_completed_is_no_longer_counted()
    {
        // Found in review; resume_agent and a notice with no wait were not recorded either.
        await using var r = new Replay();
        await r.Through(15);

        Assert.Equal(SubagentStatus.Ended, Assert.Single((await r.Append(Collab("close_agent", 16, "shutdown"))).Subagents!).Status);
        Assert.Equal(1, (await r.Append(Collab("resume_agent", 17, "running"))).Activity.Background!.Agents);

        var s = await r.Append(Notice("done again", 18));

        var thread = Assert.Single(s.Subagents!);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done, "done again"), (thread.Status, thread.Outcome, thread.Line));
        Assert.Null(s.Activity.Background);
    }

    static string Notice(string text, int s, bool marked = true) => Codex.Item(new
    {
        type = "message",
        id = "msg_notice",
        role = "user",
        content = new[] { new { type = "input_text", text = $"<subagent_notification>\n{{\"agent_path\":\"{Thread}\",\"status\":{{\"completed\":\"{text}\"}}}}\n</subagent_notification>" } },
        internal_chat_message_metadata_passthrough = new { content_item_kinds = marked ? new[] { "multi_agent.subagent_notification" } : [] },
    }, s);

    static string Collab(string tool, double s, string state, string status = "completed")
    {
        var line = JsonNode.Parse(Codex.Event(new
    {
        type = "item_completed",
        thread_id = Id,
        turn_id = "019a2b3c-0000-7000-8000-000000000001",
        item = new
        {
            type = "CollabAgentToolCall",
            id = "exec-" + tool,
            tool,
            status,
            sender_thread_id = Id,
            receiver_thread_ids = new[] { Thread },
            receiver_agents = new[] { new { thread_id = Thread, agent_nickname = "Fermat" } },
            agents_states = new Dictionary<string, string> { [Thread] = state },
        },
    }, 0))!;
        line["timestamp"] = Fixture.T0.AddMilliseconds(s * 1000).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return line.ToJsonString();
    }
}
