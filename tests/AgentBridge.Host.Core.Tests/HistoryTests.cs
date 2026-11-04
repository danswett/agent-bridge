using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Conversation;
using AgentBridge.Protocol;
using Xunit;

// session.history (docs/protocol/v1-history.md) read from synthetic transcripts by the real
// SessionSource and its HistoryReader: paging, the start of a session, stable ids that match
// activity.turn, what a turn holds for each agent, the cursor, the caps and the typed states.
public class HistoryTests
{
    const string Id = "55555555-5555-4555-8555-555555555555";

    sealed class Copilot_ : IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly string Events;
        public SessionSource Source;
        readonly string? _key;

        public Copilot_(string? keyFile = null, Func<SessionSourceOptions, SessionSourceOptions>? options = null)
        {
            Events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            _key = keyFile ?? Path.Combine(F.Root, "history-cursor.key");
            _options = options ?? (o => o);
            Source = Make();
        }

        readonly Func<SessionSourceOptions, SessionSourceOptions> _options;

        SessionSource Make()
        {
            var o = F.Options();
            return new SessionSource(_options(new SessionSourceOptions
            {
                CopilotHome = o.CopilotHome, ClaudeHome = o.ClaudeHome, CodexHome = o.CodexHome,
                ClaudeRegistrationRoots = o.ClaudeRegistrationRoots, CodexRegistrationRoots = o.CodexRegistrationRoots,
                UseFileSystemWatcher = false, PollInterval = o.PollInterval, InitialTailBytes = o.InitialTailBytes,
                HistoryKeyFile = _key,
            }), F.Processes, F.Time);
        }

        // A new host: the same profile folder, so the same cursor key.
        public async Task RestartAsync()
        {
            await Source.DisposeAsync();
            Source = Make();
            await Source.RefreshAsync();
        }

        public async Task<Session> After(params string[] lines)
        {
            Fixture.Append(Events, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        public Task<HistoryResult> Page(string? before = null, int limit = HistoryLimits.DefaultTurns) => Source.HistoryAsync(Id, before, limit);

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            F.Dispose();
        }
    }

    static string Prompt(HistoryTurn t) => t.Prompt!.Text;

    static string Scheduled(string text, int s, string source) =>
        Copilot.Event("user.message", new { content = text, source, attachments = Array.Empty<object>(), delivery = "idle" }, s);

