using System.Numerics;
using System.Security.Cryptography;

namespace AgentBridge.Roster;

// P-256 for roster keys and signatures (§3.2): uncompressed SEC1 points, checked on the curve
// before any use, and ECDSA signatures as strict minimal DER SEQUENCE { INTEGER r, INTEGER s }.
// The checks are this code's own arithmetic, not a platform's import, so a point or signature is
// accepted or refused the same way on every system and in the PWA's port.
public static class P256
{
    public const int PointBytes = 65;

    static readonly BigInteger P = Parse("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
    static readonly BigInteger B = Parse("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");
    static readonly BigInteger N = Parse("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
    static readonly BigInteger HalfN = N >> 1;

    static BigInteger Parse(string hex) => BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);

    static BigInteger Unsigned(ReadOnlySpan<byte> bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

    // 0x04 ‖ X ‖ Y with X and Y below p and y² = x³ − 3x + b (mod p). The point at infinity has no
    // such encoding, so it never passes.
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

    public static byte[] Point(string base64Url, string what)
    {
        var bytes = Codec.FromBase64Url(base64Url, PointBytes, what);
        return IsPoint(bytes) ? bytes : throw new RosterFormatException(Refusal.Point, what);
    }

    // r and s from strict minimal DER: short-form lengths, two positive INTEGERs with no needless
    // leading zero, nothing after them, and each in [1, n). Null for anything else.
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
        // Negative, or a leading zero the next byte does not need.
        if ((content[0] & 0x80) != 0 || (length > 1 && content[0] == 0 && (content[1] & 0x80) == 0)) { return null; }
        at += 2 + length;
        return Unsigned(content);
    }

    public static bool IsLowS(BigInteger s) => s <= HalfN;

    // A roster signature (`sig`, `cosig`, and the signatures of person commands, attestations'
    // keys and vetoes) and every `uv` assertion signature (§3.2): minimal DER with low-S. Signers
    // normalize; verifiers refuse the rest, so nobody without the key can flip (r, s) to (r, n − s)
    // and enlarge an entry that sits at its size limit.
    public static void CheckRosterSignature(ReadOnlySpan<byte> der, string what)
    {
        if (ReadDer(der) is not { } rs) { throw new RosterFormatException(Refusal.Der, what); }
        if (!IsLowS(rs.S)) { throw new RosterFormatException(Refusal.HighS, what); }
    }


    public static byte[] Der(BigInteger r, BigInteger s)
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

    // WebCrypto's and CryptoKit's raw r ‖ s (64 bytes) as the minimal low-S DER a roster signature is.
    public static byte[] FromRaw(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != 64) { throw new ArgumentException("a raw P-256 signature is 64 bytes", nameof(raw)); }
        var r = Unsigned(raw[..32]);
        var s = Unsigned(raw[32..]);
        if (r.IsZero || s.IsZero || r >= N || s >= N) { throw new ArgumentException("r or s out of range", nameof(raw)); }
        return Der(r, IsLowS(s) ? s : N - s);
    }

    // Any valid DER signature as the low-S form of the same signature: what a signer sends.
    public static byte[] Normalize(ReadOnlySpan<byte> der)
    {
        if (ReadDer(der) is not { } rs) { throw new ArgumentException("not a strict DER P-256 signature", nameof(der)); }
        return Der(rs.R, IsLowS(rs.S) ? rs.S : N - rs.S);
    }

    // The same signature with s replaced by n − s: valid under the same key, and high-S when the
    // input was low. For tests and vectors only; nothing sends one.
    internal static byte[] Flip(ReadOnlySpan<byte> der)
    {
        var rs = ReadDer(der) ?? throw new ArgumentException("not a strict DER P-256 signature", nameof(der));
        return Der(rs.R, N - rs.S);
    }

    // ECDSA with SHA-256 over message, under a point already checked by IsPoint. The DER must
    // already have passed CheckRosterSignature.
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
