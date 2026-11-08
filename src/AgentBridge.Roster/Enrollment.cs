using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AgentBridge.Roster;

// The values the code exchange (§4.2) and the QR paths (§4.3) compute. The channel, the messages
// and the attempt limits are R3's; these are the primitives both sides must compute alike, which
// docs/protocol/vectors/roster.json pins for every client.
public static class Enrollment
{
    public const int NonceBytes = 32;
    public const int QrSecretBytes = 16;

    // N's commitment to its nonce before it sees A's: C = SHA-256(F(commit label, h, nN)).
    public static byte[] Commit(byte[] h, byte[] nN) =>
        Framing.Sha256(Framing.F(Framing.Label(Framing.EnrollCommitLabel), h, nN));

    // What a person's joining device signs with its `sign` key to prove it holds it on this channel.
    public static byte[] KeyProof(byte[] h) => Framing.F(Framing.Label(Framing.EnrollKeyLabel), h);

    // The transcript an add's approval carries: T = SHA-256(F(label, account, h, nA, nN)).
    public static byte[] Transcript(string account, byte[] h, byte[] nA, byte[] nN) =>
        Framing.Sha256(Framing.F(Framing.Label(Framing.EnrollLabel), Framing.Utf8(account), h, nA, nN));

    public static string Code1(byte[] transcript) => Code(Framing.EnrollCode1Label, transcript);

    public static string Code2(byte[] transcript) => Code(Framing.EnrollCode2Label, transcript);

    // U32(SHA-256(F(label, T))) mod 10⁶, as six zero-padded digits.
    static string Code(string label, byte[] transcript)
    {
        var hash = Framing.Sha256(Framing.F(Framing.Label(label), transcript));
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    // A code as shown: two groups of three.
    public static string Shown(string code) => $"{code[..3]} {code[3..]}";

    // What the person typed, with spaces dropped, if it is exactly six ASCII digits; else null.
    public static string? Typed(string input)
    {
        var code = input.Replace(" ", "", StringComparison.Ordinal);
        if (code.Length != 6) { return null; }
        foreach (var c in code)
        {
            if (c is < '0' or > '9') { return null; }
        }
        return code;
    }

    // Whether the person typed the expected code, compared in fixed time.
    public static bool Matches(string expected, string input)
    {
        var typed = Typed(input);
        return typed is not null && CryptographicOperations.FixedTimeEquals(Framing.Utf8(expected), Framing.Utf8(typed));
    }

    // The hint a QR-joining device shows the relay: the first 8 bytes of HMAC(q, F(label)), as hex.
    public static string InviteHint(byte[] q) =>
        Codec.Hex(HMACSHA256.HashData(q, Framing.F(Framing.Label(Framing.InviteHintLabel))).AsSpan(0, 8));

    public static byte[] QrJoiner(byte[] q, byte[] h) => HMACSHA256.HashData(q, Framing.F(Framing.Label(Framing.QrJoinerLabel), h));

    public static byte[] QrApprover(byte[] q, byte[] h) => HMACSHA256.HashData(q, Framing.F(Framing.Label(Framing.QrApproverLabel), h));

    public static bool MacMatches(byte[] expected, byte[] received) => CryptographicOperations.FixedTimeEquals(expected, received);
}
