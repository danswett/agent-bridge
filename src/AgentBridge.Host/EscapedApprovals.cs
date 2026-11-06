using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Approve on an approval the host escaped anything in (#803, docs/protocol/v1-sessions.md, "Escaped
// approvals"). Deny needs nothing of this. Approve is a two-step confirmation from a person, so the
// command a device signed when they pressed it is taken only:
//   * through the relay, on a channel from the device that signed it: never this machine's pipe,
//     which agents and the MCP server reach, nor the Home Assistant connector, whose users include
//     agents (guard #67). Neither can hand one to AnswerService at all;
//   * from a device the relay lists as a person's (the PWA or the Windows app), never a machine,
//     and one a person approved at this machine: permissions.escapedApprovalDevices, or while that
//     is absent launch.remoteFolderTrustDevices while folder trust is on (Devices). The relay's kind is what a device says it is, and
//     anything holding the account's token can make a key and say "pwa";
//   * for this machine, this session, this question, exactly the escaped message the host would
//     send now (EscapedText.Shown, computed again at each check), and the answer's own request, so
//     one press is one answer however many times it is sent; signed at most five minutes before;
//   * while permissions.escapedApproval is confirm.
// Checked when the answer is taken and again once its typing slot is held (AnswerService), so a
// device removed from the list meanwhile stops it before any key. A command changed meanwhile is
// another question id, which the slot's own checks refuse (for Codex, as stale, from the question
// rebuilt from its fresh marker, #958).
public sealed class EscapedApprovals(P256KeyPair key, string policy, Func<IReadOnlyCollection<string>> devices, Action<string>? log = null, TimeProvider? time = null)
{
    readonly Action<string> log = log ?? (_ => { });
    readonly TimeProvider time = time ?? TimeProvider.System;
    Func<IReadOnlyList<PresenceEntry>>? presence;
    public RemoteAuthorization? Trust { get; init; }

    // The devices that may confirm, read at each check: the explicit list (even an empty one), or
    // while there is none the folder-trust devices, and those only while folder trust is on.
    // Inherited regardless, a phone listed for folder trust kept Approve after folder trust was
    // turned off (a lost phone, say), while Settings hid the list it would be removed from.
    public static Func<IReadOnlyCollection<string>> Devices(IReadOnlyList<string>? listed, Func<bool> folderTrust, Func<IReadOnlyCollection<string>> folderTrustDevices) =>
        listed is { } explicitly ? () => explicitly : () => folderTrust() ? folderTrustDevices() : [];

    // The relay's kinds for a device a person answers from, as for folder trust.
    static readonly HashSet<string> PersonKinds = new(["pwa", "app"], StringComparer.Ordinal);

    public void Attach(RelayClient? relay) => Volatile.Write(ref presence, relay is null ? null : () => relay.Presence);

    // For tests: who the relay says is connected.
    internal void AttachPresence(Func<IReadOnlyList<PresenceEntry>>? connected) => Volatile.Write(ref presence, connected);

    // Whether hello offers sessions.approveEscaped to this device: advisory, so a card never offers
    // a confirmation that would only be refused.
    public bool Offers(string peer) => Refusal(peer) is null;

    // The confirmation an answer carries, as AnswerService checks it, or null when it carries none.
    public IEscapedConfirmation? For(string peer, AnswerRequest request) =>
        request.Confirm is null ? null : new Confirmation(this, peer, request);

    // Why this device may not confirm one at all now, or null.
    string? Refusal(string peer)
    {
        if (policy != EscapedApproval.Confirm)
        {
            return "this machine takes Approve for an approval with escaped characters only at the machine (permissions.escapedApproval); deny it from here, or answer at the machine";
        }
        if (Trust?.HasRole(peer, TrustedPeerStore.Person) == false) { return "only an independently pinned person key may approve one with escaped characters"; }
        if (Volatile.Read(ref presence) is not { } connected) { return "this machine is not connected to the relay now"; }
        var signer = connected().FirstOrDefault(d => d.Id == peer);
        if (signer is null) { return "the device that signed the confirmation is not connected to the relay now"; }
        if (!PersonKinds.Contains(signer.Kind)) { return "only a person's phone, browser or Windows app can approve one with escaped characters"; }
        if (!devices().Contains(peer)) { return NotApproved(peer); }
        return null;
    }

    public static string NotApproved(string device) =>
        $"this device ({device[..Math.Min(8, device.Length)]}) is not approved at this machine to approve one with escaped characters (permissions.escapedApprovalDevices, or launch.remoteFolderTrustDevices); deny it, or answer at the machine";

    string? Check(string peer, AnswerRequest request, Question question)
    {
        if (Refusal(peer) is { } refused) { return refused; }
        var (c, problem) = SignedCommands.Verify(key, request.Confirm!, EscapedApproval.Action, time.GetUtcNow());
        if (c is null) { return problem; }
        if (c.ExpiresAt - c.IssuedAt > (long)EscapedApproval.MaxLifetime.TotalSeconds) { return $"a confirmation lasts at most {EscapedApproval.MaxLifetime.TotalMinutes:0} minutes"; }
        if (Trust is { } trust && !trust.PersonKey(request.Confirm!.By)) { return "only an independently pinned person key may approve one with escaped characters"; }
        var me = Wire.DeviceId(key.Public);
        if (c.To != me) { return "the confirmation is for another machine to act on"; }
        // The device that signed is the one asking: a machine of the account can make a key and
        // sign with it, so the MAC alone says nothing about who pressed.
        if (c.Principal.Device != peer) { return "the confirmation must come from the device that signed it"; }
        if (c.Principal.Device == me) { return "this machine cannot confirm for a person"; }
        if (c.Target.Session != request.Id) { return "the confirmation is for another session"; }
        if (c.Target.Question != question.Id) { return "the confirmation is for another question"; }
        if (!Guid.TryParseExact(request.Request, "D", out var asked) || !Guid.TryParseExact(c.Request, "D", out var signed) || asked != signed)
        {
            return "the confirmation is for another answer; press again";
        }
        // What the person read is what the host would send now, all of it: a command changed since
        // reads otherwise, and a confirmation of the old one approves nothing.
        if (question.Escaped is not { } escaped || c.Target.Shown is not { } shown
            || shown != escaped.Shown || shown != EscapedApproval.Shown(question.Message))
        {
            return "the approval reads differently now from what was confirmed; read it again";
        }
        return null;
    }

    sealed class Confirmation(EscapedApprovals owner, string peer, AnswerRequest request) : IEscapedConfirmation
    {
        public string? Check(Question question)
        {
            var why = owner.Check(peer, request, question);
            if (why is not null) { owner.log($"escaped approval refused from {peer[..Math.Min(8, peer.Length)]}: {why}"); }
            return why;
        }
    }
}
