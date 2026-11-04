// Noise_XX_P256_AESGCM_SHA256: the Noise Protocol Framework (revision 34) XX
// handshake, instantiated with NIST P-256 ECDH, AES-256-GCM and SHA-256.
//
// P-256 is not one of the spec's named DH functions, so this defines it:
//   GENERATE_KEYPAIR: a P-256 key pair.
//   DH(priv, pub):    the 32-byte X coordinate of the shared point (raw ECDH, as both
//                     WebCrypto deriveBits(256) and .NET DeriveRawSecretAgreement give).
//   Public keys:      65-byte uncompressed SEC1 encoding (0x04 || X || Y). The spec ties
//                     public key length to DHLEN; here they differ (65 vs 32), and every
//                     length below says which one it means.
// P-256 rather than X25519 because it is the one curve WebCrypto (including Safari),
// .NET's BCL, Windows' TPM provider and Apple's Secure Enclave all support.
//
// Everything else is the spec as written: AESGCM nonce = 4 zero bytes || 64-bit
// big-endian counter, HKDF as Noise defines it (HMAC-SHA256 based), and the XX tokens
//   -> e
//   <- e, ee, s, es
//   -> s, se
//
// and, for an initiator that already holds the responder's static key, IK (IKHandshake below).

using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.Transport;

public static class NoiseConst
{
    public const string ProtocolName = "Noise_XX_P256_AESGCM_SHA256";
    public const int DhLen = 32;
    public const int PubLen = 65;
    public const int HashLen = 32;
    public const int TagLen = 16;
    public const int MaxMessage = 65535;
}

public sealed class P256KeyPair : IDisposable
{
    readonly ECDiffieHellman? key;
    // A key whose private half lives elsewhere (a TPM, spike S6): only its public key is
    // here, and DH is delegated. Everything above this class is unchanged by where the
    // key lives.
    readonly Func<byte[], byte[]>? agree;
    readonly IDisposable? owner;
    public byte[] Public { get; }

    P256KeyPair(ECDiffieHellman key)
    {
        this.key = key;
        var p = key.ExportParameters(false);
        Public = EncodePoint(p.Q.X!, p.Q.Y!);
    }

    P256KeyPair(byte[] pub, Func<byte[], byte[]> agree, IDisposable? owner)
    {
        AssertOnCurve(pub);
        Public = pub;
        this.agree = agree;
        this.owner = owner;
    }

    public static P256KeyPair Generate() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    // `agree` returns the raw 32-byte X coordinate, as DH below does; it is only ever
    // called with a point already checked to be on the curve.
    public static P256KeyPair FromAgreement(byte[] pub, Func<byte[], byte[]> agree, IDisposable? owner = null) => new(pub, agree, owner);

    public bool IsExportable => key != null;

    public static P256KeyPair FromParameters(byte[] d, byte[] x, byte[] y)
    {
        var e = ECDiffieHellman.Create();
        e.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d, Q = new ECPoint { X = x, Y = y } });
        return new P256KeyPair(e);
    }

    public (byte[] D, byte[] X, byte[] Y) Export()
    {
        if (key is null) { throw new CryptographicException("this key's private half cannot leave where it is kept"); }
        var p = key.ExportParameters(true);
        return (p.D!, p.Q.X!, p.Q.Y!);
    }

    public byte[] Dh(byte[] peerPublic)
    {
        AssertOnCurve(peerPublic);
        if (agree != null)
        {
            var z = agree(peerPublic);
            if (z.Length != NoiseConst.DhLen) { throw new CryptographicException($"key agreement returned {z.Length} bytes"); }
            return z;
        }
        using var peer = ECDiffieHellman.Create();
        peer.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = peerPublic[1..33], Y = peerPublic[33..65] },
        });
        return key!.DeriveRawSecretAgreement(peer.PublicKey);
    }

    // Checked explicitly rather than left to the platform's import, which differs by
    // backend (WebKitGTK's WebCrypto accepted an off-curve point that every other
    // implementation refused). ECDH against an unchecked point is the invalid-curve
    // attack on a long-lived static key. P-256's cofactor is 1.
    static readonly System.Numerics.BigInteger P = System.Numerics.BigInteger.Parse("0ffffffff00000001000000000000000000000000ffffffffffffffffffffffff", System.Globalization.NumberStyles.HexNumber);
    static readonly System.Numerics.BigInteger B = System.Numerics.BigInteger.Parse("05ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b", System.Globalization.NumberStyles.HexNumber);

    public static void AssertOnCurve(byte[] pub)
    {
        if (pub.Length != NoiseConst.PubLen || pub[0] != 0x04) { throw new CryptographicException("bad public key encoding"); }
        var x = new System.Numerics.BigInteger(pub.AsSpan(1, 32), isUnsigned: true, isBigEndian: true);
        var y = new System.Numerics.BigInteger(pub.AsSpan(33, 32), isUnsigned: true, isBigEndian: true);
        if (x >= P || y >= P) { throw new CryptographicException("public key coordinate out of range"); }
        var rhs = System.Numerics.BigInteger.Remainder(x * x * x - 3 * x + B, P);
        var diff = ((y * y - rhs) % P + P) % P;
        if (!diff.IsZero) { throw new CryptographicException("public key is not on P-256"); }
    }

    static byte[] EncodePoint(byte[] x, byte[] y)
    {
        var b = new byte[NoiseConst.PubLen];
        b[0] = 0x04;
        x.CopyTo(b, 1);
        y.CopyTo(b, 33);
        return b;
    }

    public void Dispose()
    {
        key?.Dispose();
        owner?.Dispose();
    }
}

