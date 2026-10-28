using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using S2.Noise;

namespace S4;

public static class B64Url
{
    public static string Encode(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/').Replace(" ", "");
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}

// A browser's push subscription: where to send, and the keys only that browser can decrypt with.
public sealed record PushSubscription(string Endpoint, string P256dh, string Auth);

// RFC 8291 (Message Encryption for Web Push) over RFC 8188's aes128gcm, one record. The
// payload is encrypted to the browser's own key, so the push service (Apple, Google,
// Mozilla) and the relay that forwards it see only ciphertext. This is what lets the relay
// send notifications without being able to read them.
public static class WebPushCrypto
{
    public const int RecordSize = 4096;
    // header (86) + plaintext + delimiter (1) + tag (16) must fit one 4096-byte record.
    public const int MaxPlaintext = RecordSize - 86 - 1 - 16;

    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, P256KeyPair? asKey = null, byte[]? salt = null)
    {
        if (plaintext.Length > MaxPlaintext) { throw new ArgumentException($"push payload over {MaxPlaintext} bytes", nameof(plaintext)); }
        if (authSecret.Length != 16) { throw new CryptographicException("auth secret must be 16 bytes"); }
        P256KeyPair.AssertOnCurve(uaPublic);
        using var ephemeral = asKey is null ? P256KeyPair.Generate() : null;
        var server = asKey ?? ephemeral!;
        salt ??= RandomNumberGenerator.GetBytes(16);

        var ecdh = server.Dh(uaPublic);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. server.Public];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        // The last (and only) record ends with the 0x02 delimiter.
        byte[] padded = [.. plaintext, 0x02];
        var cipher = new byte[padded.Length + 16];
        using (var gcm = new AesGcm(cek, 16))
        {
            gcm.Encrypt(nonce, padded, cipher.AsSpan(0, padded.Length), cipher.AsSpan(padded.Length));
        }

        var header = new byte[16 + 4 + 1 + server.Public.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)server.Public.Length;
        server.Public.CopyTo(header, 21);
        return [.. header, .. cipher];
    }

    public static byte[] Encrypt(byte[] plaintext, PushSubscription sub) =>
        Encrypt(plaintext, B64Url.Decode(sub.P256dh), B64Url.Decode(sub.Auth));

    // The receiving side, for tests: what the browser does with the body.
    public static byte[] Decrypt(byte[] body, P256KeyPair uaKey, byte[] authSecret)
    {
        var salt = body[..16];
        var idLen = body[20];
        var asPublic = body[21..(21 + idLen)];
        var ecdh = uaKey.Dh(asPublic);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaKey.Public, .. asPublic];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());
        var cipher = body[(21 + idLen)..];
        var plain = new byte[cipher.Length - 16];
        using var gcm = new AesGcm(cek, 16);
        gcm.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain);
        var end = Array.LastIndexOf(plain, (byte)0x02);
        if (end < 0 || plain[(end + 1)..].Any(b => b != 0)) { throw new CryptographicException("bad padding"); }
        return plain[..end];
    }
}

// RFC 8292 VAPID: the relay proves to the push service which application server it is.
// Subscriptions are bound to this key, so it must stay the same for the relay's lifetime.
public sealed class Vapid(ECDsa key, string subject)
{
    public string PublicKey { get; } = B64Url.Encode([0x04, .. key.ExportParameters(false).Q.X!, .. key.ExportParameters(false).Q.Y!]);

    public static Vapid FromPkcs8(string base64, string subject)
    {
        var k = ECDsa.Create();
        k.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
        return new Vapid(k, subject);
    }

    public string Header(Uri endpoint, DateTimeOffset now)
    {
        var aud = endpoint.GetLeftPart(UriPartial.Authority);
        var h = B64Url.Encode("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8);
        // Short-lived, well inside RFC 8292's 24-hour ceiling; the relay signs one per push.
        var p = B64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { aud, exp = (now + TimeSpan.FromMinutes(55)).ToUnixTimeSeconds(), sub = subject }));
        var sig = key.SignData(Encoding.ASCII.GetBytes($"{h}.{p}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={h}.{p}.{B64Url.Encode(sig)}, k={PublicKey}";
    }
}
