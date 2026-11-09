using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.AgentEnvelope;

// Why a reader refused an envelope. The code is the vectors' `refusedBy`, so one defect gives
// one word in every implementation.
public static class Refusal
{
    public const string Json = "json";
    public const string TooLarge = "too-large";
    public const string Duplicate = "duplicate-member";
    public const string Unknown = "unknown-member";
    public const string Missing = "missing-member";
    public const string Type = "wrong-type";
    public const string Base64 = "base64";
    public const string Length = "length";
    public const string Value = "value";
    public const string Version = "version";
    public const string Action = "action";
    public const string Target = "target";
    public const string Chain = "chain";
    public const string Lifetime = "lifetime";
    public const string NonCanonical = "non-canonical";
    public const string Der = "der";
    public const string HighS = "high-s";
    public const string Point = "point";
    public const string Signature = "signature";
    public const string Data = "data";
}

public sealed class EnvelopeException(string code, string where) : Exception($"{code}: {where}")
{
    public string Code { get; } = code;
    public string Where { get; } = where;
}

// F(x₁, …, xₙ) = L(x₁) ‖ … ‖ L(xₙ), L(x) = x's length as a 4-byte big-endian unsigned integer then
// x: #391 §3.2's framing, the same bytes. A signature is never made over raw content, so no
// signature this key makes for another purpose can be taken for an envelope's.
public static class Framing
{
    public const string AgentCommandLabel = "agent-bridge agent command v1";

    public static byte[] F(params byte[][] parts)
    {
        var length = 0L;
        foreach (var p in parts) { length += 4 + p.Length; }
        if (length > int.MaxValue) { throw new ArgumentException("framed input too long", nameof(parts)); }
        var framed = new byte[length];
        var at = 0;
        foreach (var p in parts)
        {
            BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(at), (uint)p.Length);
            p.CopyTo(framed, at + 4);
            at += 4 + p.Length;
        }
        return framed;
    }

    public static byte[] Label(string label) => Encoding.ASCII.GetBytes(label);

    // What a hostSign key signs for an envelope body.
    public static byte[] AgentCommand(ReadOnlySpan<byte> body) => F(Label(AgentCommandLabel), body.ToArray());
}

// Base64url without padding, with exactly one accepted spelling per value.
public static class Codec
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public static string Base64Url(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    // Refuses padding, characters outside the alphabet, a length no encoding has, and non-zero
    // trailing bits (a second spelling of the same bytes).
    public static bool TryBase64Url(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length % 4 == 1) { return false; }
        foreach (var c in text)
        {
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0) { return false; }
        }
        try { bytes = System.Buffers.Text.Base64Url.DecodeFromChars(text); }
        catch (FormatException) { return false; }
        return Base64Url(bytes) == text;
    }

    // `text` must be base64url of exactly `length` bytes.
    public static void CheckBytes(string text, int length, string where)
    {
        if (!TryBase64Url(text, out var bytes)) { throw new EnvelopeException(Refusal.Base64, where); }
        if (bytes.Length != length) { throw new EnvelopeException(Refusal.Length, where); }
    }

    // 32 lowercase hex characters: a device id, an account id or a roster generation.
    public static bool IsId(string text)
    {
        if (text.Length != 32) { return false; }
        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) { return false; }
        }
        return true;
    }

    // An account id is 32 lowercase hex or a GitHub numeric id (1 to 20 ASCII digits, no leading zero so one id has one spelling); every other id stays 32 hex.
    public static bool IsAccountId(string text)
    {
        if (IsId(text)) { return true; }
        if (text.Length is 0 or > 20 || (text.Length > 1 && text[0] == '0')) { return false; }
        foreach (var c in text)
        {
            if (c is < '0' or > '9') { return false; }
        }
        return true;
    }
}

// P-256 as a roster key and a signature are (#391 §3.2): uncompressed points checked on the curve
// by this code's own arithmetic, and signatures as strict minimal DER with low-S, so a point or a
// signature is accepted or refused the same way on every system.
public static class P256
{
    public const int PointBytes = 65;

