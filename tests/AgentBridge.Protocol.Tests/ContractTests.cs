using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

public class ContractTests
{
    static string Example(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", name));

    [Fact]
    public void The_documented_example_reads_into_the_records_and_writes_back_unchanged()
    {
        var json = Example("sessions-list.json");
        var list = JsonSerializer.Deserialize<SessionList>(json, ProtocolJson.Options)!;
        var back = JsonSerializer.Serialize(list, ProtocolJson.Options);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(back)), back);
        Assert.Equal(4, list.Sessions.Count);
        Assert.Equal(SessionStatus.Waiting, list.Sessions[0].Status);
        Assert.Equal(FieldType.Multi, list.Sessions[0].Question!.Fields[1].Type);
        Assert.Null(list.Sessions[0].Activity.Background);
        Assert.Equal(SessionStatus.Idle, list.Sessions[3].Status);
        Assert.Equal(new Background { Agents = 2, Shells = 1 }, list.Sessions[3].Activity.Background);
    }

    [Fact]
    public void Background_work_is_absent_when_nothing_runs_and_sends_both_counts_when_anything_does()
    {
        Assert.Null(Background.Of(0, 0));
        var activity = new Activity { Background = Background.Of(0, 3) };
        Assert.Equal("""{"background":{"agents":0,"shells":3}}""", JsonSerializer.Serialize(activity, ProtocolJson.Options));
        Assert.Equal("{}", JsonSerializer.Serialize(new Activity { Background = Background.Of(0, 0) }, ProtocolJson.Options));
    }

    [Fact]
    public void A_session_is_sent_as_the_persons_by_leaving_the_driver_out_and_an_unknown_driver_reads_as_the_persons()
    {
        var person = new Session { Id = "x", Agent = AgentKind.Copilot, Status = SessionStatus.Idle, StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Activity = new Activity() };
        Assert.DoesNotContain("driver", JsonSerializer.Serialize(person, ProtocolJson.Options), StringComparison.Ordinal);

        var json = JsonSerializer.Serialize(person with { Driver = DriverKind.Agent, DriverPrincipal = "homeassistant:0f1e2d3c" }, ProtocolJson.Options);
        Assert.Contains("\"driver\":\"agent\",\"driverPrincipal\":\"homeassistant:0f1e2d3c\"", json, StringComparison.Ordinal);
        var newer = JsonSerializer.Deserialize<Session>(json.Replace("\"driver\":\"agent\"", "\"driver\":\"robot\"", StringComparison.Ordinal), ProtocolJson.Options)!;
        Assert.Equal(DriverKind.Person, newer.Driver);
    }

    [Fact]
    public void An_emoji_is_escaped_as_a_surrogate_pair_so_only_the_session_budget_bounds_the_json()
    {
        // 4,096 emoji are exactly the 16 KiB response limit as text, and 48 KiB as JSON.
        var text = string.Concat(Enumerable.Repeat("😀", 4096));
        Assert.Equal(Limits.Response, System.Text.Encoding.UTF8.GetByteCount(text));
        Assert.Contains("\\uD83D\\uDE00", JsonSerializer.Serialize(text, ProtocolJson.Options), StringComparison.OrdinalIgnoreCase);
        Assert.True(ProtocolJson.ByteCount(text) > SessionBudget.MaxSessionBytes);

        var s = SessionBudget.Fit(Big("emoji", text));
        Assert.True(ProtocolJson.ByteCount(s) <= SessionBudget.MaxSessionBytes);
        Assert.True(s.Activity.ResponseTruncated);
    }

    [Fact]
    public void Enums_and_names_are_written_in_camel_case_and_nulls_are_left_out()
    {
        var s = new Session
        {
            Id = "x",
            Agent = AgentKind.Copilot,
            Status = SessionStatus.Working,
            StatusSince = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Activity = new Activity { CurrentTool = new ToolCall { Id = "t", Name = "edit", Status = ToolStatus.Running, StartedAt = DateTimeOffset.UnixEpoch } },
        };
        var json = JsonSerializer.Serialize(s, ProtocolJson.Options);

        Assert.Contains("\"agent\":\"copilot\"", json);
        Assert.Contains("\"status\":\"working\"", json);
        Assert.Contains("\"currentTool\":{", json);
        Assert.DoesNotContain("null", json);
        Assert.DoesNotContain("question", json);
    }

    [Fact]
    public void Times_are_written_in_UTC_with_milliseconds_and_a_Z_whatever_their_offset()
    {
        var call = new ToolCall { Id = "t", Name = "edit", Status = ToolStatus.Done, StartedAt = new DateTimeOffset(2026, 10, 27, 21, 22, 43, 0, TimeSpan.FromHours(-7)) };
        Assert.Contains("\"startedAt\":\"2026-10-28T04:22:43.000Z\"", JsonSerializer.Serialize(call, ProtocolJson.Options));
    }

    [Fact]
    public void A_client_reading_a_newer_host_ignores_fields_it_does_not_know()
    {
        var json = """{"id":"x","agent":"claude","status":"idle","statusSince":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z","activity":{},"somethingNew":{"a":1}}""";
        var s = JsonSerializer.Deserialize<Session>(json, ProtocolJson.Options)!;
        Assert.Equal(AgentKind.Claude, s.Agent);
    }

    [Fact]
    public void The_list_form_drops_only_the_tool_history()
    {
        var call = new ToolCall { Id = "t", Name = "edit", Status = ToolStatus.Done, StartedAt = DateTimeOffset.UnixEpoch };
        var s = new Session
        {
            Id = "x", Agent = AgentKind.Codex, Status = SessionStatus.Idle, StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
            Activity = new Activity { Response = "hi", History = [call] },
        };
        var listed = s.WithoutHistory();
        Assert.Null(listed.Activity.History);
        Assert.Equal("hi", listed.Activity.Response);
        Assert.NotNull(s.Activity.History);
    }

    [Theory]
    [InlineData("abcdef", 4, "cdef", true)]
    [InlineData("abc", 4, "abc", false)]
    [InlineData("aé世", 4, "世", true)] // é is 2 bytes and 世 3: keeping 4 bytes can't take half of é
    public void Long_text_keeps_its_end_within_a_byte_limit(string text, int maxBytes, string expected, bool truncated)
    {
        Assert.Equal((expected, truncated), Limits.KeepEnd(text, maxBytes));
    }

    [Fact]
    public void Cutting_text_never_splits_a_character()
    {
        var text = "ab😀cd"; // the emoji is 4 UTF-8 bytes, two UTF-16 units
        Assert.Equal("cd", Limits.KeepEnd(text, 5).Text); // 5 bytes would need part of the emoji
        Assert.Equal("ab", Limits.KeepStart(text, 5));
        Assert.Equal("ab😀", Limits.KeepStart(text, 6));
    }

    [Fact]
    public void Enum_values_from_a_newer_host_read_as_the_documented_fallbacks_instead_of_failing_the_list()
    {
        var json = """
            {"sessions":[{"id":"x","agent":"gemini","status":"paused","statusSince":"2026-01-01T00:00:00.000Z","updatedAt":"2026-01-01T00:00:00.000Z",
              "activity":{"currentTool":{"id":"t","name":"n","status":"cancelled","startedAt":"2026-01-01T00:00:00.000Z"}},
              "question":{"id":"q","kind":"survey","message":"m","askedAt":"2026-01-01T00:00:00.000Z","fields":[{"name":"d","type":"date"}]}}],
             "revision":1}
            """;
        var s = JsonSerializer.Deserialize<SessionList>(json, ProtocolJson.Options)!.Sessions.Single();

        Assert.Equal(AgentKind.Unknown, s.Agent);
        Assert.Equal(SessionStatus.Idle, s.Status);
        Assert.Equal(ToolStatus.Done, s.Activity.CurrentTool!.Status);
        Assert.Equal(QuestionKind.Form, s.Question!.Kind);
        Assert.Equal(FieldType.Text, s.Question.Fields[0].Type);
    }

    static Session Big(string id, string response, IReadOnlyList<ToolCall>? history = null, Question? question = null) => new()
    {
        Id = id, Agent = AgentKind.Copilot, Status = question is null ? SessionStatus.Working : SessionStatus.Waiting,
        StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        Activity = new Activity { Response = Limits.KeepEnd(response, Limits.Response).Text, History = history },
        Question = question,
    };

    [Fact]
    public void Five_sessions_with_full_size_responses_are_listed_in_pages_that_each_fit_a_message()
    {
        var sessions = Enumerable.Range(0, 5).Select(i => SessionBudget.Fit(Big($"s{i}", new string('x', 20_000)))).ToList();
        var pages = Paging.Pages(sessions).ToList();
        // The largest hold a host sends: its reason all emoji, each escaped to 12 bytes.
        var held = new DiscoveryHeld(Limits.KeepStart(string.Concat(Enumerable.Repeat("😀", 400)), DiscoveryHeld.MaxReasonBytes)!, [AgentKind.Copilot, AgentKind.Claude, AgentKind.Codex]);

        Assert.True(pages.Count > 1);
        Assert.Equal(sessions.Select(s => s.Id), pages.SelectMany(p => p).Select(s => s.Id));
        foreach (var p in pages)
        {
            var bytes = ProtocolJson.ByteCount(new { re = int.MaxValue, ok = true, result = new SessionList(p, long.MaxValue, new string('c', 64), held) });
            Assert.True(bytes <= ProtocolJson.MaxMessageBytes, $"{bytes} bytes");
        }
    }

    [Fact]
    public void A_discovery_hold_is_sent_as_its_reason_and_agents_and_left_out_when_there_is_none()
    {
        var held = new DiscoveryHeld("an unrecognised copilot process (pid 4120)", [AgentKind.Copilot]);

        Assert.Equal("""{"revision":7,"discoveryHeld":{"reason":"an unrecognised copilot process (pid 4120)","agents":["copilot"]}}""",
            JsonSerializer.Serialize(new SessionsSynced(7, held), ProtocolJson.Options));
        Assert.Equal("""{"revision":7}""", JsonSerializer.Serialize(new SessionsSynced(7), ProtocolJson.Options));
        Assert.Equal("{}", JsonSerializer.Serialize(new DiscoveryChanged(), ProtocolJson.Options));
    }

    [Fact]
    public void A_full_size_response_in_Chinese_is_sent_as_UTF_8_and_fits_the_session_budget()
    {
        var s = SessionBudget.Fit(Big("cjk", new string('世', 16_000)));
        var json = JsonSerializer.Serialize(s, ProtocolJson.Options);

        Assert.Contains("世世世", json);
        Assert.DoesNotContain("\\u4E16", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(ProtocolJson.ByteCount(s) <= SessionBudget.MaxSessionBytes);
    }

    [Fact]
    public void A_session_too_big_for_the_budget_loses_its_oldest_history_first_and_keeps_its_question()
    {
        var history = Enumerable.Range(0, 50).Select(i => new ToolCall { Id = $"t{i}", Name = "powershell", Summary = new string('s', 190), Status = ToolStatus.Done, StartedAt = DateTimeOffset.UnixEpoch }).ToList();
        var question = new Question { Id = "q", Kind = QuestionKind.Form, Message = "Pick one", AskedAt = DateTimeOffset.UnixEpoch, Fields = [new Field { Name = "a", Type = FieldType.Choice, Options = [new("x"), new("y")] }] };
        var s = SessionBudget.Fit(Big("h", new string('x', 20_000), history, question), maxBytes: 24 * 1024);

        Assert.True(ProtocolJson.ByteCount(s) <= 24 * 1024);
        Assert.Equal("t49", s.Activity.History!.Last().Id);
        Assert.True(s.Activity.History!.Count < 50);
        Assert.Equal(2, s.Question!.Fields[0].Options!.Count);
        Assert.Null(s.Question.Truncated);
    }

    [Fact]
    public void A_question_whose_options_cannot_fit_is_sent_marked_truncated_rather_than_over_the_limit()
    {
        var options = Enumerable.Range(0, 400).Select(i => new Option($"v{i}", new string('o', 190))).ToList();
        var question = new Question { Id = "q", Kind = QuestionKind.Form, Message = new string('m', 8_000), AskedAt = DateTimeOffset.UnixEpoch, Fields = [new Field { Name = "a", Type = FieldType.Multi, Options = options }] };
        var s = SessionBudget.Fit(Big("q", "short", question: question));

        Assert.True(ProtocolJson.ByteCount(s) <= SessionBudget.MaxSessionBytes);
        Assert.True(s.Question!.Truncated);
        Assert.Empty(s.Question.Fields);
        Assert.Equal("short", s.Activity.Response);
    }

    [Fact]
    public void The_documented_session_with_sub_agents_reads_into_the_records_and_writes_back_unchanged()
    {
        var json = Example("session-subagents.json");
        var s = JsonSerializer.Deserialize<Session>(json, ProtocolJson.Options)!;
        var back = JsonSerializer.Serialize(s, ProtocolJson.Options);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(back)), back);
        Assert.Equal("agent-c204", s.WaitingOn);
        Assert.Equal("agent-c204", s.Question!.Agent);
        Assert.Equal([SubagentStatus.Waiting, SubagentStatus.Working, SubagentStatus.Ended], s.Subagents!.Select(a => a.Status));
        Assert.Equal((SubagentKind.Task, "agent-c204", SubagentOutcome.Done), (s.Subagents![2].Kind, s.Subagents[2].Parent, s.Subagents[2].Outcome));
        Assert.Equal(new ActivitySource { Response = "agent-3a91", Reasoning = "agent-c204" }, s.Activity.Source);
        Assert.StartsWith("Started 2", s.Activity.Own!.Response);
    }

    [Fact]
    public void Sub_agent_values_from_a_newer_host_read_as_the_documented_fallbacks()
    {
        var a = JsonSerializer.Deserialize<Subagent>("""{"id":"x","kind":"fork","status":"paused","outcome":"cancelled","shells":0}""", ProtocolJson.Options)!;

        Assert.Equal((SubagentKind.Task, SubagentStatus.Idle, SubagentOutcome.Done), (a.Kind, a.Status, a.Outcome));
    }

    static Subagent Delegate(int i, SubagentStatus status, string text) => new()
    {
        Id = $"agent-{i:D2}",
        Kind = SubagentKind.Background,
        Name = $"Delegate {i}",
        Task = Limits.KeepStart(text, Limits.SubagentTask),
        Model = "claude-haiku-5.5",
        Status = status,
        Outcome = status is SubagentStatus.Ended or SubagentStatus.Idle ? SubagentOutcome.Done : null,
        StartedAt = DateTimeOffset.UnixEpoch,
        Line = Limits.KeepEnd(text, Limits.SubagentLine).Text,
        CurrentTool = status == SubagentStatus.Working ? new ToolCall { Id = $"t{i}", Name = "powershell", Summary = "dotnet test", Status = ToolStatus.Running, StartedAt = DateTimeOffset.UnixEpoch } : null,
        Shells = 1,
    };

    [Fact]
    public void A_session_with_twenty_sub_agents_stays_within_its_budget_and_counts_the_ones_it_left_out()
    {
        // Every text at its limit, in emoji, which escapes to three times its size.
        var emoji = string.Concat(Enumerable.Repeat("😀", 4096));
        var s = Big("delegating", emoji) with
        {
            Subagents = [.. Enumerable.Range(0, 20).Select(i => Delegate(i, i < 12 ? SubagentStatus.Working : SubagentStatus.Ended, emoji))],
            Activity = Big("x", emoji).Activity with
            {
                Reasoning = Limits.KeepEnd(emoji, Limits.Reasoning).Text,
                Source = new ActivitySource { Response = "agent-00", Reasoning = "agent-01" },
                Own = new OwnActivity { Response = Limits.KeepEnd(emoji, Limits.Response).Text, Reasoning = Limits.KeepEnd(emoji, Limits.Reasoning).Text },
            },
        };
        Assert.True(ProtocolJson.ByteCount(s) > 3 * SessionBudget.MaxSessionBytes);

        var fit = SessionBudget.Fit(s);

        Assert.True(ProtocolJson.ByteCount(fit) <= SessionBudget.MaxSessionBytes, $"{ProtocolJson.ByteCount(fit)} bytes");
        // The session's own fields alone overflow it, so every sub-agent field went first.
        Assert.Null(fit.Subagents);
        Assert.Equal(20, fit.SubagentsMore);
        Assert.Equal(((string?)null, (string?)null, true), (fit.Activity.Own!.Response, fit.Activity.Own.Reasoning, fit.Activity.Own.ResponseTruncated));
        Assert.True(fit.Activity.ResponseTruncated);
    }

    [Fact]
    public void Running_delegates_beyond_the_cap_are_counted_and_the_rest_keep_their_tools_while_the_session_fits()
    {
        var text = new string('t', 2000);
        var s = Big("delegating", "short") with
        {
            Subagents = [.. Enumerable.Range(0, 20).Select(i => Delegate(i, SubagentStatus.Working, text))],
        };
        var capped = s with { Subagents = [.. s.Subagents!.Take(SessionBudget.SubagentsCap).Select(a => a with { Line = null, Task = null })], SubagentsMore = 12 };

        var fit = SessionBudget.Fit(s, ProtocolJson.ByteCount(capped) + 16);

        Assert.Equal(SessionBudget.SubagentsCap, fit.Subagents!.Count);
        Assert.Equal(12, fit.SubagentsMore);
        Assert.All(fit.Subagents, a => Assert.Equal(((string?)null, (string?)null, true), (a.Line, a.Task, a.CurrentTool is not null)));
    }

    // What an older client, or the HA connector, reads must not lose anything to fields it
    // ignores: each case is trimmed exactly as the same session without sub-agent fields.
    [Fact]
    public void Sub_agent_fields_are_trimmed_away_before_anything_an_older_client_reads()
    {
        var history = Enumerable.Range(0, 50).Select(i => new ToolCall { Id = $"t{i}", Name = "powershell", Summary = new string('s', 190), Status = ToolStatus.Done, StartedAt = DateTimeOffset.UnixEpoch }).ToList();
        Session Case(string response, string reasoning, IReadOnlyList<ToolCall>? tools) => Big("additive", response, tools) with
        {
            Title = "A session near the budget",
            Activity = Big("x", response, tools).Activity with
            {
                Reasoning = reasoning,
                UserMessage = new string('u', 2000),
                Source = new ActivitySource { Response = "agent-01", Reasoning = "agent-01" },
                Own = new OwnActivity { Response = new string('m', 12 * 1024), Reasoning = new string('n', 3 * 1024), At = DateTimeOffset.UnixEpoch },
            },
            Subagents = [.. Enumerable.Range(0, 8).Select(i => Delegate(i, SubagentStatus.Working, new string('d', 512)))],
            WaitingOn = "agent-01",
        };
        // The delegate's report as the latest text: before sub-agent fields, this was sent whole.
        Session[] cases =
        [
            Case(new string('r', Limits.Response), new string('q', Limits.Reasoning), null),
            Case(new string('r', Limits.Response), new string('q', Limits.Reasoning), history),
            Case(string.Concat(Enumerable.Repeat("😀", 4096)), new string('q', Limits.Reasoning), history),
        ];

        foreach (var s in cases)
        {
            Assert.True(ProtocolJson.ByteCount(s) > SessionBudget.MaxSessionBytes);
            var fit = SessionBudget.Fit(s);
            var older = SessionBudget.Fit(Older(s));

            Assert.True(ProtocolJson.ByteCount(fit) <= SessionBudget.MaxSessionBytes);
            Assert.Equal(JsonSerializer.Serialize(older, ProtocolJson.Options), JsonSerializer.Serialize(Older(fit), ProtocolJson.Options));
        }
        Assert.Equal(cases[0].Activity.Response, SessionBudget.Fit(cases[0]).Activity.Response);
    }

    static Session Older(Session s) => s with
    {
        Subagents = null,
        SubagentsMore = null,
        WaitingOn = null,
        Activity = s.Activity with { Source = null, Own = null },
        Question = s.Question is { } q ? q with { Agent = null } : null,
    };

    [Fact]
    public void Finished_delegates_then_lines_then_tasks_go_before_any_of_the_sessions_own_text()
    {
        var s = Big("order", "the main agent's answer") with
        {
            Activity = Big("x", "the main agent's answer").Activity with { Reasoning = "thinking" },
            Subagents = [.. Enumerable.Range(0, 10).Select(i => Delegate(i, i < 5 ? SubagentStatus.Working : SubagentStatus.Idle, new string('t', 2000)))],
        };
        int Size(Session x) => ProtocolJson.ByteCount(x);
        var withoutFinished = s with { Subagents = [.. s.Subagents!.Take(5)] };
        var withoutLines = withoutFinished with { Subagents = [.. withoutFinished.Subagents!.Select(a => a with { Line = null })] };

        var finished = SessionBudget.Fit(s, Size(withoutFinished) + 64);
        Assert.Equal(5, finished.Subagents!.Count);
        Assert.Equal(5, finished.SubagentsMore);
        Assert.NotNull(finished.Subagents[0].Line);

        var lines = SessionBudget.Fit(s, Size(withoutLines) + 64);
        Assert.All(lines.Subagents!, a => Assert.Equal(((string?)null, true), (a.Line, a.Task is not null)));
        Assert.Equal("thinking", lines.Activity.Reasoning);
        Assert.Equal("the main agent's answer", lines.Activity.Response);
    }

    [Fact]
    public void One_sub_agent_in_full_fits_the_budget_losing_its_oldest_tools_first()
    {
        var history = Enumerable.Range(0, 50).Select(i => new ToolCall { Id = $"t{i}", Name = "powershell", Summary = new string('s', 190), Status = ToolStatus.Done, StartedAt = DateTimeOffset.UnixEpoch }).ToList();
        var a = Delegate(1, SubagentStatus.Idle, "done") with
        {
            Response = new string('r', Limits.Response),
            Reasoning = Limits.KeepEnd(string.Concat(Enumerable.Repeat("😀", 1024)), Limits.Reasoning).Text,
            History = history,
        };
        Assert.True(ProtocolJson.ByteCount(a) > SessionBudget.MaxSessionBytes);

        var fit = SessionBudget.Fit(a);

        Assert.True(ProtocolJson.ByteCount(fit) <= SessionBudget.MaxSessionBytes);
        Assert.Equal("t49", fit.History!.Last().Id);
        Assert.True(fit.History.Count < 50);
        Assert.Equal((a.Response, a.Reasoning), (fit.Response, fit.Reasoning));
    }
}
