using AgentBridge.Protocol;
using System.Text.Json;
using Xunit;

// push.subscribe grew an optional notify member (per-device notification filters, docs/protocol/v1-sessions.md
// "Notification filters"). A host that predates it must keep taking the same request and simply ignore
// the member, so a new client can register with an old host. The old host's own record is written out here.
public sealed class PushNotifyCompatTests
{
    sealed record OldPushSubscribeRequest
    {
        public required string Endpoint { get; init; }
        public required PushKeys Keys { get; init; }
        public required string App { get; init; }
        public string? Device { get; init; }
    }

    [Fact]
    public void The_protocol_options_skip_members_a_record_does_not_know()
    {
        Assert.NotEqual(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, ProtocolJson.Options.UnmappedMemberHandling);
    }

    [Fact]
    public void A_host_without_notify_reads_every_push_subscribe_vector_and_ignores_the_filters()
    {
        var vectors = VectorFiles.Read("push.json")["records"]!["PushSubscribeRequest"]!["valid"]!.AsArray();
        Assert.Contains(vectors, v => v!["json"]!.AsObject().ContainsKey("notify"));
        foreach (var v in vectors)
        {
            var old = v!["json"].Deserialize<OldPushSubscribeRequest>(ProtocolJson.Options)!;
            var current = v["json"].Deserialize<PushSubscribeRequest>(ProtocolJson.Options)!;
            Assert.Equal(current.Endpoint, old.Endpoint);
            Assert.Equal(current.Keys, old.Keys);
            Assert.Equal(current.App, old.App);
            Assert.Equal(current.Device, old.Device);
        }
    }

    [Fact]
    public void Notify_is_optional_and_its_defaults_are_questions_and_pinned_on_the_rest_off()
    {
        var n = new PushNotify();
        Assert.Equal((true, true, false, false), (n.Questions, n.Pinned, n.All, n.DeliveryFailures));
        Assert.Empty(n.PinnedSessions);
        var bare = JsonSerializer.Deserialize<PushNotify>("{}", ProtocolJson.Options)!;
        Assert.Equal((true, true, false, false), (bare.Questions, bare.Pinned, bare.All, bare.DeliveryFailures));
    }
}
