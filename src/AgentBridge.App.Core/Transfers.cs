using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// Bringing a session that ended on one of the account's machines to another, and resuming it
// there as a session of its own (docs/protocol/v1-transfer.md, the PWA's pwa/js/transfer.js).
// The app is a person's device, kind "app": it signs a command for the machine the session is on
// (the source), naming it, the session and the machine it goes to (the target), and sends
// session.launch with it to the target, which carries it to the source. The original stays.

// What one of the account's machines offers for a transfer: its resumable sessions, and its
// static key from the channel's handshake, which a command for it is signed for.
public sealed record TransferSource(string HostId, string MachineName, byte[] Key, IReadOnlyList<ResumableSession> Resumable);

public static class Transfers
{
    // How long a command may be carried out for, as the PWA signs it: the source takes at most ten
    // minutes (TransferProtocol.MaxLifetime).
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromMinutes(5);
    // A transfer waits up to twelve minutes for its outcome, and is resent for up to twenty from
    // its first send: a host taking transfers remembers a launch's outcome for 25 minutes after
    // it settles (v1-transfer.md), so no resend in that time finds it forgotten.
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromMinutes(12);
    public static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(20);

    // A session.transfer command signed by this device (key) for the source (machinePublic):
    // the body's exact bytes, this device's public key, and the MAC only the two can make.
    public static SignedCommand Sign(P256KeyPair key, byte[] machinePublic, string session, string to, DateTimeOffset now, string request)
    {
        var issued = now.ToUnixTimeSeconds();
        var body = JsonSerializer.SerializeToUtf8Bytes(new CommandBody
        {
            V = 1,
            Action = TransferProtocol.Action,
            Request = request,
            Principal = new CommandPrincipal { Kind = "user", Device = Wire.DeviceId(key.Public) },
            Target = new CommandTarget { Machine = Wire.DeviceId(machinePublic), Session = session },
            To = to,
            IssuedAt = issued,
            ExpiresAt = issued + (long)CommandLifetime.TotalSeconds,
        }, ProtocolJson.Options);
        return new SignedCommand
        {
            Body = BlobFormat.Base64Url(body),
            By = BlobFormat.Base64Url(key.Public),
            Mac = BlobFormat.Base64Url(CommandMac.Of(key, machinePublic, body)),
        };
    }

    // One machine asked, over a short channel of its own, what it hands on: null when it does
    // not (no sessions.transfer.serve), has nothing resumable this app can launch, or does not
    // answer (asleep, offline, older). As collectRemote in the PWA.
    public static async Task<TransferSource?> AskAsync(IClientChannel ch, string hostId, string presenceName, TimeSpan optionsTimeout, CancellationToken ct)
    {
        if (ch is not RelayClientChannel { Channel.PeerStatic: { } key }) { return null; }
        var hello = await ch.HelloAsync(ct);
        if (!hello.Capabilities.Contains(TransferProtocol.ServeCapability)) { return null; }
        var options = (await ch.CallAsync(LaunchProtocol.Options, null, optionsTimeout, ct)).Deserialize<LaunchOptions>(ProtocolJson.Options);
        var resumable = options?.Resumable.Where(r => LaunchProtocol.Agents.Contains(r.Launcher)).ToList() ?? [];
        if (resumable.Count == 0) { return null; }
        return new TransferSource(hostId, hello.Machine.Name is { Length: > 0 } n ? n : presenceName, [.. key], resumable);
    }
}
