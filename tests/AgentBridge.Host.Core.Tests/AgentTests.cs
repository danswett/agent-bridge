using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

public class ClaudeTests
{
    const string Id = "55555555-5555-4555-8555-555555555555";

    sealed class Session_ : IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly string Transcript;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Session_(string? hookStatus = null, DateTimeOffset? hookAt = null)
        {
            Transcript = F.ClaudeTranscript(Id);
            F.ClaudeRegistration(Id, 900, Transcript, hookStatus, hookAt);
            F.Processes.Run(AgentKind.Claude, 900);
            Source = F.Source();
        }

        public async Task<Session> After(params string[] lines)
        {
            Fixture.Append(Transcript, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            F.Dispose();
        }
    }

    [Fact]
    public async Task A_prompt_makes_it_working_and_a_turn_duration_record_makes_it_idle()
    {
        await using var s = new Session_();

        var working = await s.After(Claude.User("read the file", 1), Claude.Assistant(2, null, Claude.Thinking("I should read it first")));
        Assert.Equal(SessionStatus.Working, working.Status);
        Assert.Equal("I should read it first", working.Activity.Reasoning);
        Assert.Equal("claude-fixture", working.Model);
        Assert.Equal("feature", working.Branch);
        Assert.Equal("/work/repo", working.Cwd);

        var idle = await s.After(Claude.Assistant(3, null, Claude.Text("It says hello.")), Claude.System("turn_duration", 4));
        Assert.Equal(SessionStatus.Idle, idle.Status);
        Assert.Equal(Fixture.T0.AddSeconds(4), idle.StatusSince);
        Assert.Equal("It says hello.", idle.Activity.Response);
        Assert.Equal(1, idle.Activity.TurnCount);
    }

    [Fact]
    public async Task Without_a_turn_end_record_an_end_turn_reply_with_nothing_pending_reads_idle()
    {
        await using var s = new Session_();

        var tool = await s.After(Claude.User("list files", 1),
            Claude.Assistant(2, "tool_use", Claude.ToolUse("tu1", "Bash", new JsonObject { ["command"] = "ls -la", ["description"] = "List files" })));
        Assert.Equal(SessionStatus.Working, tool.Status);
        Assert.Equal("List files", tool.Activity.CurrentTool!.Summary);

        var streaming = await s.After(Claude.ToolResult("tu1", 3), Claude.Assistant(4, null, Claude.Text("partial")));
        Assert.Equal(SessionStatus.Working, streaming.Status);
        Assert.Equal(ToolStatus.Done, Assert.Single(streaming.Activity.History!).Status);

        var idle = await s.After(Claude.Assistant(5, "end_turn", Claude.Text("Here are the files.")));
        Assert.Equal(SessionStatus.Idle, idle.Status);
        Assert.Equal(Fixture.T0.AddSeconds(5), idle.StatusSince);
    }

    [Fact]
    public async Task Several_text_blocks_in_one_reply_make_one_response()
    {
        await using var s = new Session_();

        var s1 = await s.After(Claude.User("explain", 1), Claude.Assistant(2, "end_turn", Claude.Text("First part."), Claude.Thinking("hmm"), Claude.Text("Second part.")));

        Assert.Equal("First part.\n\nSecond part.", s1.Activity.Response);

        var streamed = await s.After(Claude.User("again", 3), Claude.AssistantMessage("msg_2", 4, null, Claude.Text("Streamed one.")),
            Claude.AssistantMessage("msg_2", 5, "end_turn", Claude.Text("Streamed two.")));

        Assert.Equal("Streamed one.\n\nStreamed two.", streamed.Activity.Response);

        var next = await s.After(Claude.User("third", 6), Claude.AssistantMessage("msg_3", 7, "end_turn", Claude.Text("A new reply.")));

        Assert.Equal("A new reply.", next.Activity.Response);
    }