    static readonly BigInteger P = Parse("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
    static readonly BigInteger B = Parse("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");
    static readonly BigInteger N = Parse("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
    static readonly BigInteger HalfN = N >> 1;

    static BigInteger Parse(string hex) => BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);

    static BigInteger Unsigned(ReadOnlySpan<byte> bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

    public static bool IsPoint(ReadOnlySpan<byte> point)
    {
        if (point.Length != PointBytes || point[0] != 0x04) { return false; }
        var x = Unsigned(point.Slice(1, 32));
        var y = Unsigned(point.Slice(33, 32));
        if (x >= P || y >= P) { return false; }
        var left = BigInteger.ModPow(y, 2, P);
        var right = ((BigInteger.ModPow(x, 3, P) - 3 * x + B) % P + P) % P;
        return left == right;
    }

    // r and s from strict minimal DER, each in [1, n); null for anything else.
    public static (BigInteger R, BigInteger S)? ReadDer(ReadOnlySpan<byte> der)
    {
        if (der.Length < 8 || der[0] != 0x30 || der[1] >= 0x80 || der[1] != der.Length - 2) { return null; }
        var at = 2;
        if (ReadInteger(der, ref at) is not { } r || ReadInteger(der, ref at) is not { } s || at != der.Length) { return null; }
        return r.IsZero || s.IsZero || r >= N || s >= N ? null : (r, s);
    }

    static BigInteger? ReadInteger(ReadOnlySpan<byte> der, ref int at)
    {
        if (at + 2 > der.Length || der[at] != 0x02) { return null; }
        var length = der[at + 1];
        if (length is 0 or >= 0x80 || at + 2 + length > der.Length) { return null; }
        var content = der.Slice(at + 2, length);
        if ((content[0] & 0x80) != 0 || (length > 1 && content[0] == 0 && (content[1] & 0x80) == 0)) { return null; }
        at += 2 + length;
        return Unsigned(content);
    }

    public static bool IsLowS(BigInteger s) => s <= HalfN;

    public static void CheckSignatureShape(ReadOnlySpan<byte> der, string where)
    {
        if (ReadDer(der) is not { } rs) { throw new EnvelopeException(Refusal.Der, where); }
        if (!IsLowS(rs.S)) { throw new EnvelopeException(Refusal.HighS, where); }
    }

    static byte[] Der(BigInteger r, BigInteger s)
    {
        var rb = Integer(r);
        var sb = Integer(s);
        var der = new byte[6 + rb.Length + sb.Length];
        der[0] = 0x30;
        der[1] = (byte)(4 + rb.Length + sb.Length);
        der[2] = 0x02;
        der[3] = (byte)rb.Length;
        rb.CopyTo(der, 4);
        der[4 + rb.Length] = 0x02;
        der[5 + rb.Length] = (byte)sb.Length;
        sb.CopyTo(der, 6 + rb.Length);
        return der;
    }

    static byte[] Integer(BigInteger v)
    {
        var bytes = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        return (bytes[0] & 0x80) != 0 ? [0, .. bytes] : bytes;
    }

    // Any valid DER signature as the low-S form of the same signature: what a signer sends.
    public static byte[] Normalize(ReadOnlySpan<byte> der)
    {
        if (ReadDer(der) is not { } rs) { throw new ArgumentException("not a strict DER P-256 signature", nameof(der)); }
        return Der(rs.R, IsLowS(rs.S) ? rs.S : N - rs.S);
    }

    // The same signature with s replaced by n − s: valid under the key, and high-S when the input
    // was low. For tests and vectors only; nothing sends one.
    internal static byte[] Flip(ReadOnlySpan<byte> der)
    {
        var rs = ReadDer(der) ?? throw new ArgumentException("not a strict DER P-256 signature", nameof(der));
        return Der(rs.R, N - rs.S);
    }

    public static bool Verify(ReadOnlySpan<byte> point, ReadOnlySpan<byte> message, ReadOnlySpan<byte> der)
    {
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() },
        });
        return key.VerifyData(message, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
