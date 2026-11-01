using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

// A session and its sub-agents (docs/design/subagents.md, #173). The recorded fixture,
// tests/fixtures/copilot/subagents.jsonl, follows the event shapes and field sets of Copilot
// CLI 1.0.105 transcripts; its text, ids and paths are invented. It holds two background
// agents, a task agent nested under one of them, a sub-agent's own shells, a woken agent, a
// sub-agent's permission prompt, and a sub-agent turn that asks for tools and then replies.
public class SubagentTests
{
    const string Id = "66666666-6666-4666-8666-666666666666";
    const string Suite = "3a91c0de-0a4e-4c47-9d2b-5f1e8b7a2c11";
    const string Witness = "c204b7f2-6d3e-4f81-a0c9-2e4d6b8f1a37";
    const string Review = "7e55d1a0-92b4-4c6e-8f13-b0a9c4d2e5f6";

    static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "copilot", name);

    // Replays the recorded transcript up to a second after Fixture.T0.
    sealed class Recorded : IAsyncDisposable
    {
        readonly Fixture _f = new();
        readonly string _events;
        readonly string[] _lines = File.ReadAllLines(FixturePath("subagents.jsonl"));
        int _next;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Recorded()
        {
            _events = _f.CopilotSession(Id, 100);
            _f.Processes.Run(AgentKind.Copilot, 100);
            Source = _f.Source();
        }

        public async Task<Session> Through(int second)
        {
            var batch = new List<string>();
            while (_next < _lines.Length && At(_lines[_next]) < Fixture.T0.AddSeconds(second)) { batch.Add(_lines[_next++]); }
            Fixture.Append(_events, [.. batch]);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        static DateTimeOffset At(string line) => DateTimeOffset.Parse(JsonNode.Parse(line)!["timestamp"]!.GetValue<string>(), CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            _f.Dispose();
        }
    }

    static Subagent Entry(Session s, string id) => Assert.Single(s.Subagents!, a => a.Id == id);

    // What a client that predates sub-agents reads: everything but the fields #173 added.
    static JsonNode Legacy(Session s)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(s, ProtocolJson.Options))!.AsObject();
        node.Remove("subagents");
        node.Remove("subagentsMore");
        node.Remove("waitingOn");
        node["activity"]!.AsObject().Remove("source");
        node["activity"]!.AsObject().Remove("own");
        node["question"]?.AsObject().Remove("agent");
        return node;
    }

    [Fact]
    public async Task Older_clients_see_exactly_todays_top_level_fields_through_a_recorded_session_of_delegates()
    {
        // subagents.v1.json is what the host before #173 sent at each point, recorded from it.
        var golden = JsonNode.Parse(File.ReadAllText(FixturePath("subagents.v1.json")))!.AsObject();
        await using var r = new Recorded();

        foreach (var second in new[] { 20, 26, 45, 60, 84, 90, 101 })
        {
            var now = Legacy(await r.Through(second));
            var then = golden[second.ToString(CultureInfo.InvariantCulture)]!.DeepClone().AsObject();
            if (second == 84)
            {
                // The one intended difference (#84): witness-logic's last turn asked for a tool,
                // then replied with nothing more to run, so its run ended at that turn's end.
                // The host before kept counting it until its agent_idle arrived at 85 s.
                Assert.Equal(1, then["activity"]!["background"]!["agents"]!.GetValue<int>());
                Assert.Null(now["activity"]!["background"]);
                then["activity"]!.AsObject().Remove("background");
            }
            if (second == 90)
            {
                // And so that late agent_idle no longer changes anything: the session was last
                // updated when run-suite finished, at 82 s, not when the notice came.
                Assert.StartsWith("2026-10-28T04:01:25.", then["updatedAt"]!.GetValue<string>());
                Assert.StartsWith("2026-10-28T04:01:22.", now["updatedAt"]!.GetValue<string>());
                then.Remove("updatedAt");
                now.AsObject().Remove("updatedAt");
            }
            if (second == 101)
            {
                // The CLI shutting down ends every entry, which a client listing them sees, so
                // it moves updatedAt (subagents.md §5); nothing older clients read changed.
                Assert.StartsWith("2026-10-28T04:01:25.", then["updatedAt"]!.GetValue<string>());
                Assert.StartsWith("2026-10-28T04:01:40.", now["updatedAt"]!.GetValue<string>());
                then.Remove("updatedAt");
                now.AsObject().Remove("updatedAt");
            }
            Assert.True(JsonNode.DeepEquals(then, now), $"at {second} s:\n{now.ToJsonString()}\nwas\n{then.ToJsonString()}");
        }
    }

    [Fact]
    public async Task Several_background_agents_are_listed_with_the_name_and_prompt_they_were_given_and_their_own_model()
    {
        await using var r = new Recorded();

        var s = await r.Through(20);

        Assert.Equal(SessionStatus.Idle, s.Status);
        Assert.Equal(new Background { Agents = 2 }, s.Activity.Background);
        Assert.Equal([Witness, Suite], s.Subagents!.Select(a => a.Id));
        var suite = Entry(s, Suite);
        Assert.Equal((SubagentKind.Background, SubagentStatus.Working, (string?)null), (suite.Kind, suite.Status, suite.Parent));
        Assert.Equal("Run full test suite", suite.Name);
        Assert.Equal("Run the full test suite on HEAD and report any failure with its first error line.", suite.Task);
        Assert.Equal("claude-haiku-5.5", suite.Model);
        Assert.Equal("read_powershell", suite.CurrentTool!.Name);
        Assert.Equal("Wait for the suite.", suite.Line);
        Assert.Null(suite.Outcome);
        Assert.Equal(("gpt-5.6-luna", "Read witness shell logic"), (Entry(s, Witness).Model, Entry(s, Witness).Name));
        // The model shown for the session is the main agent's, never a delegate's (#73).
        Assert.Equal("claude-opus-5.5", s.Model);
        Assert.Null(s.SubagentsMore);
    }

    [Fact]
    public async Task A_sub_agents_own_shells_are_counted_on_its_entry_and_never_on_the_session()
    {
        await using var r = new Recorded();

        var running = await r.Through(20);
        Assert.Equal(1, Entry(running, Suite).Shells);
        Assert.Equal(0, running.Activity.Background!.Shells);

        var done = await r.Through(45);
        Assert.Equal(0, Entry(done, Suite).Shells);
    }

    [Fact]
    public async Task A_task_agent_started_by_a_sub_agent_is_listed_under_it_and_is_not_counted_as_background()
    {
        await using var r = new Recorded();

        var nested = await r.Through(26);
        var review = Entry(nested, Review);
        Assert.Equal((SubagentKind.Task, SubagentStatus.Working, Witness), (review.Kind, review.Status, review.Parent));
        Assert.Equal("Review the witness change", review.Name);
        Assert.Equal("Review the AskedForTools change in ShadowWitness.cs for regressions.", review.Task);
        Assert.Equal("task", Entry(nested, Witness).CurrentTool!.Name);
        Assert.Equal(new Background { Agents = 2 }, nested.Activity.Background);

        var finished = Entry(await r.Through(45), Review);
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Done), (finished.Status, finished.Outcome));
        Assert.InRange(finished.EndedAt!.Value, Fixture.T0.AddSeconds(29), Fixture.T0.AddSeconds(30));
        Assert.Null(finished.CurrentTool);
    }

    [Fact]
    public async Task A_background_agent_whose_run_ends_is_idle_until_a_message_wakes_the_same_entry_again()
    {
        await using var r = new Recorded();

        var idle = Entry(await r.Through(45), Suite);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (idle.Status, idle.Outcome));
        Assert.Equal("HEAD: 1 failing test, RestartAfterResume (timing).", idle.Line);

        var woken = await r.Through(60);
        var again = Entry(woken, Suite);
        Assert.Equal(SubagentStatus.Working, again.Status);
        Assert.Null(again.Outcome);
        Assert.Null(again.EndedAt);
        Assert.Equal(idle.StartedAt, again.StartedAt);
        Assert.Single(woken.Subagents!, a => a.Id == Suite);
    }

    [Fact]
    public async Task A_sub_agents_permission_prompt_makes_the_session_wait_on_that_agent()
    {
        await using var r = new Recorded();

        var s = await r.Through(60);

        Assert.Equal(SessionStatus.Waiting, s.Status);
        Assert.Equal(Witness, s.Question!.Agent);
        Assert.Equal(Witness, s.WaitingOn);
        Assert.Equal(QuestionKind.Approval, s.Question.Kind);
        // It is the CLI's one permission dialog, answered as the main agent's would be.
        Assert.Null(s.Question.AnswerAtMachine);
        Assert.Equal(SubagentStatus.Waiting, Entry(s, Witness).Status);
        Assert.Equal(new Background { Agents = 2 }, s.Activity.Background);

        var answered = await r.Through(71);
        Assert.Null(answered.WaitingOn);
        Assert.Equal(SubagentStatus.Working, Entry(answered, Witness).Status);
    }

    [Fact]
    public async Task A_sub_agent_turn_that_asks_for_tools_and_then_replies_with_nothing_to_run_ends_its_run()
    {
        await using var r = new Recorded();

        var s = await r.Through(84);

        var witness = Entry(s, Witness);
        Assert.Equal((SubagentStatus.Idle, SubagentOutcome.Done), (witness.Status, witness.Outcome));
        Assert.Null(s.Activity.Background);
    }

    [Fact]
    public async Task The_main_agents_own_text_stays_beside_a_delegates_and_source_says_who_wrote_each_field()
    {
        await using var r = new Recorded();

        var reasoning = await r.Through(20);
        Assert.Equal("Started 2 background agents: run-suite and witness-logic.", reasoning.Activity.Response);
        Assert.Equal("Wait for the suite.", reasoning.Activity.Reasoning);
        Assert.Equal(new ActivitySource { Reasoning = Suite }, reasoning.Activity.Source);
        Assert.Equal("Two independent jobs: delegate both to background agents.", reasoning.Activity.Own!.Reasoning);
        Assert.Null(reasoning.Activity.Own.Response);

        var both = await r.Through(45);
        Assert.Equal("HEAD: 1 failing test, RestartAfterResume (timing).", both.Activity.Response);
        Assert.Equal(new ActivitySource { Response = Suite, Reasoning = Review }, both.Activity.Source);
        Assert.Equal("Started 2 background agents: run-suite and witness-logic.", both.Activity.Own!.Response);

        // The main agent writing again takes its field back.
        var own = await r.Through(58);
        Assert.Equal("Asked run-suite to bisect; witness-logic is still reading.", own.Activity.Response);
        Assert.Null(own.Activity.Source!.Response);
        Assert.Equal(Suite, own.Activity.Source.Reasoning);
        Assert.Null(own.Activity.Own!.Response);
        Assert.Equal("Have it bisect.", own.Activity.Own.Reasoning);
    }

    [Fact]
    public async Task Finished_delegates_stay_listed_until_the_session_ends_and_then_read_as_ended()
    {
        await using var r = new Recorded();

        var finished = await r.Through(90);
        Assert.Equal(3, finished.Subagents!.Count);
        Assert.All(finished.Subagents, a => Assert.True(a.Finished));
        Assert.Equal([SubagentStatus.Idle, SubagentStatus.Idle, SubagentStatus.Ended], finished.Subagents.Select(a => a.Status));
        // Most recently finished first.
        Assert.Equal([Suite, Witness, Review], finished.Subagents.Select(a => a.Id));

        var ended = await r.Through(101);
        Assert.All(ended.Subagents!, a => Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Done), (a.Status, a.Outcome)));
    }

    [Fact]
    public async Task Asking_for_one_sub_agent_returns_its_own_response_reasoning_and_tools_and_nothing_for_an_unknown_one()
    {
        await using var r = new Recorded();
        await r.Through(90);

        var suite = (await r.Source.GetAgentAsync(Id, Suite))!;

        Assert.Equal("First bad commit: abc1234 (Retry the resume twice).", suite.Response);
        Assert.Equal("Bisect over the last 10 commits.", suite.Reasoning);
        Assert.Equal(["powershell", "read_powershell", "powershell"], suite.History!.Select(t => t.Name));
        Assert.Null(await r.Source.GetAgentAsync(Id, "agent-unknown"));
        Assert.Null(await r.Source.GetAgentAsync("no-such-session", Suite));
        // Lists never carry it.
        Assert.All((await r.Through(91)).Subagents!, a => Assert.Null(a.History ?? (object?)a.Response ?? a.Reasoning));
    }

    // Synthetic shapes for what no recorded transcript has shown yet.
    sealed class Live : IAsyncDisposable
    {
        public readonly Fixture F = new();
        readonly string _events;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Live(long tailBytes = 4 * 1024 * 1024)
        {
            _events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source(tailBytes);
        }

        public async Task<Session> After(params string[] lines)
        {
            Fixture.Append(_events, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            F.Dispose();
        }
    }

    static string[] Delegate(string callId, string agentId, int s) =>
    [
        Copilot.User("delegate it", s), Copilot.TurnStart(s), Copilot.Assistant("", s, tools: [callId]),
        Copilot.ToolStart(callId, "task", new { name = "helper", description = "Help out " + agentId, mode = "background", prompt = "Do the thing" }, s),
        Copilot.SubagentStarted(callId, agentId, s), Copilot.ToolDone(callId, s), Copilot.User("Do the thing", s, agentId), Copilot.TurnStart(s, agentId),
        Copilot.Assistant("Started it.", s + 1), Copilot.TurnEnd(s + 1),
    ];

    static readonly object Ask = new
    {
        message = "Which branch?",
        requestedSchema = new { properties = new { pick = new { type = "string", title = "Pick", @enum = new[] { "main", "dev" } } } },
    };

    [Fact]
    public async Task A_sub_agents_ask_user_makes_the_session_wait_on_it_and_is_answered_at_the_machine()
    {
        await using var s = new Live();
        await s.After(Delegate("t1", "a1", 1));

        var alone = await s.After(Copilot.ToolStart("q1", "ask_user", Ask, 10, "a1"));
        Assert.Equal(SessionStatus.Waiting, alone.Status);
        Assert.Equal(("a1", "a1"), (alone.Question!.Agent, alone.WaitingOn));
        // Delivery types only into the main agent's ask_user, so nothing proves where keys
        // for a sub-agent's would land, even with no other prompt pending.
        Assert.True(alone.Question.AnswerAtMachine);

        var two = await s.After(Copilot.ToolStart("q2", "ask_user", Ask, 11, "a2"));
        Assert.Equal("q2", two.Question!.Id);
        Assert.True(two.Question.AnswerAtMachine);

        var answered = await s.After(Copilot.Answered("q1", 12), Copilot.Answered("q2", 12));
        Assert.Null(answered.WaitingOn);
        Assert.Equal(SessionStatus.Idle, answered.Status);
    }

    [Fact]
    public async Task The_sessions_own_question_wins_over_a_sub_agents_whichever_came_last()
    {
        await using var s = new Live();
        await s.After(Delegate("t1", "a1", 1));

        var s1 = await s.After(Copilot.User("ask me", 10), Copilot.ToolStart("own", "ask_user", Ask, 10), Copilot.ToolStart("sub", "ask_user", Ask, 11, "a1"));

        Assert.Equal("own", s1.Question!.Id);
        Assert.Null(s1.Question.Agent);
        Assert.Null(s1.WaitingOn);
        Assert.Equal(SubagentStatus.Waiting, Entry(s1, "a1").Status);
    }

    [Fact]
    public async Task A_sub_agents_line_changing_alone_does_not_move_updatedAt()
    {
        await using var s = new Live();
        await s.After(Delegate("t1", "a1", 1));
        var said = await s.After(Copilot.Assistant("the answer", 10, reasoning: "thinking", agentId: "a1"));
        Assert.Equal("the answer", Entry(said, "a1").Line);

        // The same reasoning again: nothing but the delegate's own line changes.
        var again = await s.After(Copilot.Assistant("", 20, reasoning: "thinking", agentId: "a1"));

        Assert.Equal(said.UpdatedAt, again.UpdatedAt);
        Assert.Equal("the answer", Entry(again, "a1").Line);
    }

    [Fact]
    public async Task A_task_agent_whose_call_fails_or_whose_main_turn_is_aborted_ends_as_failed()
    {
        await using var s = new Live();
        await s.After(Copilot.User("go", 1), Copilot.TurnStart(1),
            Copilot.ToolStart("t1", "task", new { description = "First", prompt = "p" }, 2), Copilot.SubagentStarted("t1", "s1", 2, mode: "sync"),
            Copilot.TurnStart(2, "s1"));

        var failed = await s.After(Copilot.ToolDone("t1", 5, success: false));
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Failed), (Entry(failed, "s1").Status, Entry(failed, "s1").Outcome));

        var aborted = await s.After(Copilot.ToolStart("t2", "task", new { description = "Second", prompt = "p" }, 6), Copilot.SubagentStarted("t2", "s2", 6, mode: "sync"),
            Copilot.TurnStart(6, "s2"), Copilot.Event("abort", new { reason = "user initiated" }, 7));
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Failed), (Entry(aborted, "s2").Status, Entry(aborted, "s2").Outcome));
    }

    // A host starting while delegates have filled the part of the transcript it parses.
    [Fact]
    public async Task A_host_that_starts_after_delegates_filled_its_window_still_has_the_main_agents_reply_and_their_shells()
    {
        await using var s = new Live(tailBytes: 4096);
        var filler = Enumerable.Range(0, 8).Select(i => Copilot.Assistant("", 40 + i, reasoning: new string('x', 1000), tools: ["f" + i], agentId: "a1")).ToArray();

        var cold = await s.After([
            .. Delegate("t1", "a1", 1), .. Delegate("t2", "a2", 3),
            Copilot.Assistant("Both delegates are running.", 8, reasoning: "wait for them"), Copilot.TurnEnd(8),
            Copilot.ToolResult("c1", "<command started in background with shellId: build>", 9, agentId: "a1"),
            .. filler,
            Copilot.Assistant("Build is green.", 60, agentId: "a1"),
            Copilot.Event("abort", new { reason = "user initiated" }, 61, agentId: "a2"),
        ]);

        Assert.Equal("Build is green.", cold.Activity.Response);
        Assert.Equal("a1", cold.Activity.Source!.Response);
        Assert.Equal("Both delegates are running.", cold.Activity.Own!.Response);
        Assert.Equal(1, Entry(cold, "a1").Shells);
        // Known only from the skim, then seen aborting: it failed, though no run was seen going.
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Failed), (Entry(cold, "a2").Status, Entry(cold, "a2").Outcome));
    }

    [Fact]
    public async Task The_sessions_own_form_is_answered_at_the_machine_while_a_delegates_permission_prompt_is_also_pending()
    {
        await using var s = new Live();
        await s.After(Delegate("t1", "a1", 1));

        var both = await s.After(Copilot.User("ask me", 10), Copilot.ToolStart("own", "ask_user", Ask, 10),
            Copilot.ToolStart("c9", "powershell", new { command = "gh pr checks" }, 11, "a1"), Copilot.Permission("r1", "c9", Copilot.Prompts["commands"], 11));

        Assert.Equal(("own", (string?)null), (both.Question!.Id, both.Question.Agent));
        // The CLI may be showing the delegate's dialog: keys for the form could approve it.
        Assert.True(both.Question.AnswerAtMachine);
        Assert.Equal(SubagentStatus.Waiting, Entry(both, "a1").Status);

        var alone = await s.After(Copilot.PermissionDone("r1", "c9", "approved", 12));
        Assert.Null(alone.Question!.AnswerAtMachine);
    }

    [Fact]
    public async Task Aborting_the_main_turn_ends_its_task_agents_but_not_one_a_background_agent_started()
    {
        await using var s = new Live();
        await s.After([.. Delegate("t1", "bg", 1),
            Copilot.ToolStart("t2", "task", new { description = "Nested", prompt = "p" }, 3, "bg"), Copilot.SubagentStarted("t2", "nested", 3, mode: "sync"), Copilot.TurnStart(3, "nested")]);

        var aborted = await s.After(Copilot.User("go", 4), Copilot.TurnStart(4),
            Copilot.ToolStart("t3", "task", new { description = "Own task", prompt = "p" }, 5), Copilot.SubagentStarted("t3", "own", 5, mode: "sync"), Copilot.TurnStart(5, "own"),
            Copilot.Event("abort", new { reason = "user initiated" }, 6));

        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Failed), (Entry(aborted, "own").Status, Entry(aborted, "own").Outcome));
        Assert.Equal(SubagentStatus.Working, Entry(aborted, "nested").Status);
        Assert.Equal(SubagentStatus.Working, Entry(aborted, "bg").Status);
    }

    [Fact]
    public async Task A_sub_agent_that_fails_while_asking_leaves_nothing_pending_and_reads_failed()
    {
        await using var s = new Live();
        await s.After(Delegate("t1", "a1", 1));
        var asking = await s.After(Copilot.ToolStart("c9", "powershell", new { command = "gh pr checks" }, 10, "a1"), Copilot.Permission("r1", "c9", Copilot.Prompts["commands"], 10));
        Assert.Equal("a1", asking.WaitingOn);

        var failed = await s.After(Copilot.Event("subagent.failed", new { toolCallId = "t1", agentName = "general-purpose", error = "AbortError: This operation was aborted" }, 11, "a1"));

        Assert.Null(failed.Question);
        Assert.Null(failed.WaitingOn);
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Failed), (Entry(failed, "a1").Status, Entry(failed, "a1").Outcome));
    }

    [Fact]
    public async Task A_host_starting_after_a_delegate_only_thought_in_its_window_still_has_the_main_agents_answer()
    {
        await using var s = new Live(tailBytes: 4096);
        var filler = Enumerable.Range(0, 8).Select(i => Copilot.Assistant("", 40 + i, reasoning: new string('x', 1000), agentId: "a1")).ToArray();

        var cold = await s.After([.. Delegate("t1", "a1", 1), Copilot.Assistant("The delegate is on it.", 8), Copilot.TurnEnd(8), .. filler]);

        Assert.Null(cold.Activity.Response);
        Assert.Equal(new ActivitySource { Reasoning = "a1" }, cold.Activity.Source);
        Assert.Equal("The delegate is on it.", cold.Activity.Own!.Response);
    }

    [Fact]
    public async Task A_host_starting_after_a_nested_task_agent_began_still_has_its_task_its_parent_and_how_it_ended()
    {
        await using var s = new Live(tailBytes: 4096);
        var filler = Enumerable.Range(0, 8).Select(i => Copilot.Assistant("", 40 + i, reasoning: new string('x', 1000), agentId: "elsewhere")).ToArray();

        var cold = await s.After([
            .. Delegate("t1", "bg", 1),
            Copilot.ToolStart("t2", "task", new { description = "Nested job", prompt = "Check the build" }, 3, "bg"),
            Copilot.SubagentStarted("t2", "nested", 3, mode: "sync"), Copilot.User("Check the build", 3, "nested"),
            .. filler,
            Copilot.ToolDone("t2", 60, agentId: "bg"),
        ]);

        var nested = Entry(cold, "nested");
        Assert.Equal(("Nested job", "Check the build", "bg"), (nested.Name, nested.Task, nested.Parent));
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Done, Fixture.T0.AddSeconds(60)), (nested.Status, nested.Outcome, nested.EndedAt));
    }

    [Fact]
    public async Task A_host_starting_after_a_delegate_changed_model_shows_the_model_it_changed_to()
    {
        await using var s = new Live(tailBytes: 4096);
        var filler = Enumerable.Range(0, 8).Select(i => Copilot.Assistant("", 40 + i, reasoning: new string('x', 1000), agentId: "elsewhere")).ToArray();

        var cold = await s.After([
            .. Delegate("t1", "a1", 1),
            Copilot.Event("subagent.configured", new { model = "newer-model" }, 5, "a1"),
            .. filler,
            Copilot.ToolStart("c1", "view", new { path = "README.md" }, 60, "a1"),
        ]);

        Assert.Equal("newer-model", Entry(cold, "a1").Model);
    }

    [Fact]
    public async Task A_background_agent_between_runs_ends_with_its_outcome_when_the_session_resumes_in_a_new_cli()
    {
        await using var s = new Live();
        var idle = await s.After([.. Delegate("t1", "a1", 1), Copilot.Assistant("All done.", 5, agentId: "a1"), Copilot.TurnEnd(5, "a1")]);
        Assert.Equal(SubagentStatus.Idle, Entry(idle, "a1").Status);

        s.F.Processes.Exit(AgentKind.Copilot, 100);
        s.F.Processes.Run(AgentKind.Copilot, 200, startedAt: Fixture.T0.AddSeconds(30));
        s.F.CopilotSession(Id, 200, lockWrite: Fixture.T0.AddSeconds(31));
        var resumed = await s.After(Copilot.Event("session.resume", new { resumeTime = Copilot.At(31) }, 31));
        Assert.Equal(200, resumed.ProcessId);

        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Done, Fixture.T0.AddSeconds(5)), (Entry(resumed, "a1").Status, Entry(resumed, "a1").Outcome, Entry(resumed, "a1").EndedAt));
    }

    [Fact]
    public async Task A_background_agent_that_aborts_takes_the_task_agent_it_was_waiting_on_and_its_question_with_it()
    {
        await using var s = new Live();
        var asking = await s.After([.. Delegate("t1", "bg", 1),
            Copilot.ToolStart("t2", "task", new { description = "Nested", prompt = "p" }, 3, "bg"), Copilot.SubagentStarted("t2", "nested", 3, mode: "sync"), Copilot.TurnStart(3, "nested"),
            Copilot.ToolStart("c9", "powershell", new { command = "gh pr checks" }, 4, "nested"), Copilot.Permission("r1", "c9", Copilot.Prompts["commands"], 4)]);
        Assert.Equal("nested", asking.WaitingOn);

        var aborted = await s.After(Copilot.Event("abort", new { reason = "user initiated" }, 5, "bg"));

        var nested = Entry(aborted, "nested");
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Failed, (ToolCall?)null), (nested.Status, nested.Outcome, nested.CurrentTool));
        Assert.Null(aborted.Question);
        Assert.Null(aborted.WaitingOn);
    }

    [Fact]
    public async Task A_task_agent_whose_call_fails_while_it_asks_leaves_nothing_pending()
    {
        await using var s = new Live();
        var asking = await s.After(Copilot.User("go", 1), Copilot.TurnStart(1),
            Copilot.ToolStart("t1", "task", new { description = "Check", prompt = "p" }, 2), Copilot.SubagentStarted("t1", "s1", 2, mode: "sync"), Copilot.TurnStart(2, "s1"),
            Copilot.ToolStart("c9", "powershell", new { command = "gh pr checks" }, 3, "s1"), Copilot.Permission("r1", "c9", Copilot.Prompts["commands"], 3));
        Assert.Equal("s1", asking.WaitingOn);

        var failed = await s.After(Copilot.ToolDone("t1", 4, success: false));

        Assert.Null(failed.Question);
        Assert.Null(failed.WaitingOn);
        Assert.Equal((SubagentStatus.Ended, SubagentOutcome.Failed), (Entry(failed, "s1").Status, Entry(failed, "s1").Outcome));
    }

    [Fact]
    public async Task A_host_starting_after_a_delegates_run_began_takes_its_late_idle_notice_as_how_it_ended()
    {
        await using var s = new Live(tailBytes: 4096);
        var filler = Enumerable.Range(0, 8).Select(i => Copilot.Assistant("", 40 + i, reasoning: new string('x', 1000), agentId: "elsewhere")).ToArray();

        var cold = await s.After([.. Delegate("t1", "a1", 1), .. filler,
            Copilot.Event("system.notification", new { content = "idle", kind = new { type = "agent_idle", agentId = "a1" } }, 60)]);

        Assert.Equal((SubagentOutcome.Done, Fixture.T0.AddSeconds(60)), (Entry(cold, "a1").Outcome, Entry(cold, "a1").EndedAt));
    }

    [Fact]
    public async Task A_host_starting_between_a_delegates_call_and_its_permission_prompt_still_knows_whose_prompt_it_is()
    {
        await using var s = new Live(tailBytes: 4096);
        var filler = Enumerable.Range(0, 8).Select(i => Copilot.Assistant("", 40 + i, reasoning: new string('x', 1000), agentId: "elsewhere")).ToArray();

        var cold = await s.After([.. Delegate("t1", "a1", 1),
            Copilot.ToolStart("c9", "powershell", new { command = "gh pr checks" }, 30, "a1"), .. filler,
            Copilot.Permission("r1", "c9", Copilot.Prompts["commands"], 60)]);

        Assert.Equal(("a1", "a1"), (cold.Question!.Agent, cold.WaitingOn));
    }
}