public sealed class CipherState
{
    byte[]? k;
    ulong n;
    public bool HasKey => k != null;

    public void InitializeKey(byte[]? key) { k = key; n = 0; }

    static byte[] Nonce(ulong n)
    {
        var nonce = new byte[12];
        for (int i = 0; i < 8; i++) { nonce[4 + i] = (byte)(n >> (56 - 8 * i)); }
        return nonce;
    }

    public byte[] EncryptWithAd(byte[] ad, byte[] plaintext)
    {
        if (k == null) { return plaintext; }
        if (n == ulong.MaxValue) { throw new CryptographicException("nonce exhausted"); }
        using var gcm = new AesGcm(k, NoiseConst.TagLen);
        var output = new byte[plaintext.Length + NoiseConst.TagLen];
        gcm.Encrypt(Nonce(n), plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), ad);
        n++;
        return output;
    }

    public byte[] DecryptWithAd(byte[] ad, byte[] ciphertext)
    {
        if (k == null) { return ciphertext; }
        if (ciphertext.Length < NoiseConst.TagLen) { throw new CryptographicException("short ciphertext"); }
        using var gcm = new AesGcm(k, NoiseConst.TagLen);
        var plain = new byte[ciphertext.Length - NoiseConst.TagLen];
        // On a bad tag this throws and n is not advanced, as the spec requires.
        gcm.Decrypt(Nonce(n), ciphertext.AsSpan(0, plain.Length), ciphertext.AsSpan(plain.Length), plain, ad);
        n++;
        return plain;
    }
}

public sealed class SymmetricState
{
    byte[] ck;
    public byte[] H { get; private set; }
    public CipherState Cipher { get; } = new();

    public SymmetricState(string protocolName)
    {
        var name = Encoding.ASCII.GetBytes(protocolName);
        if (name.Length <= NoiseConst.HashLen)
        {
            H = new byte[NoiseConst.HashLen];
            name.CopyTo(H, 0);
        }
        else { H = SHA256.HashData(name); }
        ck = H;
    }

    public static (byte[], byte[]) Hkdf2(byte[] chainingKey, byte[] ikm)
    {
        var temp = HMACSHA256.HashData(chainingKey, ikm);
        var o1 = HMACSHA256.HashData(temp, new byte[] { 0x01 });
        var o2 = HMACSHA256.HashData(temp, o1.Append((byte)0x02).ToArray());
        return (o1, o2);
    }

    public void MixKey(byte[] ikm)
    {
        (ck, var tempK) = Hkdf2(ck, ikm);
        Cipher.InitializeKey(tempK); // HASHLEN 32 = AES-256 key length, no truncation
    }

    public void MixHash(byte[] data) => H = SHA256.HashData([.. H, .. data]);

    public byte[] EncryptAndHash(byte[] plaintext)
    {
        var c = Cipher.EncryptWithAd(H, plaintext);
        MixHash(c);
        return c;
    }

    public byte[] DecryptAndHash(byte[] ciphertext)
    {
        var p = Cipher.DecryptWithAd(H, ciphertext);
        MixHash(ciphertext);
        return p;
    }