    [Fact]
    public async Task A_session_pages_back_oldest_first_to_the_start_of_the_session()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.Event("session.start", new { selectedModel = "m" }, 0),
            Copilot.User("one", 1), Copilot.Assistant("first", 2),
            Copilot.User("two", 3), Copilot.Assistant("second", 4),
            Copilot.User("three", 5), Copilot.Assistant("third", 6));

        var newest = await s.Page(limit: 2);
        Assert.Equal(["two", "three"], newest.Turns!.Select(Prompt));
        Assert.Equal("third", newest.Turns![1].Parts.Single(p => p.Kind == HistoryPartKind.Response).Text);
        Assert.NotNull(newest.Before);
        Assert.Null(newest.Start);

        var earlier = await s.Page(newest.Before, limit: 2);
        Assert.Equal(["one"], earlier.Turns!.Select(Prompt));
        Assert.Null(earlier.Before);
        Assert.Equal(Fixture.T0, earlier.Start!.At);
        Assert.Null(earlier.Partial);
    }

    [Fact]
    public async Task The_latest_turns_id_is_the_one_activity_carries_and_a_new_prompt_moves_it()
    {
        await using var s = new Copilot_();
        var first = await s.After(Copilot.User("one", 1), Copilot.Assistant("first", 2));
        var page = await s.Page();
        Assert.Equal(page.Turns!.Single().Id, first.Activity.Turn);

        var second = await s.After(Copilot.User("two", 3));
        Assert.NotEqual(first.Activity.Turn, second.Activity.Turn);
        var again = await s.Page();
        Assert.Equal([first.Activity.Turn, second.Activity.Turn], again.Turns!.Select(t => t.Id));
    }

    [Fact]
    public async Task A_turn_whose_prompt_the_host_only_skimmed_has_the_same_id_live_and_in_history()
    {
        // A window of a few bytes: the prompt is in the skimmed part of the transcript.
        await using var s = new Copilot_(options: o => new SessionSourceOptions
        {
            CopilotHome = o.CopilotHome, ClaudeHome = o.ClaudeHome, CodexHome = o.CodexHome, UseFileSystemWatcher = false,
            InitialTailBytes = 200, HistoryKeyFile = o.HistoryKeyFile,
        });
        var session = await s.After(Copilot.User("long ago", 1), Copilot.Assistant(new string('x', 2000), 2), Copilot.Assistant("lately", 3));
        Assert.Equal((await s.Page()).Turns!.Single().Id, session.Activity.Turn);
    }

    [Fact]
    public async Task Scheduled_and_agent_sent_prompts_keep_who_sent_them_and_injected_context_starts_no_turn()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("mine", 1),
            Scheduled("[Scheduled prompt #4] Check CI", 2, "schedule-4"),
            Scheduled("Carry on", 3, "agent-7c1d"),
            // Context the agent was given, not a prompt: it stays in the turn before it.
            Scheduled("Remember the house style", 4, "memory"),
            Scheduled("<shell completed>", 5, "skill-review"),
            Copilot.Assistant("done", 6));

        var turns = (await s.Page()).Turns!;
        Assert.Equal(3, turns.Count);
        Assert.Null(turns[0].Prompt!.From);
        Assert.Equal((UserMessageFrom.Schedule, "4"), (turns[1].Prompt!.From!.Value, turns[1].Prompt!.Label));
        Assert.Equal(UserMessageFrom.Agent, turns[2].Prompt!.From);
        Assert.Equal("done", turns[2].Parts.Single().Text);
    }

    [Fact]
    public async Task A_copilot_turn_holds_its_reasoning_tools_questions_approvals_and_delegate_events_in_order()
    {
        await using var s = new Copilot_();
        var session = await s.After(
            Copilot.User("ship it", 1),
            Copilot.Assistant("Asking first.", 2, reasoning: "Need a branch."),
            Copilot.ToolStart("q1", "ask_user", new { message = "Which branch?", requestedSchema = new { properties = new { branch = new { type = "string", title = "Branch" } } } }, 3),
            Copilot.Event("human_response.recorded", new { toolCallId = "q1", requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content = new { branch = "main" } } }, 4),
            Copilot.ToolDone("q1", 4),
            Copilot.ToolStart("t1", "task", new { description = "Run the suite", prompt = "Run every test" }, 5),
            Copilot.SubagentStarted("t1", "agent-1", 5),
            Copilot.ToolStart("c1", "powershell", new { command = "git push", description = "Push" }, 6),
            Copilot.Permission("req1", "c1", Copilot.Prompts["commands"], 6),
            Copilot.PermissionDone("req1", "c1", "approved", 7),
            Copilot.ToolDone("c1", 8),
            Copilot.ToolStart("c2", "powershell", new { command = "rm -r", description = "Clean" }, 9),
            Copilot.Permission("req2", "c2", Copilot.Prompts["commands"], 9),
            Copilot.PermissionDone("req2", "c2", "denied-interactively-by-user", 10),
            Copilot.ToolDone("c2", 10, success: false),
            // A delegate's own words and tools are its own, not the main agent's.
            Copilot.Assistant("delegate talk", 11, agentId: "agent-1"),
            Copilot.ToolStart("d1", "view", new { path = "x" }, 11, agentId: "agent-1"),
            // Copilot says a background delegate's run ended twice: once in history.
            Copilot.SubagentCompleted("t1", "agent-1", 12),
            Copilot.AgentIdle("agent-1", 12),
            Copilot.Assistant("Pushed.", 13));

        var turn = (await s.Page()).Turns!.Single();
        Assert.Equal(session.Activity.Turn, turn.Id);
        Assert.Equal(
            ["reasoning", "response", "tool", "question", "tool", "delegate", "tool", "question", "tool", "question", "delegate", "response"],
            turn.Parts.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Kind.ToString())));
        var asked = turn.Parts.Single(p => p.Question?.Id == "q1");
        Assert.Equal((QuestionOutcome.Answered, "branch: main"), (asked.Outcome!.Value, asked.Answer));
        var approvals = turn.Parts.Where(p => p.Question?.Kind == QuestionKind.Approval).ToList();
        Assert.Equal([QuestionOutcome.Approved, QuestionOutcome.Denied], approvals.Select(p => p.Outcome!.Value));
        var delegates = turn.Parts.Where(p => p.Delegate is not null).Select(p => p.Delegate!).ToList();
        Assert.Equal((DelegateEventKind.Started, "Run the suite", SubagentKind.Background), (delegates[0].Event, delegates[0].Name, delegates[0].Kind!.Value));
        Assert.Equal((DelegateEventKind.Reported, SubagentOutcome.Done), (delegates[1].Event, delegates[1].Outcome!.Value));
        Assert.DoesNotContain(turn.Parts, p => p.Text == "delegate talk" || p.Tool?.Id == "d1");
        // Every part id is unique within the turn and stable on a second read.
        Assert.Equal(turn.Parts.Count, turn.Parts.Select(p => p.Id).Distinct().Count());
        Assert.Equal(turn.Parts.Select(p => p.Id), (await s.Page()).Turns!.Single().Parts.Select(p => p.Id));
    }

    [Fact]
    public async Task An_earlier_turns_open_question_is_cancelled_and_its_tool_failed_while_the_latest_turns_stay_open()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("one", 1),
            Copilot.ToolStart("q1", "ask_user", new { message = "Sure?" }, 2),
            Copilot.User("two", 3),
            Copilot.ToolStart("q2", "ask_user", new { message = "Really?" }, 4));

        var turns = (await s.Page()).Turns!;
        var earlier = turns[0].Parts;
        Assert.Equal(QuestionOutcome.Cancelled, earlier.Single(p => p.Kind == HistoryPartKind.Question).Outcome);
        Assert.Equal(ToolStatus.Failed, earlier.Single(p => p.Kind == HistoryPartKind.Tool).Tool!.Status);
        var latest = turns[1].Parts;
        Assert.Equal(QuestionOutcome.Unknown, latest.Single(p => p.Kind == HistoryPartKind.Question).Outcome);
        Assert.Equal(ToolStatus.Running, latest.Single(p => p.Kind == HistoryPartKind.Tool).Tool!.Status);
    }

    [Fact]
    public async Task A_cursor_survives_the_transcript_growing_and_the_host_restarting()
    {
        await using var s = new Copilot_();
        await s.After(Copilot.User("one", 1), Copilot.User("two", 2), Copilot.User("three", 3));
        var newest = await s.Page(limit: 1);

        await s.After(Copilot.User("four", 4));
        await s.RestartAsync();

        var earlier = await s.Page(newest.Before, limit: 5);
        Assert.Equal(["one", "two"], earlier.Turns!.Select(Prompt));
        Assert.Equal(Fixture.T0.AddSeconds(1), earlier.Start!.At);
    }

    [Fact]
    public async Task A_forged_cut_foreign_or_outdated_cursor_is_stale_never_a_page_from_elsewhere()
    {
        await using var s = new Copilot_();
        await s.After(Copilot.User("one", 1), Copilot.User("two", 2), Copilot.User("three", 3));
        var before = (await s.Page(limit: 1)).Before!;

        var flipped = before[..^2] + (before[^2] == 'A' ? 'B' : 'A') + before[^1];
        foreach (var bad in new[] { "garbage", before[..^4], flipped, "h1." + new string('A', 73) })
        {
            Assert.True((await s.Page(bad)).Stale, bad);
        }

        // A cursor sealed with the same key names a turn of another transcript.
        await using var other = new Copilot_(keyFile: Path.Combine(s.F.Root, "history-cursor.key"));
        await other.After(Copilot.User("not one", 1), Copilot.User("not two", 2), Copilot.User("not three", 3));
        Assert.True((await other.Page(before)).Stale);

        // Another host's key.
        await using var elsewhere = new Copilot_(keyFile: Path.Combine(s.F.Root, "another.key"));
        Fixture.Append(elsewhere.Events, File.ReadAllLines(s.Events));
        await elsewhere.Source.RefreshAsync();
        Assert.True((await elsewhere.Page(before)).Stale);

        // The transcript replaced by another.
        File.WriteAllText(s.Events, "");
        await s.After(Copilot.User("new one", 9), Copilot.User("new two", 10), Copilot.User("new three", 11));
        Assert.True((await s.Page(before)).Stale);
    }

    [Fact]
    public async Task A_transcript_that_cannot_be_found_is_unavailable_and_a_session_not_listed_is_missing()
    {
        await using var s = new Copilot_();
        await s.Source.RefreshAsync();
        Assert.Equal(HistoryReader.NotFound, (await s.Page()).Unavailable);
        Assert.True((await s.Source.HistoryAsync("not-a-session", null, 20)).Missing);
    }

    [Fact]
    public async Task A_transcript_with_no_prompt_yet_is_an_empty_history_at_the_start_of_the_session()
    {
        await using var s = new Copilot_();
        await s.After(Copilot.Event("session.start", new { selectedModel = "m" }, 0));
        var page = await s.Page();
        Assert.Empty(page.Turns!);
        Assert.Equal(Fixture.T0, page.Start!.At);
        Assert.Null(page.Before);
    }

    [Fact]
    public async Task A_turn_that_cannot_be_read_is_marked_in_its_place_and_its_page_partial()
    {
        await using var s = new Copilot_();
        await s.After(Copilot.User("one", 1), "{\"type\":\"user.message\",\"data\":{\"content\":", Copilot.Assistant("after it", 3), Copilot.User("three", 4));

        var page = await s.Page();
        Assert.True(page.Partial);
        Assert.Equal(3, page.Turns!.Count);
        var unreadable = page.Turns[1];
        Assert.True(unreadable.Unreadable);
        Assert.Null(unreadable.Prompt);
        Assert.Empty(unreadable.Parts);
        Assert.Null(unreadable.Truncated);
        Assert.Equal(Fixture.T0.AddSeconds(3), unreadable.At);
        Assert.Equal(["one", "three"], new[] { page.Turns[0], page.Turns[2] }.Select(Prompt));
    }

    [Fact]
    public async Task A_turn_over_its_budget_loses_tools_reasoning_and_earlier_replies_first_and_says_so()
    {
        await using var s = new Copilot_();
        var lines = new List<string> { Copilot.User("big", 1) };
        for (var i = 0; i < 60; i++)
        {
            lines.Add(Copilot.Assistant($"reply {i} " + new string('r', 2000), 2, reasoning: new string('t', 3000)));
            lines.Add(Copilot.ToolStart($"c{i}", "powershell", new { command = "dir", description = "List " + i }, 2));
        }
        lines.Add(Copilot.Assistant("the last word", 3));
        await s.After([.. lines]);

        var turn = (await s.Page()).Turns!.Single();
        Assert.True(ProtocolJson.ByteCount(turn) <= HistoryLimits.MaxTurnBytes);
        Assert.True(turn.Truncated);
        Assert.True(turn.PartsMore > 0);
        Assert.Equal("the last word", turn.Parts.Last(p => p.Kind == HistoryPartKind.Response).Text);
        Assert.DoesNotContain(turn.Parts, p => p.Kind == HistoryPartKind.Tool);
    }

    [Fact]
    public async Task A_page_holds_only_the_turns_that_fit_one_message_and_the_rest_come_next()
    {
        await using var s = new Copilot_();
        var lines = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            lines.Add(Copilot.User("prompt " + i, i * 2 + 1));
            lines.Add(Copilot.Assistant(new string((char)('a' + i), 15 * 1024), i * 2 + 2));
        }
        await s.After([.. lines]);

        var seen = new List<string>();
        string? before = null;
        do
        {
            var page = await s.Page(before);
            Assert.True(ProtocolJson.ByteCount(page) + 64 <= ProtocolJson.MaxMessageBytes);
            Assert.True(page.Turns!.Count is > 0 and < 6);
            seen.InsertRange(0, page.Turns.Select(Prompt));
            before = page.Before;
        }
        while (before is not null);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => "prompt " + i), seen);
    }

    [Fact]
    public async Task A_turn_longer_than_the_host_reads_keeps_its_prompt_and_its_end()
    {
        await using var s = new Copilot_(options: o => new SessionSourceOptions
        {
            CopilotHome = o.CopilotHome, ClaudeHome = o.ClaudeHome, CodexHome = o.CodexHome, UseFileSystemWatcher = false,
            HistoryKeyFile = o.HistoryKeyFile, HistoryBudgets = new HistoryBudgets { TurnBytes = 4096 },
        });
        var lines = new List<string> { Copilot.User("long one", 1), Copilot.Assistant("early on", 2) };
        for (var i = 0; i < 20; i++) { lines.Add(Copilot.ToolResult("c" + i, new string('z', 1000), 3)); }
        lines.Add(Copilot.Assistant("at the end", 4));
        await s.After([.. lines]);

        var page = await s.Page();
        var turn = page.Turns!.Single();
        Assert.Equal("long one", Prompt(turn));
        Assert.True(turn.Truncated);
        Assert.True(page.Partial);
        Assert.Equal(["at the end"], turn.Parts.Where(p => p.Kind == HistoryPartKind.Response).Select(p => p.Text));
    }

    [Fact]
    public async Task History_stops_at_the_newest_500_turns()
    {
        await using var s = new Copilot_();
        await s.After([.. Enumerable.Range(1, HistoryLimits.Kept + 3).Select(i => Copilot.User("p" + i, i))]);

        var served = 0;
        string? before = null;
        HistoryResult page;
        do
        {
            page = await s.Page(before, limit: HistoryLimits.MaxTurns);
            served += page.Turns!.Count;
            before = page.Before;
        }
        while (before is not null);
        Assert.Equal(HistoryLimits.Kept, served);
        Assert.True(page.Start!.Limited);
        Assert.Null(page.Start.At);
    }

    // ---- Claude and Codex ----

    [Fact]
    public async Task A_claude_turn_holds_its_thinking_replies_tools_answered_question_and_delegates()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        Fixture.Append(transcript,
            Claude.User("plan it", 1),
            Claude.AssistantMessage("msg_1", 2, null, Claude.Thinking("Thinking it through.")),
            Claude.AssistantMessage("msg_1", 2, null, Claude.Text("Part one.")),
            Claude.AssistantMessage("msg_1", 2, "tool_use", Claude.Text("Part two."), Claude.ToolUse("ask1", "AskUserQuestion", Claude.Ask(("Which way?", false, ["Left", "Right"])))),
            Claude.Answered("ask1", 3, new Dictionary<string, string> { ["Which way?"] = "Left" }),
            Claude.Assistant(4, "tool_use", Claude.ToolUse("agent1", "Agent", new JsonObject { ["description"] = "Explore the code", ["prompt"] = "look" })),
            LaunchedAgent("agent1", "a1b2", "Explore the code", 5),
            Notice("<task-notification>\n<task-id>a1b2</task-id>\n<status>completed</status>\n</task-notification>", 6),
            Claude.Assistant(7, "end_turn", Claude.Text("All done.")),
            Claude.User("next", 8));
        await using var source = f.Source();
        await source.RefreshAsync();
        var session = source.Get(Id)!;

        var page = await source.HistoryAsync(Id, null, 20);
        Assert.Equal(["plan it", "next"], page.Turns!.Select(Prompt));
        Assert.Equal(session.Activity.Turn, page.Turns![1].Id);
        var parts = page.Turns[0].Parts;
        Assert.Equal(["reasoning", "response", "tool", "question", "tool", "delegate", "delegate", "response"], parts.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Kind.ToString())));
        Assert.Equal("Part one.\n\nPart two.", parts[1].Text);
        Assert.Equal(QuestionOutcome.Answered, parts[3].Outcome);
        Assert.Contains("Left", parts[3].Answer);
        Assert.Equal([(DelegateEventKind.Started, "Explore the code"), (DelegateEventKind.Reported, "Explore the code")], parts.Where(p => p.Delegate is not null).Select(p => (p.Delegate!.Event, p.Delegate.Name)));
        Assert.Equal(SubagentOutcome.Done, parts[6].Delegate!.Outcome);
    }

    // A notice Claude delivered and marked as its own: a delegate stopped.
    static string Notice(string text, int s)
    {
        var e = JsonNode.Parse(Claude.User(text, s))!.AsObject();
        e["origin"] = new JsonObject { ["kind"] = "task-notification" };
        return e.ToJsonString();
    }

    static string LaunchedAgent(string toolUseId, string agentId, string description, int s)
    {
        var e = JsonNode.Parse(Claude.ToolResult(toolUseId, s))!.AsObject();
        e["toolUseResult"] = new JsonObject { ["status"] = "async_launched", ["agentId"] = agentId, ["description"] = description, ["prompt"] = "look" };
        return e.ToJsonString();
    }

    [Fact]
    public async Task A_codex_turn_holds_its_reply_reasoning_commands_and_an_approval_its_command_then_ran()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout, updated: Fixture.T0);
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout,
            Codex.Line("session_meta", new { id = Id, timestamp = Copilot.At(0), cwd = "/work/codex" }, 0),
            Codex.Event(new { type = "task_started" }, 1),
            Codex.Event(new { type = "user_message", message = "count the files" }, 1),
            Codex.Event(new { type = "agent_reasoning", text = "Use ls." }, 2),
            Codex.Event(new { type = "exec_approval_request", call_id = "x1", command = new[] { "ls" }, reason = "list" }, 3),
            Codex.Event(new { type = "exec_command_begin", call_id = "x1", command = new[] { "ls" } }, 4),
            Codex.Event(new { type = "exec_command_end", call_id = "x1", exit_code = 0 }, 5),
            Codex.Event(new { type = "agent_message", message = "There are 3." }, 6),
            Codex.Event(new { type = "task_complete", last_agent_message = "There are 3." }, 6));
        await using var source = f.Source();
        await source.RefreshAsync();
        var session = source.Get(Id)!;

        var page = await source.HistoryAsync(Id, null, 20);
        var turn = page.Turns!.Single();
        Assert.Equal(session.Activity.Turn, turn.Id);
        Assert.Equal("count the files", Prompt(turn));
        Assert.Equal(Fixture.T0, page.Start!.At);
        Assert.Equal(["reasoning", "question", "tool", "response"], turn.Parts.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Kind.ToString())));
        Assert.Equal(QuestionOutcome.Approved, turn.Parts[1].Outcome);
        Assert.True(turn.Parts[1].Question!.AnswerAtMachine);
        Assert.Equal(ToolStatus.Done, turn.Parts[2].Tool!.Status);
    }

    // ---- the recorded sessions ----

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Each_recorded_session_reads_whole_with_its_live_turn_last(string agent)
    {
        using var f = new Fixture();
        var recorded = Path.Combine(AppContext.BaseDirectory, "fixtures", agent);
        string transcript;
        switch (agent)
        {
            case "copilot":
                transcript = f.CopilotSession(Id, 100);
                File.Copy(Path.Combine(recorded, "subagents.jsonl"), transcript);
                f.Processes.Run(AgentKind.Copilot, 100);
                break;
            case "claude":
                transcript = f.ClaudeTranscript(Id);
                Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
                File.Copy(Directory.GetFiles(recorded, "*.jsonl").Single(), transcript);
                f.ClaudeRegistration(Id, 900, transcript);
                f.Processes.Run(AgentKind.Claude, 900);
                break;
            default:
                transcript = f.CodexTranscript(Id);
                Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
                File.Copy(Directory.GetFiles(recorded, "*.jsonl").OrderBy(p => p, StringComparer.Ordinal).First(), transcript);
                f.CodexRegistration(Id, 950, transcript, updated: Fixture.T0);
                f.Processes.Run(AgentKind.Codex, 950);
                break;
        }
        await using var source = f.Source();
        await source.RefreshAsync();
        var session = source.Get(Id)!;

        var page = await source.HistoryAsync(Id, null, HistoryLimits.MaxTurns);
        Assert.NotEmpty(page.Turns!);
        Assert.NotNull(page.Start);
        Assert.Null(page.Partial);
        Assert.Equal(session.Activity.Turn, page.Turns!.Last().Id);
        Assert.Equal(page.Turns.Count, page.Turns.Select(t => t.Id).Distinct().Count());
        Assert.All(page.Turns, t => Assert.Equal(t.Parts.Count, t.Parts.Select(p => p.Id).Distinct().Count()));
        if (agent != "claude") { Assert.Contains(page.Turns.SelectMany(t => t.Parts), p => p.Delegate is { Event: DelegateEventKind.Started }); }
    }
}
