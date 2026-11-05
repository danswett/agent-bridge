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
}
