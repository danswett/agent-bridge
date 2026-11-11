using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

public class AgentLiteSenderTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "lite-send-" + Guid.NewGuid().ToString("N"));
    bool on = true;
    readonly FakeLink link = new();

    public AgentLiteSenderTests() { Directory.CreateDirectory(folder); AgentSignKey.OpenOrCreate(folder, true).Dispose(); }
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    static AgentCredential Credential(string scope = AgentProtocol.ScopeReadReply) =>
        new("cred-1", "sess-1", "launch-1", 42, DateTimeOffset.UnixEpoch, scope, 0, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    static AgentLiteSendRequest Request(string text = "hello") => new() { Machine = new string('2', 32), Session = "target-1", Instance = Convert.ToBase64String(new byte[12]).Replace('+', '-').Replace('/', '_'), Text = text };

    AgentLiteSender Sender() => new(folder, new string('1', 32), () => on, () => link) { AckTimeout = TimeSpan.FromMilliseconds(400), ModePoll = TimeSpan.FromMilliseconds(20) };

    [Fact]
    public async Task Off_returns_off_and_opens_no_channel()
    {
        on = false;
        var r = await Sender().SendAsync(Credential(), Request());
        Assert.Equal(AgentLite.Off, r.Outcome);
        Assert.Equal(0, link.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AgentProtocol.ScopeRead)]
    [InlineData(AgentProtocol.ScopeNone)]
    public async Task A_missing_or_read_only_credential_is_refused_before_anything_is_sent(string? scope)
    {
        var r = await Sender().SendAsync(scope is null ? null : Credential(scope), Request());
        Assert.Equal(AgentLite.Refused, r.Outcome);
        Assert.Equal(0, link.Requests);
    }

    [Theory]
    [InlineData("")]
    public async Task Text_outside_the_reply_limits_is_refused(string text)
    {
        Assert.Equal(AgentLite.Refused, (await Sender().SendAsync(Credential(), Request(text))).Outcome);
        Assert.Equal(AgentLite.Refused, (await Sender().SendAsync(Credential(), Request(new string('x', AgentLite.MaxTextChars + 1)))).Outcome);
        Assert.Equal(0, link.Requests);
    }

    [Fact]
    public async Task A_peer_not_in_presence_is_undeliverable_and_nothing_is_sent()
    {
        link.Present = false;
        var r = await Sender().SendAsync(Credential(), Request());
        Assert.Equal(AgentLite.Undeliverable, r.Outcome);
        Assert.Equal(AgentLiteSender.OfflineText, r.Detail);
        Assert.Equal(0, link.Requests);
    }

    [Fact]
    public async Task A_dropped_channel_is_undeliverable()
    {
        link.Reply = (_, _) => throw new IOException("peer is not connected");
        Assert.Equal(AgentLite.Undeliverable, (await Sender().SendAsync(Credential(), Request())).Outcome);
    }

    [Fact]
    public async Task No_acknowledgement_in_time_is_unconfirmed_never_delivered()
    {
        link.Reply = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return default; };
        Assert.Equal(AgentLite.Unconfirmed, (await Sender().SendAsync(Credential(), Request())).Outcome);
    }

    [Fact]
    public async Task A_timeout_from_the_channel_is_unconfirmed()
    {
        link.Reply = (_, _) => throw new TimeoutException();
        Assert.Equal(AgentLite.Unconfirmed, (await Sender().SendAsync(Credential(), Request())).Outcome);
    }

    [Fact]
    public async Task Delivered_carries_a_fresh_request_id_each_attempt_and_binds_the_text()
    {
        var ids = new List<string>();
        link.Reply = (req, _) =>
        {
            ids.Add(req.Request);
            var body = AgentBridge.AgentEnvelope.Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(req.Delivery.Envelope)).Body;
            Assert.True(body.BindsData(AgentLite.DataOf("hello")));
            Assert.False(body.BindsData(AgentLite.DataOf("hellp")));
            return Task.FromResult(Ack(req.Request, AgentLite.Delivered));
        };
        var sender = Sender();
        Assert.Equal(AgentLite.Delivered, (await sender.SendAsync(Credential(), Request())).Outcome);
        Assert.Equal(AgentLite.Delivered, (await sender.SendAsync(Credential(), Request())).Outcome);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal("hello", link.Sent[0].Text);
    }

    [Fact]
    public async Task The_receivers_refusal_code_is_passed_through()
    {
        link.Reply = (req, _) => Task.FromResult(Ack(req.Request, AgentLite.Refused, "lite-account"));
        var r = await Sender().SendAsync(Credential(), Request());
        Assert.Equal((AgentLite.Refused, "lite-account"), (r.Outcome, r.Code));
    }

    [Fact]
    public async Task An_acknowledgement_for_another_request_is_unconfirmed()
    {
        link.Reply = (_, _) => Task.FromResult(Ack("someone-else", AgentLite.Delivered));
        Assert.Equal(AgentLite.Unconfirmed, (await Sender().SendAsync(Credential(), Request())).Outcome);
    }

    [Fact]
    public async Task Switching_off_mid_send_resolves_unconfirmed()
    {
        var gate = new TaskCompletionSource<JsonElement>();
        link.Reply = (_, _) => { on = false; return gate.Task; };
        var r = await Sender().SendAsync(Credential(), Request());
        Assert.Equal(AgentLite.Unconfirmed, r.Outcome);
        Assert.Equal("mode switched off", r.Detail);
    }

    static JsonElement Ack(string request, string outcome, string? code = null) =>
        JsonSerializer.SerializeToElement(new { ok = true, result = new AgentLiteAck(request, outcome, code) }, ProtocolJson.Options);

    sealed class FakeLink : ILiteLink
    {
        public int Requests;
        public bool Present = true;
        public List<AgentLiteDelivery> Sent = [];
        public Func<(string Request, AgentLiteDelivery Delivery), CancellationToken, Task<JsonElement>>? Reply;
        public string Account => new string('a', 32);
        public bool PeerPresent(string peer) => Present;

        public async Task<JsonElement> RequestAsync(string peer, string op, object data, TimeSpan timeout, CancellationToken ct)
        {
            Requests++;
            Assert.Equal(AgentLite.Send, op);
            var delivery = (AgentLiteDelivery)data;
            Sent.Add(delivery);
            var id = AgentBridge.AgentEnvelope.Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(delivery.Envelope)).Body.Request;
            return await Reply!((id, delivery), ct);
        }
    }
}