    [Fact]
    public async Task An_interrupt_from_the_user_ends_the_turn_and_fails_the_running_tool()
    {
        await using var s = new Session_();

        var s1 = await s.After(Claude.User("run it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("tu1", "Bash", new JsonObject { ["command"] = "sleep 99" })),
            Claude.User("[Request interrupted by user for tool use]", 3));

        Assert.Equal(SessionStatus.Idle, s1.Status);
        Assert.Equal(ToolStatus.Failed, Assert.Single(s1.Activity.History!).Status);
        Assert.Equal("run it", s1.Activity.UserMessage);
    }

    [Fact]
    public async Task Sidechain_meta_and_slash_command_entries_are_not_the_sessions_activity()
    {
        await using var s = new Session_();

        var s1 = await s.After(Claude.User("real prompt", 1), Claude.Assistant(2, "end_turn", Claude.Text("real answer")),
            Claude.User("subagent prompt", 3, sidechain: true),
            "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"caveat\"},\"timestamp\":\"2026-10-28T04:00:04.000Z\"}",
            Claude.User("<command-name>/model</command-name>", 5),
            "{\"type\":\"ai-title\",\"aiTitle\":\"Reading the sample\",\"sessionId\":\"x\"}");

        Assert.Equal(SessionStatus.Idle, s1.Status);
        Assert.Equal("real prompt", s1.Activity.UserMessage);
        Assert.Equal("Reading the sample", s1.Title);
        Assert.Equal(1, s1.Activity.TurnCount);
    }

    [Fact]
    public async Task AskUserQuestion_is_a_form_with_a_field_per_question_until_its_tool_result_arrives()
    {
        await using var s = new Session_();
        var input = new JsonObject
        {
            ["questions"] = new JsonArray(
                new JsonObject
                {
                    ["question"] = "Which database?",
                    ["header"] = "Database",
                    ["multiSelect"] = false,
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "PostgreSQL", ["description"] = "Relational" }, new JsonObject { ["label"] = "SQLite" }),
                },
                new JsonObject
                {
                    ["question"] = "Which features?",
                    ["multiSelect"] = true,
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "Auth" }, new JsonObject { ["label"] = "Search" }),
                }),
        };

        var waiting = await s.After(Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("ask1", "AskUserQuestion", input)));
        Assert.Equal(SessionStatus.Waiting, waiting.Status);
        var q = waiting.Question!;
        Assert.Equal("ask1", q.Id);
        Assert.Contains("Which database?", q.Message);
        Assert.Contains("**PostgreSQL**: Relational", q.Message);
        Assert.Equal(2, q.Fields.Count);
        Assert.Equal(("Which database?", "Database", FieldType.Choice), (q.Fields[0].Name, q.Fields[0].Title, q.Fields[0].Type));
        Assert.Equal(["PostgreSQL", "SQLite"], q.Fields[0].Options!.Select(o => o.Value));
        Assert.Null(q.Fields[0].AllowOther);
        Assert.Null(q.AnswerAtMachine);
        Assert.Equal(FieldType.Multi, q.Fields[1].Type);

        var answered = await s.After(Claude.ToolResult("ask1", 9));
        Assert.Null(answered.Question);
        Assert.Equal(SessionStatus.Working, answered.Status);
    }

    [Fact]
    public async Task A_hook_status_newer_than_the_transcript_overrides_what_the_transcript_implies()
    {
        await using var s = new Session_(hookStatus: "idle", hookAt: Fixture.T0.AddSeconds(30));

        var s1 = await s.After(Claude.User("go", 1), Claude.Assistant(2, null, Claude.Text("still streaming")));

        Assert.Equal(SessionStatus.Idle, s1.Status);
    }

    [Fact]
    public async Task A_hook_status_older_than_the_transcript_is_ignored()
    {
        await using var s = new Session_(hookStatus: "idle", hookAt: Fixture.T0);

        var s1 = await s.After(Claude.User("go", 1), Claude.Assistant(2, null, Claude.Text("still streaming")));

        Assert.Equal(SessionStatus.Working, s1.Status);
    }

    [Fact]
    public async Task A_waiting_hook_over_a_running_tool_is_an_approval_for_that_tool()
    {
        await using var s = new Session_(hookStatus: "waiting", hookAt: Fixture.T0.AddSeconds(30));

        var s1 = await s.After(Claude.User("deploy", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("tu9", "Bash", new JsonObject { ["command"] = "deploy.sh" })));

        Assert.Equal(SessionStatus.Waiting, s1.Status);
        Assert.Equal(("tu9", QuestionKind.Approval), (s1.Question!.Id, s1.Question.Kind));
        Assert.Contains("deploy.sh", s1.Question.Message);
        // agent-ha-bridge only mirrors Claude's permission prompts, so this one is answered there.
        Assert.True(s1.Question.AnswerAtMachine);
    }

    [Fact]
    public async Task An_AskUserQuestion_the_keys_cannot_drive_is_answered_at_the_machine()
    {
        await using var s = new Session_();
        var five = Claude.Ask([.. Enumerable.Range(1, 5).Select(i => ($"Question {i}?", false, new[] { "a", "b" }))]);
        var tenOptions = Claude.Ask(("Which ones?", true, [.. Enumerable.Range(1, 10).Select(i => $"o{i}")]));
        var noOptions = Claude.Ask(("Anything?", false, Array.Empty<string>()));
        var fine = Claude.Ask(("Which ones?", true, ["a", "b", "c"]), ("Which one?", false, ["x", "y"]));

        foreach (var (input, atMachine) in new[] { (five, true), (tenOptions, true), (noOptions, true), (fine, false) })
        {
            var id = "ask" + Guid.NewGuid().ToString("N")[..6];
            var q = (await s.After(Claude.Assistant(2, "tool_use", Claude.ToolUse(id, "AskUserQuestion", input)))).Question!;
            Assert.Equal((id, atMachine ? true : (bool?)null), (q.Id, q.AnswerAtMachine));
            await s.After(Claude.ToolResult(id, 3));
        }
    }
}

