using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.Transport;

// The relay protocol. Everything in this file is visible to the relay: routing ids,
// sizes, timing and the channel header. Everything a session says travels inside Noise
// and is not.
public static class Wire
{
    public const byte DataFrame = 0x01;
    public const int IdBytes = 16;
    // kind (1) + channel (8) + one whole Noise message.
    public const int ChannelHeader = 9;
    public const int MaxChannelPayload = ChannelHeader + NoiseConst.MaxMessage;
    public const int MaxFrame = 1 + IdBytes + MaxChannelPayload;
    public const int MaxControl = 4096;
    // Still says s4: the label is part of what the relay checks and the PWA computes, and the
    // live relay and the published PWA were built with it. Changing it is a protocol version.
    const string ProofLabel = "agent-bridge-s4-relay-proof";

    // Self-certifying: a device id is the hash of its static key, so a relay that hands
    // a client someone else's key under a known id is caught by the handshake.
    public static string DeviceId(byte[] staticPublic) => Hex(SHA256.HashData(staticPublic).AsSpan(0, IdBytes));

    // Proof of possession for an ECDH-only key. WebCrypto ECDH keys and TPM ECDH keys
    // cannot sign, so the device proves it holds the key with an HMAC keyed by ECDH
    // between its static key and a fresh relay ephemeral.
    public static byte[] ProofMac(byte[] sharedSecret, byte[] nonce, string accountId)
    {
        byte[] data = [.. Encoding.UTF8.GetBytes(ProofLabel), .. nonce, .. Encoding.UTF8.GetBytes(accountId)];
        return HMACSHA256.HashData(sharedSecret, data);
    }

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    public static byte[] Unhex(string s) => Convert.FromHexString(s);

    public static bool IsDeviceId(string? s) => s is { Length: IdBytes * 2 } && s.All(Uri.IsHexDigit);

    public static byte[] DataFrameBytes(string peerId, ReadOnlySpan<byte> payload)
    {
        if (!IsDeviceId(peerId)) { throw new ArgumentException("not a device id", nameof(peerId)); }
        var f = new byte[1 + IdBytes + payload.Length];
        f[0] = DataFrame;
        Unhex(peerId).CopyTo(f, 1);
        payload.CopyTo(f.AsSpan(1 + IdBytes));
        return f;
    }

    // Null when the peer closed. Throws FrameTooLargeException before buffering more than max.
    public static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveAsync(WebSocket ws, int max, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) { return null; }
            if (ms.Length + r.Count > max) { throw new FrameTooLargeException(); }
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return (r.MessageType, ms.ToArray());
    }
}

public sealed class FrameTooLargeException() : Exception("frame too large");

public static class CloseCodes
{
    public const int Protocol = 4000;
    public const int AuthFailed = 4001;
    public const int ProofFailed = 4003;
    public const int RateLimited = 4008;
    public const int Replaced = 4009;
    public const int TooLarge = 4013;
    public const int TooManyDevices = 4029;
    // Sent by a relay that is shutting down: reconnect now, and land on a replica that is not.
    public const int Restarting = 1012;
}

public enum ChannelKind : byte { Msg1 = 1, Msg2 = 2, Msg3 = 3, Transport = 4, Close = 5 }
