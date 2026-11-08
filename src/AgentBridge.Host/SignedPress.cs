using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// A person's press that a device signed for this machine, as manage.* (#801) and Update now (#1159)
// take one: the one gate both pass, so a rule added for one is never missing from the other. It
// checks, in this order, that the command verifies for this machine and action as of its own
// signing time (SignedCommands, the MAC and every field first), that the device sending it is the
// one that signed it, is connected to the relay now as a person's (pwa or app, until #55's roster),
// is not the app beside this host (whose key an agent here could use as the same user), and that
// the machine is not read-only. Lifetime is checked apart (Lifetime), after the caller's own
// replay record, so a press already answered gets that answer again even past its lifetime.
//
// Only Verify makes one, and only from a press that passed it: LifecycleCoordinator grants an
// unannounced Update departure Legacy only for a lease taken with one (docs/host-lifecycle.md), so
// nothing but a verified person's press can leave that way, never an automatic update.
public sealed class SignedPress
{
    // Connection's answer to Q-C1 on #886: a person's device is pwa or app, as for update and folder trust.
    static readonly HashSet<string> PersonKinds = new(["pwa", "app"], StringComparer.Ordinal);

    SignedPress(CommandBody body, string request, string device, string by, string name) =>
        (Body, Request, Device, By, Name) = (body, request, device, by, name);

    public CommandBody Body { get; }
    // The body's request, as a UUID's canonical form.
    public string Request { get; }
    public string Device { get; }
    // The device's kind and short id ("pwa 1a2b3c4d"), and the name it gives, cleaned.
    public string By { get; }
    public string Name { get; }
    public DateTimeOffset IssuedAt => DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(Body.IssuedAt, 0, 253402300799));

    public static bool IsPerson(string? kind) => kind is not null && PersonKinds.Contains(kind);

    // What a refusal names: the reason code (ManageReason), the words for the log, and the press's
    // request once the signature has shown it.
    internal readonly record struct Checked(SignedPress? Press, string? Code, string? Detail, string? Request);

    // The words a refusal uses for what the press asked: "manage a machine", "update a machine now".
    internal sealed record Words(string Act, string Command);

    // Its parts bounded for this action before anything is decoded (TransferProtocol.MaxBodyFor).
    static CommandBody? Bounded(SignedCommand command, string action) =>
        TransferProtocol.Check(command, action) is null ? Peek(command) : null;

    // A press this host verified and recorded before (UpdateNowRecord), read back for its departure,
    // perhaps by a host that has restarted since: its MAC and fields verified again as of its own
    // signing time. Who sent it was checked when it was pressed; by is what was recorded then.
    internal static SignedPress? Reverify(P256KeyPair self, SignedCommand command, string action, string by)
    {
        if (Bounded(command, action) is not { } peeked) { return null; }
        var (body, _) = SignedCommands.Verify(self, command, action, DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(peeked.IssuedAt, 0, 253402300799)));
        return body is null ? null : new SignedPress(body, Guid.Parse(body.Request).ToString(), body.Principal.Device, by, "");
    }

    internal static Checked Verify(P256KeyPair self, SignedCommand? command, string action, string peer, PresenceEntry? who,
        Func<string, bool> isLocalApp, Func<string?> readOnly, Words words)
    {
        if (command is null || Bounded(command, action) is not { } peeked)
        {
            return new(null, ManageReason.BadSignature, "the command is malformed", null);
        }
        // Verified as of its own signing time, so the MAC and every field are checked first; its
        // lifetime is checked by Lifetime, after the replay check.
        var (body, problem) = SignedCommands.Verify(self, command, action, DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(peeked.IssuedAt, 0, 253402300799)));
        if (body is null) { return new(null, ManageReason.BadSignature, problem ?? "the command could not be verified", null); }
        var request = Guid.Parse(body.Request).ToString();
        if (body.Principal.Device != peer) { return new(null, ManageReason.NotPeer, "the command must be signed by the device sending it", request); }
        if (who is null) { return new(null, ManageReason.NotConnected, "the device asking is not connected to the relay now", request); }
        if (!IsPerson(who.Kind)) { return new(null, ManageReason.NotPerson, $"only a person's phone, browser or Windows app can {words.Act}", request); }
        if (isLocalApp(peer)) { return new(null, ManageReason.LocalDevice, "the app on this machine manages it from its Settings, not through the relay", request); }
        if (readOnly() is { } ro) { return new(null, ManageReason.ReadOnly, $"this machine is read-only for other devices: {ro}", request); }
        var by = $"{who.Kind} {peer[..8]}";
        var name = Limits.KeepStart(HostConfigFile.CleanName(who.Name), ManageProtocol.Name) ?? "";
        return new(new SignedPress(body, request, peer, by, name), null, null, request);
    }

    // Null while the press may still be acted on: signed no more than Skew in the future, not
    // expired, and lasting at most ManageProtocol.MaxLifetime.
    internal static (string Code, string Detail)? Lifetime(CommandBody body, DateTimeOffset now, Words words)
    {
        var issued = DateTimeOffset.FromUnixTimeSeconds(body.IssuedAt);
        var expires = DateTimeOffset.FromUnixTimeSeconds(body.ExpiresAt);
        if (issued - ManageService.Skew > now) { return (ManageReason.Future, "the command was signed in the future; check the clocks of the two devices"); }
        if (expires < now) { return (ManageReason.Expired, "the command has expired; press again"); }
        if (expires - issued > ManageProtocol.MaxLifetime) { return (ManageReason.Expired, $"{words.Command} lasts at most 2 minutes"); }
        return null;
    }

    static CommandBody? Peek(SignedCommand command)
    {
        try
        {
            return BlobFormat.FromBase64Url(command.Body) is { } bytes ? JsonSerializer.Deserialize<CommandBody>(bytes, ProtocolJson.Options) : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { return null; }
    }
}
