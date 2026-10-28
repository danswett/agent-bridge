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

        Assert.True(pages.Count > 1);
        Assert.Equal(sessions.Select(s => s.Id), pages.SelectMany(p => p).Select(s => s.Id));
        foreach (var p in pages)
        {
            var bytes = ProtocolJson.ByteCount(new { re = int.MaxValue, ok = true, result = new SessionList(p, long.MaxValue, new string('c', 64)) });
            Assert.True(bytes <= ProtocolJson.MaxMessageBytes, $"{bytes} bytes");
        }
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
}
