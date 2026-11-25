using AgentBridge.Host;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// Clear and send (#804): the confirmation a person's device signs for the box it was shown. Each refusal is one way an agent,
// a stale press, another reply or another machine could otherwise ride it.
public sealed class ReplyClearConfirmationTests : IDisposable
{
    const string Session = "c0b11070-0000-4000-8000-0000000000aa";
    static readonly string Digest = EscapedApproval.Shown("rerun the tests");
    readonly P256KeyPair machine = P256KeyPair.Generate();
    readonly P256KeyPair phone = P256KeyPair.Generate();
    readonly List<PresenceEntry> present = [];
    List<string> approved;

    public ReplyClearConfirmationTests()
    {
        approved = [Phone];
        present.Add(new PresenceEntry(Phone, "Phone", "pwa", DateTimeOffset.UtcNow));
    }

    public void Dispose() { machine.Dispose(); phone.Dispose(); }

    string Phone => Wire.DeviceId(phone.Public);

    ReplyClearConfirmations Confirmations(string policy = ReplyClear.Confirm)
    {
        var c = new ReplyClearConfirmations(machine, policy, () => approved);
        c.AttachPresence(() => present);
        return c;
    }

    SignedCommand Sign(string request, string action = ReplyClear.Action, string? shown = null, string? session = null,
        byte[]? forMachine = null, TimeSpan? lifetime = null, DateTimeOffset? issued = null)
    {
        var at = issued ?? DateTimeOffset.UtcNow;
        var target = forMachine ?? machine.Public;
        return SignedCommands.Sign(phone, target, new CommandBody
        {
            V = 1, Action = action, Request = request,
            Principal = new CommandPrincipal { Kind = "user", Device = Phone },
            Target = new CommandTarget { Machine = Wire.DeviceId(target), Session = session ?? Session, Shown = shown ?? Digest },
            To = Wire.DeviceId(target),
            IssuedAt = at.ToUnixTimeSeconds(), ExpiresAt = at.Add(lifetime ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
        });
    }

    static ReplyRequest Reply(string request, SignedCommand? clear) => new() { Id = Session, Request = request, Text = "go on", Clear = clear };

    string? Check(ReplyRequest request, string? peer = null, ReplyClearConfirmations? confirmations = null, string? digest = null) =>
        (confirmations ?? Confirmations()).For(peer ?? Phone, request)!(digest ?? Digest);

    [Fact]
    public void A_confirmation_the_approved_phone_signed_for_this_box_and_reply_holds()
    {
        var request = Guid.NewGuid().ToString();
        Assert.Null(Check(Reply(request, Sign(request))));
        Assert.True(Confirmations().Offers(Phone));
    }

    [Fact]
    public void A_reply_without_a_clear_has_nothing_to_check() => Assert.Null(Confirmations().For(Phone, Reply(Guid.NewGuid().ToString(), null)));

    [Fact]
    public void Policy_off_refuses_and_offers_nothing()
    {
        var request = Guid.NewGuid().ToString();
        var off = Confirmations(ReplyClear.Off);
        Assert.Contains("only at the machine", Check(Reply(request, Sign(request)), confirmations: off));
        Assert.False(off.Offers(Phone));
    }

    [Fact]
    public void A_box_changed_since_it_was_read_is_refused_by_its_digest()
    {
        var request = Guid.NewGuid().ToString();
        Assert.Contains("reads differently now", Check(Reply(request, Sign(request)), digest: EscapedApproval.Shown("rerun the tests, then ship")));
        Assert.Contains("reads differently now", Check(Reply(request, Sign(request, shown: "")), digest: Digest));
    }

    [Theory]
    [InlineData("other-session", "another session")]
    [InlineData("other-request", "another reply")]
    [InlineData("other-action", "not session.reply:clear")]
    [InlineData("other-machine", "not signed for this machine")]
    [InlineData("expired", "expired")]
    [InlineData("too-long", "at most 5 minutes")]
    public void A_confirmation_for_something_else_is_refused(string what, string expected)
    {
        var request = Guid.NewGuid().ToString();
        using var other = P256KeyPair.Generate();
        var command = what switch
        {
            "other-session" => Sign(request, session: "c0b11070-0000-4000-8000-0000000000bb"),
            "other-request" => Sign(Guid.NewGuid().ToString()),
            "other-action" => Sign(request, action: "session.approve:escaped"),
            "other-machine" => Sign(request, forMachine: other.Public),
            "expired" => Sign(request, issued: DateTimeOffset.UtcNow.AddHours(-1)),
            _ => Sign(request, lifetime: TimeSpan.FromMinutes(7)),
        };
        Assert.Contains(expected, Check(Reply(request, command)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_device_that_did_not_sign_it_a_machine_an_unlisted_device_or_one_not_connected_is_refused()
    {
        var request = Guid.NewGuid().ToString();
        using var other = P256KeyPair.Generate();
        var otherId = Wire.DeviceId(other.Public);
        Assert.Contains("must come from the device that signed it", Check(Reply(request, Sign(request)), peer: otherId, confirmations: Connected(otherId, "pwa", [otherId])));
        Assert.Contains("only a person's phone", Check(Reply(request, Sign(request)), confirmations: Connected(Phone, "machine", [Phone])));
        Assert.Contains("not approved at this machine to clear a composer", Check(Reply(request, Sign(request)), confirmations: Connected(Phone, "pwa", [])));
        Assert.Contains("not connected to the relay now", Check(Reply(request, Sign(request)), confirmations: Connected("c0b11070000040008000000000000999", "pwa", [Phone])));
    }

    ReplyClearConfirmations Connected(string id, string kind, List<string> list)
    {
        present.Clear();
        present.Add(new PresenceEntry(id, "d", kind, DateTimeOffset.UtcNow));
        approved = list;
        return Confirmations();
    }
}