    public (CipherState, CipherState) Split()
    {
        var (k1, k2) = Hkdf2(ck, Array.Empty<byte>());
        var c1 = new CipherState(); c1.InitializeKey(k1);
        var c2 = new CipherState(); c2.InitializeKey(k2);
        return (c1, c2);
    }
}

// What a channel needs of its handshake, whichever pattern it is.
public interface INoiseHandshake
{
    byte[]? RemoteStatic { get; }
    byte[] HandshakeHash { get; }
    bool IsComplete { get; }
    CipherState? Send { get; }
    CipherState? Receive { get; }
    byte[] WriteMessage(byte[] payload);
    byte[] ReadMessage(byte[] message);
}

public sealed class XXHandshake : INoiseHandshake
{
    readonly bool initiator;
    readonly SymmetricState ss;
    readonly P256KeyPair s;
    P256KeyPair? e;
    byte[]? re;
    int step;

    public byte[]? RemoteStatic { get; private set; }
    public byte[] HandshakeHash => ss.H;
    public bool IsComplete => Volatile.Read(ref step) == 3;
    public CipherState? Send { get; private set; }
    public CipherState? Receive { get; private set; }

    public XXHandshake(bool initiator, P256KeyPair staticKey, byte[] prologue, P256KeyPair? fixedEphemeral = null)
    {
        this.initiator = initiator;
        s = staticKey;
        e = fixedEphemeral;
        ss = new SymmetricState(NoiseConst.ProtocolName);
        ss.MixHash(prologue);
    }

    bool MyTurn => initiator ? step % 2 == 0 : step % 2 == 1;

    public byte[] WriteMessage(byte[] payload)
    {
        if (!MyTurn || IsComplete) { throw new InvalidOperationException("not this side's turn"); }
        var buf = new List<byte>();
        switch (step)
        {
            case 0: // -> e
                e ??= P256KeyPair.Generate();
                buf.AddRange(e.Public); ss.MixHash(e.Public);
                break;
            case 1: // <- e, ee, s, es
                e ??= P256KeyPair.Generate();
                buf.AddRange(e.Public); ss.MixHash(e.Public);
                ss.MixKey(e.Dh(re!));
                buf.AddRange(ss.EncryptAndHash(s.Public));
                ss.MixKey(s.Dh(re!));
                break;
            case 2: // -> s, se
                buf.AddRange(ss.EncryptAndHash(s.Public));
                ss.MixKey(s.Dh(re!));
                break;
        }
        buf.AddRange(ss.EncryptAndHash(payload));
        Advance();
        if (buf.Count > NoiseConst.MaxMessage) { throw new InvalidOperationException("message too long"); }
        return [.. buf];
    }

    public byte[] ReadMessage(byte[] message)
    {
        if (MyTurn || IsComplete) { throw new InvalidOperationException("not the peer's turn"); }
        int at = 0;
        byte[] Take(int n)
        {
            if (message.Length - at < n) { throw new CryptographicException("short handshake message"); }
            var r = message[at..(at + n)]; at += n; return r;
        }
        switch (step)
        {
            case 0: // -> e
                re = Take(NoiseConst.PubLen); ss.MixHash(re);
                break;
            case 1: // <- e, ee, s, es
                re = Take(NoiseConst.PubLen); ss.MixHash(re);
                ss.MixKey(e!.Dh(re));
                RemoteStatic = ss.DecryptAndHash(Take(NoiseConst.PubLen + NoiseConst.TagLen));
                ss.MixKey(e.Dh(RemoteStatic));
                break;
            case 2: // -> s, se
                RemoteStatic = ss.DecryptAndHash(Take(NoiseConst.PubLen + NoiseConst.TagLen));
                ss.MixKey(e!.Dh(RemoteStatic));
                break;
        }
        var payload = ss.DecryptAndHash(message[at..]);
        Advance();
        return payload;
    }

    // The cipher states before the step that says they exist: the receive path reads IsComplete
    // and then Receive while another thread may be writing the last message (IKHandshake).
    void Advance()
    {
        if (step == 2)
        {
            var (c1, c2) = ss.Split();
            (Send, Receive) = initiator ? (c1, c2) : (c2, c1);
        }
        Volatile.Write(ref step, step + 1);
    }
}

