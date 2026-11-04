using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

// session.patch (v1-sessions.md, Patches): a patch applied to the session a client holds gives
// exactly the session a whole upsert of the same moment would, as the client reads it.
public class SessionPatchTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 4, 22, 43, 182, TimeSpan.Zero);

    static Subagent Delegate(int i, SubagentStatus status = SubagentStatus.Working) => new()
    {
        Id = $"agent-{i}", Kind = SubagentKind.Background, Name = $"Delegate {i}", Task = new string('t', 400) + i, Model = "claude-haiku-5.5",
        Status = status, StartedAt = T0.AddSeconds(i), Line = $"working on part {i}",
        CurrentTool = status == SubagentStatus.Working ? Tool($"tool-{i}") : null, Shells = i % 2,
    };

    static ToolCall Tool(string id, string summary = "Run the tests") => new() { Id = id, Name = "powershell", Summary = summary, Status = ToolStatus.Running, StartedAt = T0 };

    static Session Base() => new()
    {
        Id = "4f0c2a1e-9d3b-4e6f-8a7c-5b1d2e3f4a5b", Agent = AgentKind.Copilot, Title = "Patches", Cwd = @"C:\src\agent-bridge", Repository = "danswett/agent-bridge",
        Branch = "perf/session-patch", Model = "claude-opus-5.5", Status = SessionStatus.Working, StatusSince = T0, StartedAt = T0, UpdatedAt = T0, ProcessId = 4120,
        Activity = new Activity { Response = "Reading the spec.", Reasoning = "Start with the framing.", CurrentTool = Tool("call-1"), UserMessage = "Build session.patch", TurnCount = 3 },
        Subagents = [.. Enumerable.Range(0, 20).Select(i => Delegate(i))],
    };

    // What a client makes of the JSON, written back: the comparison every test here makes.
    static string Read(JsonObject json) => JsonSerializer.Serialize(Received.Read<Session>(JsonSerializer.SerializeToElement(json)), ProtocolJson.Options);

    // As a client receives it: the patch on the wire, read as a host record is.
    static SessionPatch Wire(SessionPatch patch) => Received.Read<SessionPatch>(JsonSerializer.SerializeToElement(patch, ProtocolJson.Options));

    static JsonObject Applied(Session start, Session to, out SessionPatch patch)
    {
        var held = SessionPatches.Json(start);
        patch = SessionPatches.Diff(held, 41, SessionPatches.Json(to)) ?? throw new InvalidOperationException("nothing changed");
        return SessionPatches.Apply(held, 41, Wire(patch));
    }

    [Fact]
    public void A_change_to_one_delegate_of_twenty_sends_that_entry_alone_by_id()
    {
        var start = Base();
        var to = start with
        {
            UpdatedAt = T0.AddSeconds(5),
            Subagents = [.. start.Subagents!.Select(a => a.Id == "agent-7" ? a with { Status = SubagentStatus.Ended, Outcome = SubagentOutcome.Done, EndedAt = T0.AddSeconds(5), CurrentTool = null } : a)],
        };

        var applied = Applied(start, to, out var patch);

        Assert.Equal(Read(SessionPatches.Json(to)), Read(applied));
        var entry = Assert.Single(patch.Subagents!.Set!);
        Assert.Equal("agent-7", entry.GetProperty("id").GetString());
        Assert.False(entry.TryGetProperty("task", out _));
        Assert.Null(patch.Subagents.Order);
        Assert.True(ProtocolJson.ByteCount(patch) * 20 < ProtocolJson.ByteCount(to), $"a {ProtocolJson.ByteCount(patch)}-byte patch of a {ProtocolJson.ByteCount(to)}-byte session");
    }

    [Fact]
    public void Delegates_removed_added_and_reordered_arrive_in_the_hosts_order()
    {
        var start = Base();
        var list = start.Subagents!.ToList();
        list.RemoveAt(3);
        list.Insert(0, Delegate(30));
        (list[5], list[6]) = (list[6], list[5]);
        var to = start with { Subagents = list };

        var applied = Applied(start, to, out var patch);

        Assert.Equal(Read(SessionPatches.Json(to)), Read(applied));
        Assert.Equal(["agent-3"], patch.Subagents!.Remove!);
        Assert.Equal(list.Select(a => a.Id), patch.Subagents.Order!);
    }

    [Fact]
    public void Text_that_grew_is_appended_and_text_cut_from_the_front_is_set_whole()
    {
        var start = Base();
        var grown = start with { Activity = start.Activity with { Response = start.Activity.Response + " Then the host.", Reasoning = "Framing first." } };

        var applied = Applied(start, grown, out var patch);

        Assert.Equal(Read(SessionPatches.Json(grown)), Read(applied));
        Assert.Equal(" Then the host.", patch.Append!.Value.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal("Framing first.", patch.Set!.Value.GetProperty("activity").GetProperty("reasoning").GetString());
        Assert.False(patch.Set.Value.GetProperty("activity").TryGetProperty("response", out _));
    }

    [Fact]
    public void Fields_that_went_are_removed_and_delegates_all_gone_leave_none()
    {
        var start = Base() with { Question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Which?", AskedAt = T0 }, Status = SessionStatus.Waiting };
        var to = start with { Question = null, Status = SessionStatus.Idle, Subagents = null, Activity = start.Activity with { CurrentTool = null } };

        var applied = Applied(start, to, out _);

        Assert.Equal(Read(SessionPatches.Json(to)), Read(applied));
        Assert.False(applied.ContainsKey("subagents"));
        Assert.False(applied.ContainsKey("question"));
    }

    [Fact]
    public void Nothing_changed_sends_nothing_and_a_patch_no_smaller_than_the_whole_session_is_sent_whole()
    {
        var start = SessionPatches.Json(Base());
        Assert.Null(SessionPatches.Next(start, 41, SessionPatches.Json(Base()), 7));

        var other = Base() with { Title = "Another", Cwd = "/x", Repository = "a/b", Branch = "c", Model = "m", Subagents = [Delegate(99)], Activity = new Activity { Response = "new" } };
        var next = SessionPatches.Next(start, 41, SessionPatches.Json(other), 7)!;
        Assert.Null(next.Base);
        Assert.Equal(7, next.Revision);
        Assert.Equal(Read(SessionPatches.Json(other)), Read(SessionPatches.Apply(null, null, Wire(next))));
    }

    [Theory]
    [InlineData(40L)]
    [InlineData(null)]
    public void A_patch_on_another_event_than_the_one_the_session_is_held_from_is_not_applied(long? heldSeq)
    {
        var start = Base();
        var held = SessionPatches.Json(start);
        var patch = SessionPatches.Diff(held, 41, SessionPatches.Json(start with { Title = "Renamed" }))!;

        var e = Assert.Throws<SessionPatchException>(() => SessionPatches.Apply(heldSeq is null ? null : held, heldSeq, patch));
        Assert.Equal(SessionPatches.NotOnHeld, e.Message);
        Assert.Equal("Patches", held["title"]!.GetValue<string>());
    }

    [Fact]
    public void Appending_to_a_field_that_is_not_text_and_patching_delegates_without_ids_are_not_applied()
    {
        var held = SessionPatches.Json(Base());
        var append = new SessionPatch { Id = held["id"]!.GetValue<string>(), Base = 41, Append = JsonSerializer.SerializeToElement(new { activity = new { turnCount = "4" } }) };
        Assert.Equal(SessionPatches.NotAMerge, Assert.Throws<SessionPatchException>(() => SessionPatches.Apply(held, 41, append)).Message);

        var order = new SessionPatch { Id = append.Id, Base = 41, Subagents = new SubagentsPatch { Order = ["agent-1", "agent-1"] } };
        Assert.Equal(SessionPatches.NotByIds, Assert.Throws<SessionPatchException>(() => SessionPatches.Apply(held, 41, order)).Message);
        var noId = new SessionPatch { Id = append.Id, Base = 41, Subagents = new SubagentsPatch { Set = [JsonSerializer.SerializeToElement(new { status = "ended" })] } };
        Assert.Equal(SessionPatches.NotByIds, Assert.Throws<SessionPatchException>(() => SessionPatches.Apply(held, 41, noId)).Message);
    }

    // Property: any session to any other, by changes of every kind the host makes, through the wire.
    [Fact]
    public void Any_change_of_a_session_patched_over_it_gives_exactly_the_session_upserted()
    {
        var random = new Random(601);
        var now = Base();
        var held = SessionPatches.Json(now);
        long seq = 1;
        for (var step = 0; step < 1000; step++)
        {
            var next = Mutate(now, random, step);
            var json = SessionPatches.Json(next);
            var patch = SessionPatches.Next(held, seq, json, step);
            if (patch is null)
            {
                Assert.True(JsonNode.DeepEquals(json, held));
                continue;
            }
            var applied = SessionPatches.Apply(held, seq, Wire(patch));
            // The JSON itself, which is more than what a client reads of it being the same.
            Assert.True(JsonNode.DeepEquals(json, applied), $"step {step}: {applied.ToJsonString()}");
            (now, held, seq) = (next, applied, seq + random.Next(1, 4));
        }
    }

    static Session Mutate(Session s, Random r, int step)
    {
        var at = T0.AddSeconds(step);
        var subagents = s.Subagents?.ToList() ?? [];
        switch (r.Next(12))
        {
            case 0: return s with { Status = (SessionStatus)r.Next(4), StatusSince = at, UpdatedAt = at };
            case 1: return s with { Activity = s.Activity with { Response = (s.Activity.Response is { Length: < 2000 } r1 ? r1 : "") + $" more {step} \U0001F600 é" }, UpdatedAt = at };
            case 2: return s with { Activity = s.Activity with { Response = r.Next(3) == 0 ? null : $"fresh {step}" } };
            case 3: return s with { Activity = s.Activity with { CurrentTool = r.Next(2) == 0 ? null : Tool($"call-{step}", $"step {step}"), TurnCount = step } };
            case 4: return s with { Question = r.Next(2) == 0 ? null : new Question { Id = $"q{step}", Kind = QuestionKind.Approval, Message = "Run it?", AskedAt = at, Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new("approve"), new("deny")] }] } };
            case 5 when subagents.Count > 0: subagents.RemoveAt(r.Next(subagents.Count)); break;
            case 6: subagents.Insert(r.Next(subagents.Count + 1), Delegate(100 + step)); break;
            case 7 when subagents.Count > 1:
                var i = r.Next(subagents.Count);
                var j = r.Next(subagents.Count);
                (subagents[i], subagents[j]) = (subagents[j], subagents[i]);
                break;
            case 8 when subagents.Count > 0:
                var k = r.Next(subagents.Count);
                subagents[k] = subagents[k] with { Status = SubagentStatus.Idle, Outcome = SubagentOutcome.Failed, EndedAt = at, CurrentTool = null, Line = null, Name = r.Next(2) == 0 ? null : "renamed" };
                break;
            case 9: return s with { Activity = s.Activity with { Source = r.Next(2) == 0 ? null : new ActivitySource { Response = "agent-1" }, Own = r.Next(2) == 0 ? null : new OwnActivity { Response = $"own {step}", At = at } } };
            case 10: return s with { Driver = r.Next(2) == 0 ? null : DriverKind.Agent, DriverPrincipal = null, SubagentsMore = r.Next(3) == 0 ? null : r.Next(5), WaitingOn = r.Next(2) == 0 ? null : "agent-2" };
            default: return s with { Title = r.Next(4) == 0 ? null : $"Title {step}", Activity = s.Activity with { Background = Background.Of(r.Next(3), r.Next(3)) } };
        }
        return s with { Subagents = subagents.Count == 0 ? null : subagents, UpdatedAt = at };
    }
}
