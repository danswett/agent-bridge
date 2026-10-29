using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Commands a person's device signs for one machine (design §5.5), as a transfer needs: the
// machine a session is on acts only on a command from a device of the account, naming that
// machine, that session and the machine it goes to, inside a lifetime, once. This retires
// agent-ha-bridge's shared fleet secret, which proved only that the sender held it.
//
// The device keys are ECDH keys (a TPM's, or WebCrypto's non-extractable ones, which cannot
// sign), so the signature is a MAC under a key from static-static ECDH between the signing
// device and the machine it is for: only those two can make it. The machine that carries the
// command to its target cannot change a byte of it, and nor can the relay.
//
//   key  = HKDF-SHA256(ikm: ECDH(signer, machine), salt: "agent-bridge command v1",
//                      info: "<signer device id>|<machine device id>", 32 bytes)
//   mac  = HMAC-SHA256(key, the body's exact UTF-8 bytes)
//
// The body travels as those bytes (base64url), so neither end has to agree on how to write
// JSON. pwa/js/transfer.js signs the same way, and a test vector holds both to it.
public static class SignedCommands
{
    static readonly byte[] Salt = "agent-bridge command v1"u8.ToArray();

    public static byte[] Key(P256KeyPair self, byte[] peerPublic, string signerId, string machineId)
    {
        var z = self.Dh(peerPublic);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 32, Salt, Encoding.ASCII.GetBytes($"{signerId}|{machineId}"));
    }

    public static SignedCommand Sign(P256KeyPair signer, byte[] machinePublic, CommandBody body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, ProtocolJson.Options);
        var key = Key(signer, machinePublic, Wire.DeviceId(signer.Public), Wire.DeviceId(machinePublic));
        return new SignedCommand
        {
            Body = BlobFormat.Base64Url(bytes),
            By = BlobFormat.Base64Url(signer.Public),
            Mac = BlobFormat.Base64Url(HMACSHA256.HashData(key, bytes)),
        };
    }

    // The body, when the command is one this machine (self) may act on now for action; else why
    // not. Its request still has to be remembered by the caller, so it is acted on once.
    public static (CommandBody? Body, string? Problem) Verify(P256KeyPair self, SignedCommand command, string action, DateTimeOffset now)
    {
        if (TransferProtocol.Check(command) is { } malformed) { return (null, malformed); }
        var body = BlobFormat.FromBase64Url(command.Body);
        var by = BlobFormat.FromBase64Url(command.By);
        var mac = BlobFormat.FromBase64Url(command.Mac);
        if (body is null || by is null || mac is not { Length: 32 }) { return (null, "the signed command is malformed"); }
        try { P256KeyPair.AssertOnCurve(by); }
        catch (CryptographicException) { return (null, "the signed command's key is not a P-256 public key"); }
        var me = Wire.DeviceId(self.Public);
        var signer = Wire.DeviceId(by);
        var expected = HMACSHA256.HashData(Key(self, by, signer, me), body);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac)) { return (null, "the command was not signed for this machine by the device it names"); }

        CommandBody? c;
        try { c = JsonSerializer.Deserialize<CommandBody>(body, ProtocolJson.Options); }
        catch (JsonException) { return (null, "the signed command's body is not a command"); }
        if (c is null || c.Principal is null || c.Target is null) { return (null, "the signed command's body is not a command"); }
        if (c.V != 1) { return (null, $"the signed command is version {c.V}; this host reads version 1"); }
        if (c.Action != action) { return (null, $"the command is for {Limits.KeepStart(c.Action, 64)}, not {action}"); }
        if (!Guid.TryParseExact(c.Request, "D", out _)) { return (null, "the command's request must be a UUID"); }
        if (c.Principal.Kind != "user") { return (null, "only a person's device can ask for this"); }
        if (c.Principal.Device != signer) { return (null, "the command names a device other than the one that signed it"); }
        if (c.Target.Machine != me) { return (null, "the command is for another machine"); }
        if (!TransferProtocol.IsDeviceId(c.To)) { return (null, "the command does not name the machine it is for"); }
        var issued = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(c.IssuedAt, 0, 253402300799));
        var expires = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(c.ExpiresAt, 0, 253402300799));
        if (expires <= issued || expires - issued > TransferProtocol.MaxLifetime) { return (null, $"a command lasts at most {TransferProtocol.MaxLifetime.TotalMinutes:0} minutes"); }
        if (issued - TransferProtocol.Skew > now) { return (null, "the command was signed in the future; check the clocks of the two devices"); }
        if (expires < now) { return (null, "the command has expired; press again"); }
        return (c, null);
    }
}

// Commands acted on, by request, until they expire: a command carried again is refused, so one
// press is one transfer however many times it is sent.
public sealed class CommandMemory(TimeProvider? time = null, int capacity = 4096)
{
    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly Dictionary<string, DateTimeOffset> seen = new(StringComparer.Ordinal);
    readonly Lock gate = new();

    // True the first time; false for a request already acted on, or when full (a flood of
    // valid commands is from the account's own devices, and waits for some to expire).
    public bool Take(CommandBody c)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            foreach (var (k, until) in seen.ToList()) { if (until < now) { seen.Remove(k); } }
            if (seen.ContainsKey(c.Request) || seen.Count >= capacity) { return false; }
            // Kept past expiry by the skew, so a command a fast clock signed is not taken twice.
            seen[c.Request] = DateTimeOffset.FromUnixTimeSeconds(c.ExpiresAt) + TransferProtocol.Skew;
            return true;
        }
    }
}
