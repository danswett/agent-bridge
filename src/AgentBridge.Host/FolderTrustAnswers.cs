using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// launch.trust.answer (docs/protocol/v1-launch.md, "Folder trust"): a person answering a launched
// agent's folder-trust question from one of their devices. Trusting a folder lets the agent run
// what is in it, so only a person may, and only from a device of theirs:
//   * through the relay only, on a channel from that device (SessionService refuses the op on
//     this machine's pipe, which agents, the MCP server and the Home Assistant connector reach);
//   * with a command that device signed for this machine, this question and this choice;
//   * from a device the relay lists as the PWA or the Windows app, never a machine;
//   * and from a device a person approved at this machine (launch.remoteFolderTrustDevices):
//     the relay's kind is what a device says it is, and anything holding the account's token
//     (an agent on another machine, say) can make a key and say "pwa";
//   * and only where the machine allows it (launch.remoteFolderTrust), is not read-only, and
//     agent-ha-bridge is not running (LaunchService checks those, and the window).
public sealed class FolderTrustAnswers(P256KeyPair key, ISessionLauncher launcher, Action<string>? log = null, TimeProvider? time = null)
{
    readonly Action<string> log = log ?? (_ => { });
    readonly TimeProvider time = time ?? TimeProvider.System;
    Func<IReadOnlyList<PresenceEntry>>? presence;

    // The relay's kinds for a device a person answers from.
    static readonly HashSet<string> PersonKinds = new(["pwa", "app"], StringComparer.Ordinal);

    public void Attach(RelayClient? relay) => Volatile.Write(ref presence, relay is null ? null : () => relay.Presence);

    // For tests: who the relay says is connected.
    internal void AttachPresence(Func<IReadOnlyList<PresenceEntry>>? devices) => Volatile.Write(ref presence, devices);

    // hello's launch.trust, on a channel through the relay.
    public bool Available => Volatile.Read(ref presence) is not null && launcher.AnswersTrust;

    public async Task<TrustAnswerResult> AnswerAsync(string peer, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException($"{TrustProtocol.Answer} needs a question, a choice and a command"); }
        TrustAnswerRequest? request;
        try { request = data.Deserialize<TrustAnswerRequest>(ProtocolJson.Options); }
        catch (JsonException e) { throw new ArgumentException($"{TrustProtocol.Answer} could not be read: {e.Message}"); }
        if (request?.Command is null || request.Question is null || request.Choice is null) { throw new ArgumentException($"{TrustProtocol.Answer} needs a question, a choice and a command"); }
        if (request.Choice is not (TrustProtocol.Trust or TrustProtocol.Quit)) { return Refused(peer, $"the choice is '{TrustProtocol.Trust}' or '{TrustProtocol.Quit}'"); }
        if (Volatile.Read(ref presence) is not { } devices) { return Refused(peer, "this machine is not connected to the relay now"); }

        var (c, problem) = SignedCommands.Verify(key, request.Command, TrustProtocol.Action(request.Choice), time.GetUtcNow());
        if (c is null) { return Refused(peer, problem!); }
        var me = Wire.DeviceId(key.Public);
        if (c.To != me) { return Refused(peer, "the command is for another machine to act on"); }
        if (c.Target.Session != request.Question) { return Refused(peer, "the command is for another question"); }
        // The device that signed is the one asking, and a person's: a machine of the account can
        // make a key and sign with it, so the MAC alone says nothing about who pressed.
        if (c.Principal.Device != peer) { return Refused(peer, "the command must come from the device that signed it"); }
        if (c.Principal.Device == me) { return Refused(peer, "this machine cannot answer for a person"); }
        var signer = devices().FirstOrDefault(d => d.Id == c.Principal.Device);
        if (signer is null) { return Refused(peer, "the device that signed the command is not connected to the relay now"); }
        if (!PersonKinds.Contains(signer.Kind)) { return Refused(peer, "only a person's phone, browser or Windows app can answer a folder-trust question"); }

        return await launcher.AnswerTrustAsync(Guid.Parse(c.Request), request.Question, request.Choice == TrustProtocol.Trust, peer, $"{signer.Kind} {peer[..8]}");
    }

    TrustAnswerResult Refused(string peer, string why)
    {
        log($"folder-trust answer refused from {peer[..Math.Min(8, peer.Length)]}: {why}");
        return new TrustAnswerResult(TrustAnswerOutcome.Refused, why);
    }
}
