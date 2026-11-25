using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// What every confirmation a person's device signs has to be, however its target differs (escaped approvals #803, clear and send #804):
// taken through the relay from a device that signed it, never this machine, never its pipe or Home Assistant; a device the relay lists as
// a person's (the PWA or the Windows app) and one a person approved at this machine; an independently pinned person key; for this
// machine, signed at most five minutes before. The caller adds its own policy and what the command names.
public sealed class PersonConfirmations(P256KeyPair key, Func<IReadOnlyCollection<string>> devices, string what, Func<string, string> notApproved, TimeProvider? time = null)
{
    readonly TimeProvider time = time ?? TimeProvider.System;
    // The relay's kinds for a device a person answers from, as for folder trust.
    static readonly HashSet<string> PersonKinds = new(["pwa", "app"], StringComparer.Ordinal);
    Func<IReadOnlyList<PresenceEntry>>? presence;
    public RemoteAuthorization? Trust { get; set; }

    public void Attach(RelayClient? relay) => Volatile.Write(ref presence, relay is null ? null : () => relay.Presence);

    // For tests: who the relay says is connected.
    internal void AttachPresence(Func<IReadOnlyList<PresenceEntry>>? connected) => Volatile.Write(ref presence, connected);

    // Why this device may not confirm at all now, or null.
    public string? Refusal(string peer)
    {
        if (Trust?.HasRole(peer, TrustedPeerStore.Person) == false) { return $"only an independently pinned person key may {what}"; }
        if (Volatile.Read(ref presence) is not { } connected) { return "this machine is not connected to the relay now"; }
        var signer = connected().FirstOrDefault(d => d.Id == peer);
        if (signer is null) { return "the device that signed the confirmation is not connected to the relay now"; }
        if (!PersonKinds.Contains(signer.Kind)) { return $"only a person's phone, browser or Windows app can {what}"; }
        if (!devices().Contains(peer)) { return notApproved(peer); }
        return null;
    }

    // The command's body when the device peer signed it for this machine now (the action, its lifetime, the key and the device), else why not.
    // What it names (session, request, the thing read) is for the caller.
    public (CommandBody? Body, string? Problem) Verify(string peer, SignedCommand command, string action)
    {
        var (c, problem) = SignedCommands.Verify(key, command, action, time.GetUtcNow());
        if (c is null) { return (null, problem); }
        if (c.ExpiresAt - c.IssuedAt > (long)EscapedApproval.MaxLifetime.TotalSeconds) { return (null, $"a confirmation lasts at most {EscapedApproval.MaxLifetime.TotalMinutes:0} minutes"); }
        if (Trust is { } trust && !trust.PersonKey(command.By)) { return (null, $"only an independently pinned person key may {what}"); }
        var me = Wire.DeviceId(key.Public);
        if (c.To != me) { return (null, "the confirmation is for another machine to act on"); }
        // The device that signed is the one asking: a machine of the account can make a key and sign with it, so the MAC alone says nothing about who pressed.
        if (c.Principal.Device != peer) { return (null, "the confirmation must come from the device that signed it"); }
        if (c.Principal.Device == me) { return (null, "this machine cannot confirm for a person"); }
        return (c, null);
    }
}
