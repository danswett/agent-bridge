using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

// hello's machine.restore (#780; v1-sessions.md, What went).
public sealed class RestoreProtocolTests
{
    [Fact]
    public void A_cause_or_a_reason_a_client_does_not_know_reads_as_the_one_that_promises_least()
    {
        const string json = """
            { "id": "h", "name": "DEV", "os": "windows", "hostVersion": "0.9.0",
              "restore": { "at": "2026-11-04T16:02:11.000Z", "cause": "meteor", "why": "later", "waiting": 1,
                "offered": [ { "id": "s1", "agent": "copilot", "midTurn": true } ] } }
            """;

        var restore = JsonSerializer.Deserialize<MachineInfo>(json, ProtocolJson.Options)!.Restore!;

        Assert.Equal((RestoreCause.Crash, (RestoreWhy?)RestoreWhy.Pending, 1), (restore.Cause, restore.Why, restore.Waiting));
        Assert.Equal(("s1", AgentKind.Copilot, (bool?)true, (bool?)null), (restore.Offered[0].Id, restore.Offered[0].Agent, restore.Offered[0].MidTurn, restore.Offered[0].QuestionLost));
    }

    [Fact]
    public void A_run_outcome_or_a_session_outcome_a_client_does_not_know_reads_as_the_one_that_says_something_may_have_started()
    {
        const string json = """
            { "outcome": "later", "sessions": [ { "id": "s1", "outcome": "teleported", "reason": "x" } ] }
            """;

        var result = JsonSerializer.Deserialize<RestoreRunResult>(json, ProtocolJson.Options)!;

        Assert.Equal((RestoreRunOutcome.Partial, LaunchOutcome.Unconfirmed), (result.Outcome, result.Sessions[0].Outcome));
    }

    [Fact]
    public void A_run_result_whose_session_entry_has_no_id_is_refused_whole()
    {
        Assert.Throws<MalformedRecordException>(() => Received.Read<RestoreRunSession>(JsonDocument.Parse("""{"outcome":"started"}""").RootElement));
    }

    [Fact]
    public void A_run_request_keeps_its_offer_and_the_ids_in_order()
    {
        const string json = """
            { "request": "6f0d2c1e-8a4b-4c3d-9e2f-1a2b3c4d5e6f", "offer": "2026-11-04T16:02:11.000Z", "sessions": ["b", "a"], "progress": true }
            """;

        var request = JsonSerializer.Deserialize<RestoreRunRequest>(json, ProtocolJson.Options)!;

        Assert.Equal((new DateTimeOffset(2026, 11, 4, 16, 2, 11, TimeSpan.Zero), "b,a", (bool?)true), (request.Offer, string.Join(",", request.Sessions), request.Progress));
    }

    [Fact]
    public void An_offered_session_says_when_it_died_again_too_soon()
    {
        var s = JsonSerializer.Deserialize<RestoreSession>("""{"id":"s1","agent":"copilot","loop":true}""", ProtocolJson.Options)!;

        Assert.True(s.Loop);
    }
}
