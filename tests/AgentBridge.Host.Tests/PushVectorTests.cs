using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

// The push.subscribe vectors (docs/protocol/vectors/push.json) against the host's own check, which
// lives here rather than in AgentBridge.Protocol: a valid vector must be one a host takes.
public sealed class PushVectorTests
{
    [Fact]
    public void A_valid_push_subscription_vector_is_one_the_host_takes()
    {
        var vectors = VectorFiles.Read("push.json")["records"]!["PushSubscribeRequest"]!["valid"]!.AsArray();

        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            var request = v!["json"].Deserialize<PushSubscribeRequest>(ProtocolJson.Options)!;
            Assert.True(PushNotifier.Problem(request) is null, $"{v["name"]}: {PushNotifier.Problem(request)}");
        }
    }
}
