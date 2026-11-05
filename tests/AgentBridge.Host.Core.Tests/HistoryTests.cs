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
        Assert.True((await s.Page()).Partial);
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

    static Func<SessionSourceOptions, SessionSourceOptions> With(HistoryBudgets? budgets = null, int? maxLineBytes = null) => o => new SessionSourceOptions
    {
        CopilotHome = o.CopilotHome, ClaudeHome = o.ClaudeHome, CodexHome = o.CodexHome, UseFileSystemWatcher = false,
        HistoryKeyFile = o.HistoryKeyFile, HistoryBudgets = budgets ?? new HistoryBudgets(), MaxLineBytes = maxLineBytes ?? o.MaxLineBytes,
    };

    // Found in review of #292: only a line's first 4 KiB were searched, and JSON may put a record's
    // type after any amount of anything else. Read in chunks of a few bytes, the marks split
    // between chunks are found too.
    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(37)]
    public async Task A_prompt_whose_type_comes_after_a_long_property_still_starts_its_turn(int chunk)
    {
        await using var s = new Copilot_(options: With(new HistoryBudgets { ChunkBytes = chunk }));
        var late = new JsonObject
        {
            ["data"] = new JsonObject { ["content"] = "late " + new string('l', 6000), ["attachments"] = new JsonArray() },
            ["id"] = "late-1",
            ["timestamp"] = Copilot.At(3),
            ["type"] = "user.message",
        }.ToJsonString();
        var session = await s.After(Copilot.User("one", 1), Copilot.Assistant("first", 2), late, Copilot.Assistant("second", 4));

        var turns = (await s.Page()).Turns!;
        Assert.Equal(2, turns.Count);
        Assert.StartsWith("late ", Prompt(turns[1]));
        Assert.Equal("second", turns[1].Parts.Single().Text);
        Assert.Equal(session.Activity.Turn, turns[1].Id);
    }

    [Fact]
    public async Task A_record_that_may_start_a_turn_and_cannot_be_read_leaves_the_live_turn_unknown_until_the_next_prompt()
    {
        await using var s = new Copilot_();
        var session = await s.After(Copilot.User("one", 1), "{\"type\":\"user.message\",\"data\":{\"content\":", Copilot.Assistant("after it", 3));

        // History puts the reply under a turn it can't read; live does not put it under "one".
        Assert.Null(session.Activity.Turn);
        Assert.Null(session.Activity.ResponseTurn);
        Assert.True((await s.Page()).Turns![^1].Unreadable);

        var next = await s.After(Copilot.User("two", 4));
        Assert.Equal((await s.Page()).Turns![^1].Id, next.Activity.Turn);
    }

    [Fact]
    public async Task A_prompt_longer_than_the_host_reads_is_a_turn_neither_live_nor_history_can_read()
    {
        await using var s = new Copilot_(options: With(maxLineBytes: 2048));
        var session = await s.After(Copilot.User("one", 1), Copilot.User("huge " + new string('h', 3000), 2), Copilot.Assistant("after it", 3));

        Assert.Null(session.Activity.Turn);
        var page = await s.Page();
        Assert.True(page.Partial);
        Assert.Equal("one", Prompt(page.Turns![0]));
        Assert.True(page.Turns[1].Unreadable);

        var next = await s.After(Copilot.User("two", 4));
        Assert.Equal((await s.Page()).Turns![^1].Id, next.Activity.Turn);
    }

    // Found in review of #292: the forward look for the first prompt passed over a line too long
    // to read, so the page after it was taken for the start and the turn before was unreachable.
    [Fact]
    public async Task A_first_prompt_too_long_to_read_is_still_paged_back_to()
    {
        await using var s = new Copilot_(options: With(maxLineBytes: 2048));
        await s.After(Copilot.User("huge " + new string('h', 3000), 1), Copilot.User("two", 2), Copilot.User("three", 3));

        var newest = await s.Page(limit: 2);
        Assert.Equal(["two", "three"], newest.Turns!.Select(Prompt));
        Assert.Null(newest.Start);
        var earlier = await s.Page(newest.Before, limit: 2);
        Assert.True(earlier.Turns!.Single().Unreadable);
        Assert.NotNull(earlier.Start);
    }

    [Fact]
    public async Task The_live_reply_names_its_own_turn_so_a_new_prompt_never_shows_the_last_turns_reply_as_its_own()
    {
        await using var s = new Copilot_();
        var first = await s.After(Copilot.User("one", 1), Copilot.Assistant("first", 2));
        Assert.Equal(first.Activity.Turn, first.Activity.ResponseTurn);

        var asked = await s.After(Copilot.User("two", 3));
        Assert.Equal("first", asked.Activity.Response);
        Assert.NotEqual(asked.Activity.Turn, asked.Activity.ResponseTurn);
        Assert.Equal(first.Activity.Turn, asked.Activity.ResponseTurn);

        var replied = await s.After(Copilot.Assistant("second", 4));
        Assert.Equal(replied.Activity.Turn, replied.Activity.ResponseTurn);
    }

    // Found in review of #292: a report spent across a page boundary or not, by page size.
    [Fact]
    public async Task A_turn_reads_the_same_whatever_page_it_is_on()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("start it", 1),
            Copilot.ToolStart("t1", "task", new { description = "Background job", prompt = "go" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2),
            Copilot.SubagentCompleted("t1", "agent-1", 3),
            // Its notice is taken in the next turn.
            Copilot.User("next", 4),
            Copilot.AgentIdle("agent-1", 5),
            Copilot.Assistant("ok", 6));

        var together = (await s.Page(limit: 2)).Turns![1];
        var alone = (await s.Page(limit: 1)).Turns!.Single();
        Assert.Equal(JsonSerializer.Serialize(together, ProtocolJson.Options), JsonSerializer.Serialize(alone, ProtocolJson.Options));
    }

    [Fact]
    public async Task An_answer_cut_to_its_cap_says_so_on_its_part_and_its_turn()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("ask", 1),
            Copilot.ToolStart("q1", "ask_user", new { message = "Say a lot" }, 2),
            Copilot.ToolResult("q1", new string('a', HistoryLimits.Answer + 100), 3));

        var page = await s.Page();
        var turn = page.Turns!.Single();
        var asked = turn.Parts.Single(p => p.Kind == HistoryPartKind.Question);
        Assert.Equal((HistoryLimits.Answer, true, true, true), (asked.Answer!.Length, asked.Truncated, turn.Truncated, page.Partial));
    }

    [Fact]
    public async Task A_delegates_question_stays_in_history_with_whose_it_is_and_how_it_was_answered()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Ask around", prompt = "ask" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2),
            Copilot.ToolStart("q9", "ask_user", new { message = "Which one?" }, 3, agentId: "agent-1"),
            Copilot.ToolDone("q9", 4, agentId: "agent-1"),
            Copilot.Assistant("done", 5));

        var parts = (await s.Page()).Turns!.Single().Parts;
        var asked = parts.Single(p => p.Kind == HistoryPartKind.Question);
        Assert.Equal(("q9", "agent-1", QuestionOutcome.Answered), (asked.Question!.Id, asked.Question.Agent, asked.Outcome!.Value));
        // The delegate's call is its own work, as before: no tool part for it.
        Assert.DoesNotContain(parts, p => p.Tool?.Id == "q9");
    }

    [Fact]
    public async Task A_delegate_started_by_another_delegates_task_call_is_never_the_main_agents()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Lead", prompt = "lead" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2),
            // agent-1 starts agent-2; the start names no parent, only the call's stamp does.
            Copilot.ToolStart("t2", "task", new { description = "Follow", prompt = "follow" }, 3, agentId: "agent-1"),
            Copilot.SubagentStarted("t2", "agent-2", 3),
            Copilot.Event("subagent.completed", new { toolCallId = "t2" }, 4),
            Copilot.SubagentCompleted("t1", "agent-1", 5));

        var delegates = (await s.Page()).Turns!.Single().Parts.Where(p => p.Delegate is not null).Select(p => (p.Delegate!.Agent, p.Delegate.Event)).ToList();
        Assert.Equal([("agent-1", DelegateEventKind.Started), ("agent-1", DelegateEventKind.Reported)], delegates);
    }

    [Fact]
    public async Task A_delegate_reporting_back_is_named_even_when_its_start_is_on_an_older_page_and_the_session_lists_too_many_to_show_it()
    {
        await using var s = new Copilot_();
        var lines = new List<string> { Copilot.User("start them", 1) };
        for (var i = 0; i < Limits.Subagents + 2; i++)
        {
            lines.Add(Copilot.ToolStart("t" + i, "task", new { description = "Job " + i, prompt = "go" }, 2 + i));
            lines.Add(Copilot.SubagentStarted("t" + i, "agent-" + i, 2 + i));
        }
        lines.Add(Copilot.User("wait", 40));
        lines.Add(Copilot.AgentIdle("agent-0", 41));
        var session = await s.After([.. lines]);
        Assert.DoesNotContain(session.Subagents!, a => a.Id == "agent-0");

        var newest = (await s.Page(limit: 1)).Turns!.Single();
        var reported = newest.Parts.Single(p => p.Delegate is not null).Delegate!;
        Assert.Equal(("agent-0", DelegateEventKind.Reported, SubagentOutcome.Done), (reported.Agent, reported.Event, reported.Outcome!.Value));
    }

    [Fact]
    public async Task A_line_still_being_written_longer_than_the_host_reads_back_is_as_far_as_history_goes()
    {
        await using var s = new Copilot_(options: With(new HistoryBudgets { ScanBytes = 100_000 }));
        await s.After(Copilot.User("one", 1));
        File.AppendAllText(s.Events, "{\"type\":\"assistant.message\",\"data\":{\"content\":\"" + new string('x', 300_000));

        var page = await s.Page();
        Assert.Empty(page.Turns!);
        Assert.True(page.Start!.Limited);
        Assert.True(page.Partial);
    }

    [Fact]
    public async Task A_page_its_parsing_budget_cuts_short_goes_on_from_where_it_stopped()
    {
        await using var s = new Copilot_(options: With(new HistoryBudgets { PageBytes = 10_000 }));
        await s.After([.. Enumerable.Range(0, 6).Select(i => Copilot.User($"p{i} " + new string('p', 4000), i + 1))]);

        var seen = new List<string>();
        string? before = null;
        do
        {
            var page = await s.Page(before);
            Assert.True(page.Turns!.Count is > 0 and <= 3);
            seen.InsertRange(0, page.Turns.Select(t => Prompt(t)[..2]));
            before = page.Before;
        }
        while (before is not null);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => "p" + i), seen);
    }

    // Found in review of #292: a synchronous delegate's task call failing ends it, and live drops
    // what it was asking (BackgroundWork.TaskCallDone); history left the approval pending.
    [Fact]
    public async Task A_synchronous_delegates_approval_is_withdrawn_when_its_task_call_fails_as_live()
    {
        await using var s = new Copilot_();
        var session = await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Helper", prompt = "help" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2, mode: "sync"),
            Copilot.ToolStart("c7", "powershell", new { command = "git push" }, 3, agentId: "agent-1"),
            Copilot.Permission("req7", "c7", Copilot.Prompts["commands"], 4, agentId: "agent-1"),
            Copilot.Event("tool.execution_complete", new { toolCallId = "t1", success = false, error = new { message = "failed" } }, 5));

        Assert.Null(session.Question);
        var asked = (await s.Page()).Turns!.Single().Parts.Single(p => p.Kind == HistoryPartKind.Question);
        Assert.Equal(("agent-1", QuestionOutcome.Cancelled), (asked.Question!.Agent, asked.Outcome!.Value));
    }

    [Fact]
    public async Task A_synchronous_delegate_started_on_an_older_page_still_withdraws_its_approval_when_its_call_fails()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Helper", prompt = "help" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2, mode: "sync"),
            Copilot.User("meanwhile", 3),
            Copilot.Permission("req7", "c7", Copilot.Prompts["commands"], 4, agentId: "agent-1"),
            Copilot.Event("tool.execution_complete", new { toolCallId = "t1", success = false, error = new { message = "failed" } }, 5));

        var alone = (await s.Page(limit: 1)).Turns!.Single();
        var together = (await s.Page(limit: 2)).Turns![1];
        Assert.Equal(QuestionOutcome.Cancelled, alone.Parts.Single(p => p.Kind == HistoryPartKind.Question).Outcome);
        Assert.Equal(JsonSerializer.Serialize(together, ProtocolJson.Options), JsonSerializer.Serialize(alone, ProtocolJson.Options));
    }

    // #789: a failed task call ends every synchronous delegate below the one it started, as live
    // (BackgroundWork.EndTurnOf), so a nested one's approval is withdrawn too.
    [Fact]
    public async Task A_failed_task_call_withdraws_the_approvals_of_the_synchronous_delegates_below_it_as_live()
    {
        await using var s = new Copilot_();
        var session = await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Lead", prompt = "lead" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2, mode: "sync"),
            Copilot.ToolStart("t2", "task", new { description = "Helper", prompt = "help" }, 3, agentId: "agent-1"),
            Copilot.SubagentStarted("t2", "agent-2", 3, mode: "sync"),
            Copilot.ToolStart("c7", "powershell", new { command = "git push" }, 4, agentId: "agent-2"),
            Copilot.Permission("req7", "c7", Copilot.Prompts["commands"], 5, agentId: "agent-2"),
            Copilot.Event("tool.execution_complete", new { toolCallId = "t1", success = false, error = new { message = "failed" } }, 6));

        Assert.Null(session.Question);
        var asked = (await s.Page()).Turns!.Single().Parts.Single(p => p.Kind == HistoryPartKind.Question);
        Assert.Equal(("agent-2", QuestionOutcome.Cancelled), (asked.Question!.Agent, asked.Outcome!.Value));
    }

    // Found in review of #292: a record that is JSON but holds half a surrogate pair ("\uD800", as
    // Node writes when it cuts a tool's output mid-emoji) parsed, and then threw when its text was
    // read, which failed every history request reaching it. It is read with the replacement
    // character, live and in history, so nothing it carries is lost.
    static string Undecodable(string line) => line.Replace("UNDECODABLE", "\\uD800", StringComparison.Ordinal);

    const string Replacement = "\uFFFD";

    [Fact]
    public async Task A_claude_prompt_holding_half_a_surrogate_pair_is_read_and_the_turns_before_it_are_reached()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        Fixture.Append(transcript,
            Claude.User("first", 1),
            Claude.Assistant(2, "end_turn", Claude.Text("One.")),
            Undecodable(Claude.User("bad UNDECODABLE", 3)),
            Claude.Assistant(4, "end_turn", Claude.Text("Two.")));
        await using var source = f.Source();
        await source.RefreshAsync();

        var newest = await source.HistoryAsync(Id, null, 1);
        Assert.Null(newest.Partial);
        Assert.Equal("bad " + Replacement, Prompt(newest.Turns!.Single()));
        Assert.Equal(source.Get(Id)!.Activity.Turn, newest.Turns!.Single().Id);
        Assert.Equal("bad " + Replacement, source.Get(Id)!.Activity.UserMessage);
        var earlier = await source.HistoryAsync(Id, newest.Before, 1);
        Assert.Equal(["first"], earlier.Turns!.Select(Prompt));
        Assert.NotNull(earlier.Start);
    }

    [Fact]
    public async Task An_older_copilot_reply_or_name_holding_half_a_surrogate_pair_is_read_whole()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("one", 1),
            Undecodable(Copilot.Assistant("reply UNDECODABLE", 2, reasoning: "half UNDECODABLE")),
            Undecodable(Copilot.Event("assistant.message", new Dictionary<string, object> { ["content"] = "kept", ["UNDECODABLE"] = "name" }, 3)),
            Copilot.User("two", 5),
            Copilot.Assistant("second", 6));

        var newest = await s.Page(limit: 1);
        Assert.Equal(["two"], newest.Turns!.Select(Prompt));
        var earlier = await s.Page(newest.Before, limit: 1);
        var one = earlier.Turns!.Single();
        Assert.Null(earlier.Partial);
        Assert.Null(one.Truncated);
        Assert.Contains(one.Parts, p => p.Text == "half " + Replacement);
        Assert.Contains(one.Parts, p => p.Text?.Contains("reply " + Replacement, StringComparison.Ordinal) == true);
        Assert.NotNull(earlier.Start);
    }

    [Fact]
    public async Task A_first_record_and_first_prompt_holding_half_a_surrogate_pair_still_give_the_start_of_the_session()
    {
        await using var s = new Copilot_();
        await s.After(
            Undecodable(Copilot.Event("session.start", new { selectedModel = "UNDECODABLE" }, 0)),
            Undecodable(Copilot.User("first UNDECODABLE", 1)),
            Copilot.User("two", 2),
            Copilot.User("three", 3));

        var newest = await s.Page(limit: 2);
        Assert.Equal(["two", "three"], newest.Turns!.Select(Prompt));
        Assert.Null(newest.Start);
        var earlier = await s.Page(newest.Before, limit: 2);
        Assert.Equal("first " + Replacement, Prompt(earlier.Turns!.Single()));
        Assert.Equal(Fixture.T0, earlier.Start!.At);
    }

    // Found in review of #292: refusing such a record lost a tool's end and a question's answer
    // carried in it, so the tool ran for good and the question was offered again.
    [Fact]
    public async Task A_claude_tool_result_and_answer_holding_half_a_surrogate_pair_in_text_nobody_reads_still_end_the_tool_and_answer_live_and_in_history()
    {
        static string Extra(string line) => Undecodable(line.Replace("\"is_error\":false", "\"is_error\":false,\"stderr\":\"cut UNDECODABLE\"", StringComparison.Ordinal));
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        Fixture.Append(transcript,
            Claude.User("go", 1),
            Claude.Assistant(2, "tool_use", Claude.ToolUse("t1", "Bash", new JsonObject { ["command"] = "ls" })),
            Extra(Claude.ToolResult("t1", 3)),
            Claude.Assistant(4, "tool_use", Claude.ToolUse("ask1", "AskUserQuestion", Claude.Ask(("Which way?", false, ["Left", "Right"])))),
            Extra(Claude.Answered("ask1", 5, new Dictionary<string, string> { ["Which way?"] = "Left" })));
        Assert.Contains("\\uD800", File.ReadAllText(transcript), StringComparison.Ordinal);
        await using var source = f.Source();
        await source.RefreshAsync();
        var session = source.Get(Id)!;

        Assert.Null(session.Question);
        Assert.Null(session.Activity.CurrentTool);
        var parts = (await source.HistoryAsync(Id, null, 20)).Turns!.Single().Parts;
        Assert.Equal(ToolStatus.Done, parts.First(p => p.Tool?.Id == "t1").Tool!.Status);
        Assert.Equal(QuestionOutcome.Answered, parts.Single(p => p.Kind == HistoryPartKind.Question).Outcome);
    }

    [Fact]
    public async Task A_synchronous_task_calls_failed_end_holding_half_a_surrogate_pair_still_ends_it_and_withdraws_its_approval()
    {
        await using var s = new Copilot_();
        var session = await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Helper", prompt = "help" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2, mode: "sync"),
            Copilot.ToolStart("c7", "powershell", new { command = "git push" }, 3, agentId: "agent-1"),
            Copilot.Permission("req7", "c7", Copilot.Prompts["commands"], 4, agentId: "agent-1"),
            Undecodable(Copilot.Event("tool.execution_complete", new { toolCallId = "t1", success = false, result = new { content = "cut UNDECODABLE" }, error = new { message = "failed" } }, 5)));

        Assert.Null(session.Question);
        var asked = (await s.Page()).Turns!.Single().Parts.Single(p => p.Kind == HistoryPartKind.Question);
        Assert.Equal(QuestionOutcome.Cancelled, asked.Outcome);
    }

    // #789, with the delegate's nested start on an older page: the session's own record of who
    // started whom (BackgroundWork.Parents) still ends it with the call that failed.
    [Fact]
    public async Task A_nested_delegate_started_on_an_older_page_still_has_its_approval_withdrawn_when_the_call_above_it_fails()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Lead", prompt = "lead" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2, mode: "sync"),
            Copilot.ToolStart("t2", "task", new { description = "Helper", prompt = "help" }, 3, agentId: "agent-1"),
            Copilot.SubagentStarted("t2", "agent-2", 3, mode: "sync"),
            Copilot.User("meanwhile", 4),
            Copilot.ToolStart("c7", "powershell", new { command = "git push" }, 5, agentId: "agent-2"),
            Copilot.Permission("req7", "c7", Copilot.Prompts["commands"], 6, agentId: "agent-2"),
            Copilot.Event("tool.execution_complete", new { toolCallId = "t1", success = false, error = new { message = "failed" } }, 7));

        var alone = (await s.Page(limit: 1)).Turns!.Single();
        var together = (await s.Page(limit: 2)).Turns![1];
        var asked = alone.Parts.Single(p => p.Kind == HistoryPartKind.Question);
        Assert.Equal(("agent-2", QuestionOutcome.Cancelled), (asked.Question!.Agent, asked.Outcome!.Value));
        Assert.Equal(JsonSerializer.Serialize(together, ProtocolJson.Options), JsonSerializer.Serialize(alone, ProtocolJson.Options));
    }
    // Found in review of #292: a delegate's start was named from its task call when that call
    // was on the page, and from the start's own description when not.
    [Fact]
    public async Task A_delegate_start_has_the_same_name_whatever_page_its_task_call_is_on()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Lead job", prompt = "lead" }, 2),
            Copilot.User("next", 3),
            Copilot.SubagentStarted("t1", "agent-1", 4));

        var alone = (await s.Page(limit: 1)).Turns!.Single();
        var together = (await s.Page(limit: 2)).Turns![1];
        Assert.Equal("Lead job", alone.Parts.Single(p => p.Delegate is not null).Delegate!.Name);
        Assert.Equal(JsonSerializer.Serialize(together, ProtocolJson.Options), JsonSerializer.Serialize(alone, ProtocolJson.Options));
    }

    // Found in review of #292: who made a delegate's call was known only from the page's own
    // records, so its permission prompt in the next turn lost whose it was when read alone.
    [Fact]
    public async Task A_delegates_permission_prompt_names_the_delegate_when_its_call_is_on_an_older_page()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Helper", prompt = "help" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2),
            Copilot.ToolStart("c7", "powershell", new { command = "git push" }, 3, agentId: "agent-1"),
            Copilot.User("meanwhile", 4),
            Copilot.Permission("req7", "c7", Copilot.Prompts["commands"], 5));

        var alone = (await s.Page(limit: 1)).Turns!.Single();
        var together = (await s.Page(limit: 2)).Turns![1];
        Assert.Equal("agent-1", alone.Parts.Single(p => p.Kind == HistoryPartKind.Question).Question!.Agent);
        Assert.Equal(JsonSerializer.Serialize(together, ProtocolJson.Options), JsonSerializer.Serialize(alone, ProtocolJson.Options));
    }

    // Found in review of #292 (Codex): a delegate's task call on an older page, and the start it
    // made on this one, which names no parent: read alone, the start was taken for the main agent's.
    [Fact]
    public async Task A_delegate_started_by_another_delegates_call_on_an_older_page_is_never_the_main_agents()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("go", 1),
            Copilot.ToolStart("t1", "task", new { description = "Lead", prompt = "lead" }, 2),
            Copilot.SubagentStarted("t1", "agent-1", 2),
            Copilot.ToolStart("t2", "task", new { description = "Follow", prompt = "follow" }, 3, agentId: "agent-1"),
            Copilot.User("next", 4),
            Copilot.SubagentStarted("t2", "agent-2", 5),
            Copilot.Event("subagent.completed", new { toolCallId = "t2" }, 6));

        var alone = (await s.Page(limit: 1)).Turns!.Single();
        Assert.DoesNotContain(alone.Parts, p => p.Delegate is not null);
        var together = (await s.Page(limit: 2)).Turns![1];
        Assert.Equal(JsonSerializer.Serialize(together, ProtocolJson.Options), JsonSerializer.Serialize(alone, ProtocolJson.Options));
    }

    // Found in review of #292: lines carrying a turn's mark that start no turn were parsed free
    // until the first prompt was found.
    [Fact]
    public async Task Lines_parsed_looking_for_a_prompt_are_charged_to_the_page_even_before_one_is_found()
    {
        await using var s = new Copilot_(options: With(new HistoryBudgets { PageBytes = 10_000 }));
        await s.After([Copilot.User("one", 1), .. Enumerable.Range(0, 6).Select(i => Copilot.User($"d{i} " + new string('d', 4000), 2 + i, agentId: "agent-1"))]);

        var page = await s.Page();
        Assert.Empty(page.Turns!);
        Assert.True(page.Start!.Limited);
        Assert.True(page.Partial);
    }

    [Fact]
    public async Task A_question_cut_to_its_cap_makes_its_turn_truncated_and_its_page_partial()
    {
        await using var s = new Copilot_();
        await s.After(
            Copilot.User("ask", 1),
            Copilot.ToolStart("q1", "ask_user", new { message = new string('m', Limits.QuestionMessage + 100) }, 2));

        var page = await s.Page();
        var turn = page.Turns!.Single();
        Assert.True(turn.Parts.Single(p => p.Kind == HistoryPartKind.Question).Question!.Truncated);
        Assert.Equal((true, true), (turn.Truncated, page.Partial));
    }

    // Found in review of #292: two prompts too long to read, the same in their first 4 KiB and
    // their length, were one turn id, so a client merging by id drew one of them.
    [Fact]
    public async Task Two_prompts_too_long_to_read_that_start_alike_are_two_turns()
    {
        await using var s = new Copilot_(options: With(maxLineBytes: 2048));
        static string Long(int n) => new JsonObject
        {
            ["type"] = "user.message",
            ["data"] = new JsonObject { ["content"] = new string('h', 5000), ["attachments"] = new JsonArray() },
            ["id"] = $"long-{n}",
            ["timestamp"] = Copilot.At(n),
        }.ToJsonString();
        await s.After(Copilot.User("one", 1), Long(2), Long(3));

        var turns = (await s.Page()).Turns!;
        Assert.Equal(3, turns.Count);
        Assert.True(turns[1].Unreadable == true && turns[2].Unreadable == true);
        Assert.NotEqual(turns[1].Id, turns[2].Id);
        Assert.Equal(turns.Select(t => t.Id), (await s.Page()).Turns!.Select(t => t.Id));
    }

    // ---- Claude and Codex ----

    // Found in review of #292: each block of one message copied the whole response so far, so a
    // message of many small blocks cost the square of its length to read.
    [Fact]
    public async Task A_claude_message_of_many_blocks_is_gathered_in_bounded_work_and_keeps_its_end()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        const int blocks = 3000;
        Fixture.Append(transcript, [Claude.User("write a lot", 1), .. Enumerable.Range(0, blocks).Select(i => Claude.AssistantMessage("msg_1", 2, null, Claude.Text($"block {i:D4} " + new string('b', 200))))]);
        await using var source = f.Source();
        await source.RefreshAsync();

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var page = await source.HistoryAsync(Id, null, 20);
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        var response = page.Turns!.Single().Parts.Single(p => p.Kind == HistoryPartKind.Response);
        Assert.EndsWith($"block {blocks - 1:D4} " + new string('b', 200), response.Text);
        Assert.True(response.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(response.Text!) <= HistoryLimits.Response);
        // Copying the whole response for every block allocates over a gigabyte here.
        Assert.True(allocated < 256L * 1024 * 1024, $"reading the message allocated {allocated:N0} bytes");
    }

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

    // Claude queues a delegate's notice the moment it stops, and delivers it later.
    static string Queued(string text, int s) =>
        new JsonObject { ["type"] = "queue-operation", ["operation"] = "enqueue", ["timestamp"] = Copilot.At(s), ["sessionId"] = Id, ["content"] = text }.ToJsonString();

    static string TaskNotice(string status, string task = "a1b2") => $"<task-notification>\n<task-id>{task}</task-id>\n<status>{status}</status>\n</task-notification>";

    // Found in review of #292: the first run stopped, a message woke the delegate before that run's
    // notice was delivered, and the second run's notice was then dropped, so only the first run's
    // "done" was left while the second had failed.
    static string[] WokenBeforeItsNoticeWasDelivered(bool ownTurn) =>
    [
        Claude.User("explore", 1),
        Claude.Assistant(2, "tool_use", Claude.ToolUse("agent1", "Agent", new JsonObject { ["description"] = "Explore the code", ["prompt"] = "look" })),
        LaunchedAgent("agent1", "a1b2", "Explore the code", 3),
        Queued(TaskNotice("completed"), 4),
        .. ownTurn ? new[] { Claude.Assistant(5, "end_turn", Claude.Text("Started it.")), Claude.User("look again", 6) } : [],
        Claude.Assistant(7, "tool_use", Claude.ToolUse("wake1", "SendMessage", new JsonObject { ["to"] = "a1b2", ["summary"] = "Again", ["message"] = "Look again." })),
        Claude.ToolResult("wake1", 8),
        Notice(TaskNotice("completed"), 9),
        Queued(TaskNotice("failed"), 10),
        Notice(TaskNotice("failed"), 11),
        Claude.Assistant(12, "end_turn", Claude.Text("The second look failed.")),
    ];

    static (DelegateEventKind, SubagentOutcome?, DateTimeOffset)[] Delegates(HistoryTurn turn) =>
        [.. turn.Parts.Where(p => p.Delegate is not null).Select(p => (p.Delegate!.Event, p.Delegate.Outcome, p.At))];

    [Fact]
    public async Task A_claude_delegate_woken_before_its_notice_was_delivered_reports_both_runs_outcomes()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        Fixture.Append(transcript, WokenBeforeItsNoticeWasDelivered(ownTurn: false));
        await using var source = f.Source();
        await source.RefreshAsync();

        var page = await source.HistoryAsync(Id, null, 20);
        Assert.Equal(
            [(DelegateEventKind.Started, null, Fixture.T0.AddSeconds(3)), (DelegateEventKind.Reported, SubagentOutcome.Done, Fixture.T0.AddSeconds(9)),
             (DelegateEventKind.Reported, SubagentOutcome.Failed, Fixture.T0.AddSeconds(11))],
            Delegates(page.Turns!.Single()));
    }

    [Fact]
    public async Task A_claude_delegate_woken_in_a_later_turn_reports_both_runs_there_on_whichever_page_it_is_read()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        Fixture.Append(transcript, WokenBeforeItsNoticeWasDelivered(ownTurn: true));
        await using var source = f.Source();
        await source.RefreshAsync();

        var whole = await source.HistoryAsync(Id, null, 20);
        var alone = await source.HistoryAsync(Id, null, 1);
        Assert.Equal(["explore", "look again"], whole.Turns!.Select(Prompt));
        Assert.Equal("look again", Prompt(alone.Turns!.Single()));
        (DelegateEventKind, SubagentOutcome?, DateTimeOffset)[] reports =
            [(DelegateEventKind.Reported, SubagentOutcome.Done, Fixture.T0.AddSeconds(9)), (DelegateEventKind.Reported, SubagentOutcome.Failed, Fixture.T0.AddSeconds(11))];
        Assert.Equal(reports, Delegates(whole.Turns![1]));
        Assert.Equal(reports, Delegates(alone.Turns!.Single()));
        Assert.Equal([(DelegateEventKind.Started, (SubagentOutcome?)null, Fixture.T0.AddSeconds(3))], Delegates(whole.Turns[0]));
    }

    static async Task<HistoryResult> ReadClaude(params string[] lines)
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        Fixture.Append(transcript, lines);
        await using var source = f.Source();
        await source.RefreshAsync();
        return await source.HistoryAsync(Id, null, 20);
    }

    // Found in review of #292: Claude fires a run's notice again, hours later
    // (anthropics/claude-code#95742). Live takes it once, and so does a turn.
    [Fact]
    public async Task A_claude_notice_fired_again_for_the_same_run_is_reported_once()
    {
        var page = await ReadClaude(
            Claude.User("explore", 1),
            Claude.Assistant(2, "tool_use", Claude.ToolUse("agent1", "Agent", new JsonObject { ["description"] = "Explore the code", ["prompt"] = "look" })),
            LaunchedAgent("agent1", "a1b2", "Explore the code", 3),
            Notice(TaskNotice("completed"), 4),
            Notice(TaskNotice("completed"), 5),
            Notice(TaskNotice("completed"), 6));

        Assert.Equal(
            [(DelegateEventKind.Started, null, Fixture.T0.AddSeconds(3)), (DelegateEventKind.Reported, SubagentOutcome.Done, Fixture.T0.AddSeconds(4))],
            Delegates(page.Turns!.Single()));
    }

    [Fact]
    public async Task A_stale_claude_notice_after_the_resumed_runs_failure_leaves_failed_as_the_last_word()
    {
        var page = await ReadClaude([.. WokenBeforeItsNoticeWasDelivered(ownTurn: false), Notice(TaskNotice("completed"), 13)]);

        var reports = Delegates(page.Turns!.Single()).Where(d => d.Item1 == DelegateEventKind.Reported).ToList();
        Assert.Equal([(DelegateEventKind.Reported, SubagentOutcome.Done, Fixture.T0.AddSeconds(9)), (DelegateEventKind.Reported, SubagentOutcome.Failed, Fixture.T0.AddSeconds(11))], reports);
    }

    [Fact]
    public async Task Claude_notices_fired_again_never_push_another_delegates_parts_out_of_the_turn()
    {
        var page = await ReadClaude(
        [
            Claude.User("explore", 1),
            Claude.Assistant(2, "tool_use", Claude.ToolUse("agent0", "Agent", new JsonObject { ["description"] = "Check the docs", ["prompt"] = "look" })),
            LaunchedAgent("agent0", "c3d4", "Check the docs", 3),
            Notice(TaskNotice("completed", "c3d4"), 4),
            Claude.Assistant(5, "tool_use", Claude.ToolUse("agent1", "Agent", new JsonObject { ["description"] = "Explore the code", ["prompt"] = "look" })),
            LaunchedAgent("agent1", "a1b2", "Explore the code", 6),
            .. Enumerable.Range(0, HistoryLimits.DelegatesPerTurn + 10).Select(i => Notice(TaskNotice("completed"), 7 + i)),
        ]);

        var delegates = page.Turns!.Single().Parts.Where(p => p.Delegate is not null).Select(p => (p.Delegate!.Event, p.Delegate.Name)).ToList();
        Assert.Equal(
            [(DelegateEventKind.Started, "Check the docs"), (DelegateEventKind.Reported, "Check the docs"),
             (DelegateEventKind.Started, "Explore the code"), (DelegateEventKind.Reported, "Explore the code")],
            delegates);
        Assert.Null(page.Turns.Single().Truncated);
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

    static async Task<(Session Session, HistoryResult Page)> ReadCodex(params string[] lines)
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout, updated: Fixture.T0);
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout, [Codex.Line("session_meta", new { id = Id, timestamp = Copilot.At(0), cwd = "/work/codex" }, 0), .. lines]);
        await using var source = f.Source();
        await source.RefreshAsync();
        return (source.Get(Id)!, await source.HistoryAsync(Id, null, 20));
    }

    [Fact]
    public async Task A_codex_reply_and_reasoning_written_in_both_streams_are_each_said_once_and_one_written_only_as_items_is_kept()
    {
        var (session, page) = await ReadCodex(
            Codex.Event(new { type = "user_message", message = "both streams" }, 1),
            Codex.Item(new { type = "reasoning", summary = new[] { new { type = "summary_text", text = "First thought." }, new { type = "summary_text", text = "Second thought." } } }, 2),
            Codex.Event(new { type = "agent_reasoning", text = "First thought." }, 2),
            Codex.Event(new { type = "agent_reasoning", text = "Second thought." }, 2),
            Codex.Event(new { type = "agent_message", message = "Said twice." }, 3),
            Codex.Item(new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Said twice." } } }, 3),
            Codex.Event(new { type = "task_complete", last_agent_message = "Said twice." }, 3),
            Codex.Event(new { type = "user_message", message = "items only" }, 4),
            Codex.Item(new { type = "reasoning", summary = new[] { new { type = "summary_text", text = "Only an item." } } }, 5),
            Codex.Item(new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Item reply." } } }, 5),
            Codex.Event(new { type = "task_complete", last_agent_message = "The final word." }, 6));

        var texts = page.Turns!.Select(t => t.Parts.Select(p => (p.Kind, p.Text)).ToList()).ToList();
        Assert.Equal([(HistoryPartKind.Reasoning, "First thought."), (HistoryPartKind.Reasoning, "Second thought."), (HistoryPartKind.Response, "Said twice.")], texts[0]);
        Assert.Equal([(HistoryPartKind.Reasoning, "Only an item."), (HistoryPartKind.Response, "Item reply."), (HistoryPartKind.Response, "The final word.")], texts[1]);
        Assert.Equal(("The final word.", session.Activity.Turn), (session.Activity.Response, session.Activity.ResponseTurn));
    }

    const string Thread = "019a0000-0000-7000-8000-00000000beef";

    static string Collab(string tool, int s, JsonNode state) => Codex.Event(new
    {
        type = "item_completed",
        item = new
        {
            type = "CollabAgentToolCall",
            id = "call-" + tool + s,
            tool,
            status = "completed",
            sender_thread_id = Id,
            receiver_thread_ids = new[] { Thread },
            receiver_agents = new[] { new { thread_id = Thread, agent_nickname = "Fermat" } },
            agents_states = new Dictionary<string, JsonNode> { [Thread] = state },
        },
    }, s);

    static JsonNode Completed => new JsonObject { ["completed"] = "ok" };

    static string SubagentNotice(int s) => Codex.Item(new
    {
        type = "message",
        role = "user",
        content = new[] { new { type = "input_text", text = $"<subagent_notification>\n{{\"agent_path\":\"{Thread}\",\"status\":{{\"completed\":\"ok\"}}}}\n</subagent_notification>" } },
        internal_chat_message_metadata_passthrough = new { content_item_kinds = new[] { "multi_agent.subagent_notification" } },
    }, s);

    // Found in review of #292: a delegate woken by input reported only its first run.
    [Fact]
    public async Task A_codex_delegate_woken_again_reports_each_run_once_and_a_late_notice_is_spent_on_the_run_it_was_for()
    {
        var (_, page) = await ReadCodex(
            Codex.Event(new { type = "user_message", message = "delegate" }, 1),
            Collab("spawn_agent", 2, JsonValue.Create("pending_init")),
            SubagentNotice(3),
            Collab("send_input", 4, JsonValue.Create("running")),
            // The wait hears the second run end, and its notice comes after.
            Collab("wait", 5, Completed),
            SubagentNotice(6),
            Collab("send_input", 7, JsonValue.Create("running")),
            SubagentNotice(8));

        var events = page.Turns!.Single().Parts.Where(p => p.Delegate is not null).Select(p => (p.Delegate!.Event, p.At)).ToList();
        Assert.Equal(
            [(DelegateEventKind.Started, Fixture.T0.AddSeconds(2)), (DelegateEventKind.Reported, Fixture.T0.AddSeconds(3)),
             (DelegateEventKind.Reported, Fixture.T0.AddSeconds(5)), (DelegateEventKind.Reported, Fixture.T0.AddSeconds(8))],
            events);
    }

    // Found in review of #292: each wait saying the same end again owed another notice, which then
    // swallowed the next run's.
    [Fact]
    public async Task A_codex_run_end_said_by_several_waits_owes_one_notice_and_the_next_run_still_reports()
    {
        var (_, page) = await ReadCodex(
            Codex.Event(new { type = "user_message", message = "delegate" }, 1),
            Collab("spawn_agent", 2, JsonValue.Create("pending_init")),
            Collab("wait", 3, Completed),
            Collab("wait", 4, Completed),
            SubagentNotice(5),
            Collab("send_input", 6, JsonValue.Create("running")),
            SubagentNotice(7));

        var reports = page.Turns!.Single().Parts.Where(p => p.Delegate is { Event: DelegateEventKind.Reported }).Select(p => p.At);
        Assert.Equal([Fixture.T0.AddSeconds(3), Fixture.T0.AddSeconds(7)], reports);
    }

    // Found in review of #292: a prompt that is just the words "tool_result" holds that bare token.
    [Fact]
    public async Task A_claude_prompt_that_says_tool_result_is_still_a_prompt()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        Fixture.Append(transcript,
            Claude.User("first", 1),
            Claude.Assistant(2, "end_turn", Claude.Text("One.")),
            Claude.User("tool_result", 3),
            Claude.Assistant(4, "end_turn", Claude.Text("Two.")));
        await using var source = f.Source();
        await source.RefreshAsync();

        var turns = (await source.HistoryAsync(Id, null, 20)).Turns!;
        Assert.Equal(["first", "tool_result"], turns.Select(Prompt));
        Assert.Equal("Two.", turns[1].Parts.Single().Text);
        Assert.Equal(source.Get(Id)!.Activity.Turn, turns[1].Id);
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
        var turns = page.Turns!;
        Assert.NotEmpty(turns);
        Assert.NotNull(page.Start);
        Assert.Null(page.Partial);
        Assert.Equal(session.Activity.Turn, turns.Last().Id);
        Assert.Equal(turns.Count, turns.Select(t => t.Id).Distinct().Count());
        Assert.All(turns, t => Assert.Equal(t.Parts.Count, t.Parts.Select(p => p.Id).Distinct().Count()));
        if (agent != "claude") { Assert.Contains(turns.SelectMany(t => t.Parts), p => p.Delegate is { Event: DelegateEventKind.Started }); }
    }
}
