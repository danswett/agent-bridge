using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class ProbeR2O147
{
    [Theory]
    [InlineData("{\"Version\":3,\"Messages\":[],\"Retired\":[]}")]
    [InlineData("{\"Version\":2,\"Messages\":[{\"Node\":\"n\",\"Rid\":\"r\",\"Sender\":\"s\",\"Payload\":\"\",\"QueuedAt\":\"2026-01-01T00:00:00Z\",\"DeliveredAt\":null,\"Outcome\":\"bogus\"}],\"Retired\":[]}")]
    public async Task Parseable_but_invalid_snapshot_is_isolated(string json)
    {
        var dir = Directory.CreateTempSubdirectory("probe-").FullName;
        try
        {
            var p = Path.Combine(dir, "r.inbox");
            File.WriteAllText(p, json);
            var inbox = new RelayReplyInbox(p, (_, _) => Task.FromResult(new ReplyResult(AnswerOutcome.Delivered)));
            await inbox.RecoverAsync(["session"]);
            var rid = Guid.NewGuid().ToString("D");
            var r = await inbox.ReceiveAsync("session", rid,
                new(new ReplyRequest { Id = "session", Request = rid, Text = "x" }, "peer", "acct"));
            Assert.Equal(AnswerOutcome.Refused, r.Outcome);
            Assert.Null(r.ReplyAck);
        }
        finally { Directory.Delete(dir, true); }
    }
}