public class CodexTests
{
    const string Id = "66666666-6666-4666-8666-666666666666";

    [Fact]
    public async Task Task_started_and_complete_drive_status_and_the_older_event_set_fills_the_activity()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout, updated: Fixture.T0);
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout,
            Codex.Line("session_meta", new { id = Id, timestamp = Copilot.At(0), cwd = "/work/codex", git = new { branch = "dev", repository_url = "git@github.com:octo/codex-repo.git" } }, 0),
            Codex.Line("turn_context", new { model = "gpt-turn", cwd = "/work/codex" }, 1),
            Codex.Event(new { type = "task_started" }, 1),
            Codex.Event(new { type = "user_message", message = "count the files" }, 1),
            Codex.Event(new { type = "agent_reasoning", text = "**Counting**" }, 2),
            Codex.Item(new { type = "function_call", name = "shell", call_id = "call1", arguments = "{\"command\":[\"bash\",\"-lc\",\"ls | wc -l\"]}" }, 3));
        await using var source = f.Source();

        await source.RefreshAsync();
        var working = source.Get(Id)!;
        Assert.Equal(SessionStatus.Working, working.Status);
        Assert.Equal("ls | wc -l", working.Activity.CurrentTool!.Summary);
        Assert.Equal("count the files", working.Title);
        Assert.Equal("**Counting**", working.Activity.Reasoning);
        Assert.Equal("gpt-turn", working.Model);
        Assert.Equal(("dev", "octo/codex-repo"), (working.Branch, working.Repository));

        Fixture.Append(rollout,
            Codex.Item(new { type = "function_call_output", call_id = "call1", output = "42" }, 4),
            Codex.Event(new { type = "agent_message", message = "There are 42." }, 5),
            Codex.Event(new { type = "task_complete", last_agent_message = "There are 42 files." }, 6));
        await source.RefreshAsync();
        var idle = source.Get(Id)!;
        Assert.Equal(SessionStatus.Idle, idle.Status);
        Assert.Equal(Fixture.T0.AddSeconds(6), idle.StatusSince);
        Assert.Equal("There are 42 files.", idle.Activity.Response);
        Assert.Equal(1, idle.Activity.TurnCount);
        Assert.Equal(ToolStatus.Done, Assert.Single(idle.Activity.History!).Status);
    }

    [Fact]
    public async Task The_newer_item_completed_events_are_read_as_well()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout);
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout,
            Codex.Event(new { type = "task_started" }, 1),
            Codex.Event(new { type = "item_completed", item = new { type = "UserMessage", id = "u1", content = new[] { new { type = "text", text = "Measure four" } } } }, 2),
            Codex.Event(new { type = "item_completed", item = new { type = "Reasoning", id = "r1", summary_text = new[] { "**Filling**", "then pouring" } } }, 3),
            Codex.Event(new { type = "item_completed", item = new { type = "CommandExecution", id = "c1", command = "echo hi", status = "completed", exit_code = 0 } }, 4),
            Codex.Event(new { type = "item_completed", item = new { type = "AgentMessage", id = "a1", content = new[] { new { type = "Text", text = "Fill the five." } } } }, 5));
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = source.Get(Id)!;
        Assert.Equal("Measure four", s.Activity.UserMessage);
        Assert.Equal("**Filling**\nthen pouring", s.Activity.Reasoning);
        Assert.Equal("Fill the five.", s.Activity.Response);
        Assert.Equal(("echo hi", ToolStatus.Done), (Assert.Single(s.Activity.History!).Summary, s.Activity.History![0].Status));
    }

    [Fact]
    public async Task An_exec_approval_request_waits_as_an_approval_until_the_command_starts()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout);
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1),
            Codex.Event(new { type = "exec_approval_request", call_id = "call7", command = new[] { "rm", "-rf", "dist" }, reason = "Clean the build" }, 2));
        await using var source = f.Source();

        await source.RefreshAsync();
        var waiting = source.Get(Id)!;
        Assert.Equal(SessionStatus.Waiting, waiting.Status);
        Assert.Equal(("call7", QuestionKind.Approval), (waiting.Question!.Id, waiting.Question.Kind));
        Assert.Contains("rm -rf dist", waiting.Question.Message);
        Assert.Contains("Clean the build", waiting.Question.Message);
        // Without agent-ha-bridge's marker there is no generation to check before typing.
        Assert.True(waiting.Question.AnswerAtMachine);

        Fixture.Append(rollout, Codex.Event(new { type = "exec_command_begin", call_id = "call7", command = new[] { "rm", "-rf", "dist" } }, 3));
        await source.RefreshAsync();
        Assert.Null(source.Get(Id)!.Question);
        Assert.Equal(SessionStatus.Working, source.Get(Id)!.Status);
    }

    [Fact]
    public async Task A_command_that_failed_stays_failed_when_its_output_arrives_in_the_response_stream()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout);
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1),
            Codex.Item(new { type = "function_call", name = "shell", call_id = "call3", arguments = "{\"command\":[\"false\"]}" }, 2),
            Codex.Event(new { type = "exec_command_begin", call_id = "call3", command = new[] { "false" } }, 2),
            Codex.Event(new { type = "exec_command_end", call_id = "call3", exit_code = 1 }, 3),
            Codex.Item(new { type = "function_call_output", call_id = "call3", output = "exit 1" }, 4));
        await using var source = f.Source();

        await source.RefreshAsync();

        var tool = Assert.Single(source.Get(Id)!.Activity.History!);
        Assert.Equal((ToolStatus.Failed, Fixture.T0.AddSeconds(3)), (tool.Status, tool.EndedAt));
    }

    [Fact]
    public async Task An_approval_marker_beside_the_registration_is_an_approval_question()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout, status: "waiting", updated: Fixture.T0.AddSeconds(10));
        File.WriteAllText(Path.Combine(f.CodexRegistrations, Id + ".approval.json"),
            new JsonObject { ["SessionId"] = Id, ["DecisionId"] = "node-1", ["Question"] = "Needs approval: git push", ["Created"] = Fixture.T0.AddSeconds(10).ToString("o") }.ToJsonString());
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1));
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal(SessionStatus.Waiting, s.Status);
        Assert.Equal(("node-1", "Needs approval: git push"), (s.Question!.Id, s.Question.Message));
        Assert.Equal(Fixture.T0.AddSeconds(10), s.Question.AskedAt);
        Assert.Equal("decision", Assert.Single(s.Question.Fields).Name);
        Assert.Equal(["approve", "deny"], s.Question.Fields[0].Options!.Select(o => o.Value));
        Assert.Null(s.Question.AnswerAtMachine);
    }

    [Fact]
    public async Task The_marker_stands_for_the_approval_the_rollout_shows_so_the_one_shown_can_be_answered()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout, status: "waiting", updated: Fixture.T0.AddSeconds(10));
        f.CodexApprovalMarker(Id, "node-2", created: Fixture.T0.AddSeconds(3));
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1),
            Codex.Event(new { type = "exec_approval_request", call_id = "call7", command = new[] { "git", "push" } }, 2));
        await using var source = f.Source();

        await source.RefreshAsync();

        var q = source.Get(Id)!.Question!;
        Assert.Equal(("node-2", (bool?)null), (q.Id, q.AnswerAtMachine));
    }

    [Fact]
    public async Task A_marker_agent_ha_bridge_already_typed_into_is_answered_at_the_machine_and_an_unreadable_one_is_not_shown()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout, status: "waiting", updated: Fixture.T0.AddSeconds(10));
        f.CodexApprovalMarker(Id, "node-3", attempted: true);
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1));
        await using var source = f.Source();

        await source.RefreshAsync();
        Assert.Equal(("node-3", (bool?)true), (source.Get(Id)!.Question!.Id, source.Get(Id)!.Question!.AnswerAtMachine));

        // Its own sender refuses these as unreadable, so they are no approval to answer.
        foreach (var marker in new Action[]
        {
            () => f.CodexApprovalMarker(Id, "node-4", attempted: false),
            () => f.CodexApprovalMarker(Id, "node-5", attempted: "yes"),
            () => f.CodexApprovalMarker(Id, "node-6", owner: "someone-else"),
            () => File.WriteAllText(Path.Combine(f.CodexRegistrations, Id + ".approval.json"), "{\"SessionId\":"),
        })
        {
            marker();
            await source.RefreshAsync();
            Assert.Null(source.Get(Id)!.Question);
        }
    }

    [Fact]
    public async Task A_registration_status_newer_than_the_rollout_is_used()
    {
        using var f = new Fixture();
        var rollout = f.CodexTranscript(Id);
        f.CodexRegistration(Id, 950, rollout, status: "idle", updated: Fixture.T0.AddSeconds(20));
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1));
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = source.Get(Id)!;
        Assert.Equal(SessionStatus.Idle, s.Status);
        Assert.Equal("gpt-fixture", s.Model);
    }
}