// Noise_IK_P256_AESGCM_SHA256: the same suite as XX, for an initiator that already holds the
// responder's static key (learned from an earlier XX with it, #603). One round trip instead of
// XX's one and a half, so the responder can answer the initiator's first message in its own:
//   <- s
//   ...
//   -> e, es, s, ss
//   <- e, ee, se
// The first message's payload is encrypted to the responder's static key only: a relay can replay
// it, and its secrecy does not survive that key, so only what is safe to repeat goes in it
// (ChannelHub.EarlyOps). The responder's reply, and everything after, is forward-secret as in XX.
public sealed class IKHandshake : INoiseHandshake
{
    public const string ProtocolName = "Noise_IK_P256_AESGCM_SHA256";

    readonly bool initiator;
    readonly SymmetricState ss;
    readonly P256KeyPair s;
    P256KeyPair? e;
    byte[]? re;
    int step;

    public byte[]? RemoteStatic { get; private set; }
    public byte[] HandshakeHash => ss.H;
    public bool IsComplete => Volatile.Read(ref step) == 2;
    public CipherState? Send { get; private set; }
    public CipherState? Receive { get; private set; }

    // responderStatic: the initiator's copy of the responder's key; a responder passes none.
    public IKHandshake(bool initiator, P256KeyPair staticKey, byte[] prologue, byte[]? responderStatic = null, P256KeyPair? fixedEphemeral = null)
    {
        this.initiator = initiator;
        s = staticKey;
        e = fixedEphemeral;
        if (initiator)
        {
            ArgumentNullException.ThrowIfNull(responderStatic);
            P256KeyPair.AssertOnCurve(responderStatic);
            RemoteStatic = responderStatic;
        }
        ss = new SymmetricState(ProtocolName);
        ss.MixHash(prologue);
        // The pre-message (<- s): both sides hash the responder's static key.
        ss.MixHash(initiator ? responderStatic! : staticKey.Public);
    }

    bool MyTurn => initiator ? step == 0 : step == 1;

    public byte[] WriteMessage(byte[] payload)
    {
        if (!MyTurn || IsComplete) { throw new InvalidOperationException("not this side's turn"); }
        var buf = new List<byte>();
        e ??= P256KeyPair.Generate();
        buf.AddRange(e.Public); ss.MixHash(e.Public);
        if (step == 0)
        {
            // -> e, es, s, ss
            ss.MixKey(e.Dh(RemoteStatic!));
            buf.AddRange(ss.EncryptAndHash(s.Public));
            ss.MixKey(s.Dh(RemoteStatic!));
        }
        else
        {
            // <- e, ee, se
            ss.MixKey(e.Dh(re!));
            ss.MixKey(e.Dh(RemoteStatic!));
        }
        buf.AddRange(ss.EncryptAndHash(payload));
        if (buf.Count > NoiseConst.MaxMessage) { throw new InvalidOperationException("message too long"); }
        Advance();
        return [.. buf];
    }

    public byte[] ReadMessage(byte[] message)
    {
        if (MyTurn || IsComplete) { throw new InvalidOperationException("not the peer's turn"); }
        int at = 0;
        byte[] Take(int n)
        {
            if (message.Length - at < n) { throw new CryptographicException("short handshake message"); }
            var r = message[at..(at + n)]; at += n; return r;
        }
        re = Take(NoiseConst.PubLen); ss.MixHash(re);
        if (step == 0)
        {
            // -> e, es, s, ss
            ss.MixKey(s.Dh(re));
            RemoteStatic = ss.DecryptAndHash(Take(NoiseConst.PubLen + NoiseConst.TagLen));
            ss.MixKey(s.Dh(RemoteStatic));
        }
        else
        {
            // <- e, ee, se
            ss.MixKey(e!.Dh(re));
            ss.MixKey(s.Dh(re));
        }
        var payload = ss.DecryptAndHash(message[at..]);
        Advance();
        return payload;
    }

    // The cipher states before the step that says they exist. A responder writes its reply off the
    // receive path (ChannelHub.AnswerIkAsync), so a frame arriving meanwhile (forged, since only the
    // reply lets the opener encrypt) saw IsComplete and then a null Receive (#653's review).
    void Advance()
    {
        if (step == 1)
        {
            var (c1, c2) = ss.Split();
            (Send, Receive) = initiator ? (c1, c2) : (c2, c1);
        }
        Volatile.Write(ref step, step + 1);
    }
}
