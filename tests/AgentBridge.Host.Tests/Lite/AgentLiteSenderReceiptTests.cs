using System.Text.Json;
using AgentBridge.Host.Lite;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #3875 slice 2: the sender keeps a small record of each unconfirmed send and asks the target host what its first attempt became.
public partial class AgentLiteSenderTests
{
    // The receiver answers like the real one, but the first acknowledgement is lost on the way back (the send ends unconfirmed).
    static LiteReceiver LostAck(Rig r, LiteDelivery? result = null, bool reaches = true)
    {
        r.Deliver = new Deliver(result ?? new LiteDelivery(true, null));
        var receiver = new LiteReceiver(new Mode(), r.Deliver);
        var channel = new LiteChannel(Self, "host", OwnStatic, Other, Account, true);
        var calls = 0;
        r.Link.Reply = async (req, ct) =>
        {
            if (++calls == 1)
            {
                if (reaches) { await receiver.ReceiveAsync(channel, JsonSerializer.SerializeToElement(req), ct); }
                throw new TimeoutException();
            }
            return await receiver.ReceiveAsync(channel, JsonSerializer.SerializeToElement(req), ct);
        };
        return receiver;
    }

    static async Task<(AgentLiteSender Sender, string Request)> Unconfirmed(Rig r, TimeProvider? time = null)
    {
        var sender = r.Sender(time);
        var outcome = await sender.SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hello"), default);
        Assert.Equal(LiteKind.Unconfirmed, outcome.Kind);
        return (sender, outcome.Request);
    }

    [Fact]
    public async Task A_lookup_after_a_lost_acknowledgement_says_what_the_receiver_did_and_types_nothing_more()
    {
        using var r = new Rig(); LostAck(r);
        var (sender, request) = await Unconfirmed(r);
        var reply = await sender.ReceiptAsync(Caller, request, default);
        Assert.Equal(new LiteReceiptReply("delivered", null, request), reply);
        var asked = JsonSerializer.SerializeToElement(r.Link.Request!);
        Assert.Equal("", asked.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_lookup_reports_the_first_attempts_own_refusal_and_that_is_the_only_refused_it_can_return()
    {
        using var r = new Rig(); LostAck(r, new LiteDelivery(false, "busy"));
        var (sender, request) = await Unconfirmed(r);
        Assert.Equal(new LiteReceiptReply("refused", "busy", request), await sender.ReceiptAsync(Caller, request, default));
    }

    [Fact]
    public async Task A_lookup_of_a_send_that_never_reached_the_receiver_says_unknown_without_a_lookup_failure_code()
    {
        using var r = new Rig(); LostAck(r, reaches: false);
        var (sender, request) = await Unconfirmed(r);
        Assert.Equal(new LiteReceiptReply("unknown", null, request), await sender.ReceiptAsync(Caller, request, default));
    }

    [Fact]
    public async Task A_request_this_credential_did_not_leave_unconfirmed_is_refused_locally_and_sent_nowhere()
    {
        using var r = new Rig(); LostAck(r);
        var (sender, request) = await Unconfirmed(r);
        var opened = r.Link.Opened;
        Assert.Null(await sender.ReceiptAsync(Caller, Guid.NewGuid().ToString("D"), default));
        Assert.Null(await sender.ReceiptAsync(Caller with { Id = "cred-2" }, request, default));
        Assert.Equal(opened, r.Link.Opened);
    }

    public static IEnumerable<object[]> LookupFailures() =>
    [
        ["timeout", "lookup: " + AgentLiteSender.TimedOut],
        ["offline", "lookup: offline"],
        ["channel", "lookup: " + AgentLiteSender.ChannelLost],
        ["rate", "lookup: refused:rate"],
        ["lite-action", "lookup: refused:lite-action"],
        ["weird", "lookup: " + AgentLiteSender.BadAck],
        ["wrong-request", "lookup: " + AgentLiteSender.BadAck],
    ];

    [Theory, MemberData(nameof(LookupFailures))]
    public async Task Anything_that_goes_wrong_with_the_lookup_itself_is_unknown_never_refused_so_the_send_stays_uncertain(string failure, string code)
    {
        using var r = new Rig(); LostAck(r);
        var (sender, request) = await Unconfirmed(r);
        r.Link.Reply = (_, _) => failure switch
        {
            "timeout" => throw new TimeoutException(),
            "offline" => throw new LitePeerOfflineException("gone"),
            "channel" => throw new InvalidOperationException(),
            "rate" => Task.FromResult(new LiteAck("refused", "rate", request, 0)),
            "lite-action" => Task.FromResult(new LiteAck("refused", "lite-action", request, 0)),
            "weird" => Task.FromResult(new LiteAck("weird", null, request, 0)),
            _ => Task.FromResult(new LiteAck("receipt", "delivered", Guid.NewGuid().ToString("D"), 0)),
        };
        Assert.Equal(new LiteReceiptReply("unknown", code, request), await sender.ReceiptAsync(Caller, request, default));
    }

    [Fact]
    public async Task A_lookup_with_messaging_off_or_a_lapsed_credential_is_unknown_and_sends_nothing()
    {
        using var r = new Rig(); LostAck(r);
        var (sender, request) = await Unconfirmed(r);
        var opened = r.Link.Opened;
        r.Mode.On = false;
        Assert.Equal("unknown", (await sender.ReceiptAsync(Caller, request, default))!.Outcome);
        r.Mode.On = true; r.Live = false;
        Assert.Equal("unknown", (await sender.ReceiptAsync(Caller, request, default))!.Outcome);
        Assert.Equal(opened, r.Link.Opened);
    }

    [Fact]
    public async Task The_request_record_lasts_as_long_as_the_original_envelope_and_no_longer()
    {
        using var r = new Rig(); LostAck(r);
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var (sender, request) = await Unconfirmed(r, time);
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.NotNull(await sender.ReceiptAsync(Caller, request, default));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await sender.ReceiptAsync(Caller, request, default));
    }

    [Fact]
    public async Task One_credential_keeps_at_most_sixty_four_records_and_the_oldest_goes_first()
    {
        using var r = new Rig();
        r.Link.Reply = (_, _) => throw new TimeoutException();
        var sender = r.Sender();
        var requests = new List<string>();
        for (var i = 0; i < AgentLiteSender.MaxReceiptRecords + 1; i++)
        {
            requests.Add((await sender.SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hello " + i), default)).Request);
        }
        Assert.Null(await sender.ReceiptAsync(Caller, requests[0], default));
        Assert.NotNull(await sender.ReceiptAsync(Caller, requests[^1], default));
        Assert.NotNull(await sender.ReceiptAsync(Caller, requests[1], default));
    }
}
