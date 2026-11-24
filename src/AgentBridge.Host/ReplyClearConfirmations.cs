using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Clear and send (#804): the confirmation a person's device signs for the box it was shown, as the host takes it from the relay.
// The person checks are PersonConfirmations'; this adds the policy and what the command names: this session, this reply's own
// request, and the digest of the box as the host reads it now (ComposerInput.Digest), so a box changed since is refused again.
// Nothing here makes a clear happen: ReplyRequest.ClearConfirmation takes the function For returns (#804 slice 3b).
public sealed class ReplyClearConfirmations(P256KeyPair key, string policy, Func<IReadOnlyCollection<string>> devices, Action<string>? log = null, TimeProvider? time = null)
{
    readonly Action<string> log = log ?? (_ => { });
    readonly PersonConfirmations person = new(key, devices, "clear a composer", NotApproved, time);
    public RemoteAuthorization? Trust { get => person.Trust; init => person.Trust = value; }

    public void Attach(RelayClient? relay) => person.Attach(relay);
    internal void AttachPresence(Func<IReadOnlyList<PresenceEntry>>? connected) => person.AttachPresence(connected);

    // Whether hello offers sessions.replyClear to this device: advisory, so a client never offers what would only be refused.
    public bool Offers(string peer) => Refusal(peer) is null;

    string? Refusal(string peer) => policy != ReplyClear.Confirm
        ? "this machine clears a composer only at the machine (replies.clearAndSend)"
        : person.Refusal(peer);

    public static string NotApproved(string device) =>
        $"this device ({device[..Math.Min(8, device.Length)]}) is not approved at this machine to clear a composer (permissions.escapedApprovalDevices, or launch.remoteFolderTrustDevices); clear it at the machine";

    // The check ReplyRequest.ClearConfirmation runs with the digest of the box as read now, or null when the reply carries no clear.
    public Func<string, string?>? For(string peer, ReplyRequest request) =>
        request.Clear is null ? null : digest => Check(peer, request, digest);

    string? Check(string peer, ReplyRequest request, string digest)
    {
        var why = Why(peer, request, digest);
        if (why is not null) { log($"clear and send refused from {peer[..Math.Min(8, peer.Length)]}: {why}"); }
        return why;
    }

    string? Why(string peer, ReplyRequest request, string digest)
    {
        if (Refusal(peer) is { } refused) { return refused; }
        var (c, problem) = person.Verify(peer, request.Clear!, ReplyClear.Action);
        if (c is null) { return problem; }
        if (c.Target.Session != request.Id) { return "the confirmation is for another session"; }
        if (!Guid.TryParseExact(request.Request, "D", out var asked) || !Guid.TryParseExact(c.Request, "D", out var signed) || asked != signed)
        {
            return "the confirmation is for another reply; press again";
        }
        // What the person read is the whole box as the host reads it now: a box changed since reads otherwise, and a confirmation of the old one clears nothing.
        return c.Target.Shown is { } shown && shown == digest ? null : "the box reads differently now from what was confirmed; read it again";
    }
}
