using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host;
using AgentBridge.Host.Lite;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Codec = AgentBridge.Roster.Codec;
using Xunit;

[Trait("Category", "Network")]
public sealed class RelayReplyAckTests
{
    const string Session = "11111111-1111-4111-8111-111111111111";
    sealed class Mode : ILiteMode
    {
        public bool On => true;
        public long KillEpoch => 0;
        public void TurnOff(string why) => throw new NotSupportedException();
        public void RaiseEpoch(long epoch) => throw new NotSupportedException();
    }
    sealed class Pins(LitePeer peer) : ILitePeers
    {
        public IReadOnlyList<LitePeer> All() => [peer];
        public bool TryFind(string id, out LitePeer found) { found = peer; return id == peer.DeviceId; }
    }
    sealed class Terminal : ISessionReplier, ILiteDeliver
    {
        public bool Available => true;
        public AnswerOutcome Outcome = AnswerOutcome.Delivered;
        public int Calls;
        public Action? BeforeTyping;
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        {
            BeforeTyping?.Invoke();
            Calls++;
            await Release.Task.WaitAsync(cancellationToken);
            return new(Outcome);
        }
        public async Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) =>
            new((await ReplyAsync(new ReplyRequest { Id = Session, Request = envelope.Body.Request, Text = text }, cancellationToken: ct)).Outcome == AnswerOutcome.Delivered, null);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Durable_relay_results_ack_only_rid_and_reack_duplicates_while_rollback_stays_direct(bool lite, bool durable)
        => await Check(lite, durable, failedWrite: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_relay_append_emits_no_ack_and_types_nothing(bool lite)
        => await Check(lite, durable: true, failedWrite: true);

    [Theory]
    [InlineData(false, "capacityRefused")]
    [InlineData(true, "capacityRefused")]
    [InlineData(false, "retired")]
    [InlineData(true, "retired")]
    public async Task A_capacity_or_retirement_refusal_has_no_admission_receipt_on_either_route(bool lite, string refusal)
        => await Check(lite, durable: true, failedWrite: false, refusal: refusal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_occupied_source_returns_a_prompt_queued_receipt_without_dispatch(bool lite)
        => await Check(lite, durable: true, failedWrite: false, occupied: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_work_that_is_later_refused_still_gets_its_receipt_and_duplicate_reack(bool lite)
        => await Check(lite, durable: true, failedWrite: false, deliveryRefused: true);

    static async Task Check(bool lite, bool durable, bool failedWrite, string? refusal = null, bool occupied = false, bool deliveryRefused = false)
    {
        var folder = Directory.CreateTempSubdirectory("relay-ack-").FullName;
        var terminal = new Terminal { Outcome = deliveryRefused ? AnswerOutcome.Refused : AnswerOutcome.Delivered };
        try
        {
            await using var relay = await TestRelay.StartAsync();
            var token = relay.TokenFor(1, "fixture");
            var host = await relay.ConnectAsync(token, "fixture", "host");
            var sender = await relay.ConnectAsync(token, "fixture", "host");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var point = key.ExportParameters(false).Q;
            var receiver = new LiteReceiver(new Mode(), new Pins(new LitePeer(sender.Id, [0x04, .. point.X!, .. point.Y!], "fixture")), terminal);
            var source = new FleetPublisherTests.Source { Sessions = [new Session
            {
                Id = Session, Agent = AgentKind.Copilot, Status = occupied ? SessionStatus.Working : SessionStatus.Idle,
                StatusSince = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Activity = new(),
            }] };
            var logged = new List<string>();
            var service = new SessionService(source, new MachineInfo(host.Id, "fixture", "test", "0"), logged.Add, replies: terminal) { LiteReceiver = receiver };
            var path = Path.Combine(folder, "replies.inbox");
            if (durable) { service.ConfigureRelayInbox(path, (_, _) => true, _ => true); }
            if (failedWrite) { File.WriteAllText(path, "invalid snapshot"); }
            if (refusal is not null)
            {
                var now = DateTimeOffset.UtcNow;
                var node = AgentBridge.HomeAssistant.HaContract.NodeId(Session);
                var messages = Enumerable.Range(0, refusal == "retired" ? 0 : 100).Select(_ => new
                {
                    Node = node, Rid = Guid.NewGuid().ToString("N"), Sender = sender.Id, Payload = "",
                    QueuedAt = now, DeliveredAt = now, StartedAt = now, Attempt = 1, Outcome = "unconfirmed",
                });
                var retired = refusal == "retired" ? new object[] { new { Node = node, At = now, Resolution = 0 } } : [];
                File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 2, Messages = messages, Retired = retired }));
            }
            terminal.BeforeTyping = () => { if (durable) { Assert.Contains("private reply", File.ReadAllText(path)); } };
            host.Hub.JsonOptions = ProtocolJson.Options;
            host.Hub.Handler = service.HandleAsync;
            await Task.WhenAll(sender.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10)),
                host.Relay.WaitForPeerAsync(p => p.Id == sender.Id, TimeSpan.FromSeconds(10)));
            var channel = await sender.Hub.OpenAsync(host.Id);
            const string rid = "22222222-2222-4222-8222-222222222222";
            object request = new { id = Session, request = rid, text = "private reply" };
            if (lite)
            {
                var from = new LiteSender(sender.Relay.AccountId, sender.Id, Codec.Base64Url(SHA256.HashData(sender.Key.Public)),
                    "fixture", Codec.Base64Url(new byte[12]), Codec.Base64Url(new byte[16]), "agent", "fixture");
                var envelope = LiteProfile.Seal(from, new LiteTarget(host.Id, Session, Codec.Base64Url(new byte[12])), rid,
                    Encoding.UTF8.GetBytes("private reply"), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new EcdsaEnvelopeSigner(key));
                request = new { envelope = Codec.Base64Url(envelope), text = "private reply", killEpoch = 0 };
            }
            terminal.Release.TrySetResult();
            var first = await channel.RequestAsync(lite ? LiteReceiver.Op : Ops.SessionReply, request).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(first.GetProperty("ok").GetBoolean());
            if (!failedWrite && refusal is null)
            {
                Receipt(first.GetProperty("result"), durable ? "present" : "absent");
                if (occupied)
                {
                    if (lite) { Assert.Equal("busy", first.GetProperty("result").GetProperty("code").GetString()); }
                    else { Assert.Equal(AnswerOutcome.Busy, first.GetProperty("result").Deserialize<ReplyResult>(ProtocolJson.Options)!.Outcome); }
                }
                var duplicate = await channel.RequestAsync(lite ? LiteReceiver.Op : Ops.SessionReply, request);
                Assert.Equal(occupied ? 0 : durable || lite ? 1 : 2, terminal.Calls);
                Receipt(duplicate.GetProperty("result"), durable ? "duplicate" : "absent");
            }
            else
            {
                Assert.Equal(0, terminal.Calls);
                Receipt(first.GetProperty("result"), refusal ?? "absent");
                if (lite) { Assert.Equal("refused", first.GetProperty("result").GetProperty("status").GetString()); }
                else { Assert.Equal(AnswerOutcome.Refused, first.GetProperty("result").Deserialize<ReplyResult>(ProtocolJson.Options)!.Outcome); }
                if (refusal is not null)
                {
                    Receipt((await channel.RequestAsync(lite ? LiteReceiver.Op : Ops.SessionReply, request)).GetProperty("result"), refusal);
                    using var saved = JsonDocument.Parse(File.ReadAllText(path));
                    var entries = saved.RootElement.GetProperty("Messages").EnumerateArray().Where(m => m.GetProperty("Rid").GetString() == Guid.Parse(rid).ToString("N")).ToArray();
                    if (refusal == "retired") { Assert.Empty(entries); }
                    else { Assert.True(Assert.Single(entries).GetProperty("CapacityRefused").GetBoolean()); Assert.Equal("", entries[0].GetProperty("Payload").GetString()); }
                }
            }
            Assert.False(channel.Events.TryRead(out _));
            Assert.DoesNotContain(logged, line => line.Contains("private reply", StringComparison.Ordinal));
        }
        finally { terminal.Release.TrySetResult(); Directory.Delete(folder, true); }
    }

    static void Receipt(JsonElement result, string vector)
    {
        var actual = new JsonObject();
        if (result.TryGetProperty("reply_ack", out var ack)) { actual["reply_ack"] = JsonNode.Parse(ack.GetRawText()); }
        Assert.True(JsonNode.DeepEquals(VectorFiles.Read("reply-ack.json")[vector], actual), actual.ToJsonString());
    }
}
