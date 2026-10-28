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
        Assert.Equal(3, list.Sessions.Count);
        Assert.Equal(SessionStatus.Waiting, list.Sessions[0].Status);
        Assert.Equal(FieldType.Multi, list.Sessions[0].Question!.Fields[1].Type);
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
    public void Long_text_keeps_its_end(string text, int max, string expected, bool truncated)
    {
        Assert.Equal((expected, truncated), Limits.KeepEnd(text, max));
    }

    [Fact]
    public void Cutting_text_never_splits_a_surrogate_pair()
    {
        var text = "ab😀cd"; // the emoji is two UTF-16 units at 2..3
        var (end, _) = Limits.KeepEnd(text, 3); // would start on the low surrogate
        Assert.Equal("cd", end);
        Assert.Equal("ab", Limits.KeepStart(text, 3)); // would end on the high surrogate
    }
}
