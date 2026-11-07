using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace AgentBridge.FeedbackEnvelope;

public sealed class FeedbackEnvelopeException(string message) : Exception(message);

public sealed record FeedbackEnvelopeHeader(
    string KeyId, ReadOnlyMemory<byte> BundleId, ReadOnlyMemory<byte> EphemeralPublicKey,
    ReadOnlyMemory<byte> Salt, ReadOnlyMemory<byte> Nonce, ReadOnlyMemory<byte> Ciphertext);

public static class FeedbackEnvelopeV1
{
    const int MinStoredBytes = 4096;
    const int MaxStoredBytes = 8 * 1024 * 1024;
    const int MiB = 1024 * 1024;
    static readonly byte[] Label = "slipgates feedback envelope v1"u8.ToArray();

    public static byte[] Seal(ReadOnlySpan<byte> plaintext, string keyId, ReadOnlySpan<byte> recipientPublicKey, ReadOnlySpan<byte> bundleId)
    {
        if (plaintext.IsEmpty) { throw new ArgumentException("Feedback must not be empty.", nameof(plaintext)); }
        if (!ValidKeyId(keyId)) { throw new ArgumentException("Invalid feedback key ID.", nameof(keyId)); }
        if (recipientPublicKey.Length != 32) { throw new ArgumentException("Recipient public key must be 32 bytes.", nameof(recipientPublicKey)); }
        if (bundleId.Length != 16) { throw new ArgumentException("Bundle ID must be 16 bytes.", nameof(bundleId)); }
        var headerSize = 6 + keyId.Length + 16 + 32 + 32 + 12 + 4;
        if (plaintext.Length > MaxStoredBytes - headerSize - 16)
        {
            throw new ArgumentException("Feedback exceeds the maximum stored size.", nameof(plaintext));
        }

        var recipient = PublicKey.Import(KeyAgreementAlgorithm.X25519, recipientPublicKey, KeyBlobFormat.RawPublicKey);
        using var ephemeral = Key.Create(KeyAgreementAlgorithm.X25519);
        using var secret = KeyAgreementAlgorithm.X25519.Agree(ephemeral, recipient)
            ?? throw new FeedbackEnvelopeException("Invalid recipient public key.");
        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var context = Context(keyId, bundleId);
        var key = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(secret, salt, context, 32);
        try
        {
            var stored = new byte[StoredSize(headerSize + plaintext.Length + 16)];
            "SGBF"u8.CopyTo(stored);
            stored[4] = 1;
            stored[5] = (byte)keyId.Length;
            var at = 6;
            Encoding.ASCII.GetBytes(keyId).CopyTo(stored, at); at += keyId.Length;
            bundleId.CopyTo(stored.AsSpan(at)); at += 16;
            ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey).CopyTo(stored, at); at += 32;
            salt.CopyTo(stored, at); at += salt.Length;
            nonce.CopyTo(stored, at); at += nonce.Length;
            BinaryPrimitives.WriteUInt32BigEndian(stored.AsSpan(at), (uint)(plaintext.Length + 16)); at += 4;
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, stored.AsSpan(at, plaintext.Length), stored.AsSpan(at + plaintext.Length, 16), context);
            return stored;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Open(ReadOnlyMemory<byte> stored, ReadOnlySpan<byte> recipientPrivateKey)
    {
        if (recipientPrivateKey.Length != 32) { throw new ArgumentException("Recipient private key must be 32 bytes.", nameof(recipientPrivateKey)); }
        var envelope = Parse(stored);
        using var recipient = Key.Import(KeyAgreementAlgorithm.X25519, recipientPrivateKey, KeyBlobFormat.RawPrivateKey);
        var ephemeral = PublicKey.Import(KeyAgreementAlgorithm.X25519, envelope.EphemeralPublicKey.Span, KeyBlobFormat.RawPublicKey);
        using var secret = KeyAgreementAlgorithm.X25519.Agree(recipient, ephemeral)
            ?? throw new FeedbackEnvelopeException("Invalid feedback envelope.");
        var context = Context(envelope.KeyId, envelope.BundleId.Span);
        var key = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(secret, envelope.Salt.Span, context, 32);
        var plaintext = new byte[envelope.Ciphertext.Length - 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            try
            {
                aes.Decrypt(envelope.Nonce.Span, envelope.Ciphertext.Span[..^16], envelope.Ciphertext.Span[^16..], plaintext, context);
                return plaintext;
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new FeedbackEnvelopeException("Invalid feedback envelope.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static FeedbackEnvelopeHeader Parse(ReadOnlyMemory<byte> stored)
    {
        var data = stored.Span;
        if (data.Length < MinStoredBytes || data.Length > MaxStoredBytes
            || !data[..4].SequenceEqual("SGBF"u8) || data[4] != 1 || data[5] is < 1 or > 64)
        {
            throw InvalidEnvelope();
        }
        var keyLength = data[5];
        var keyId = Encoding.ASCII.GetString(data.Slice(6, keyLength));
        if (!ValidKeyId(keyId)) { throw InvalidEnvelope(); }
        var at = 6 + keyLength;
        var id = stored.Slice(at, 16); at += 16;
        var ephemeral = stored.Slice(at, 32); at += 32;
        var salt = stored.Slice(at, 32); at += 32;
        var nonce = stored.Slice(at, 12); at += 12;
        var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at, 4)); at += 4;
        if (length < 16 || length > data.Length - at) { throw InvalidEnvelope(); }
        var end = at + (int)length;
        if (StoredSize(end) != data.Length || data[end..].IndexOfAnyExcept((byte)0) >= 0) { throw InvalidEnvelope(); }
        return new FeedbackEnvelopeHeader(keyId, id, ephemeral, salt, nonce, stored.Slice(at, (int)length));
    }

    static bool ValidKeyId(string? keyId) => keyId is { Length: >= 1 and <= 64 }
        && keyId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    static byte[] Context(string keyId, ReadOnlySpan<byte> bundleId)
    {
        var context = new byte[Label.Length + keyId.Length + bundleId.Length];
        Label.CopyTo(context, 0);
        Encoding.ASCII.GetBytes(keyId).CopyTo(context, Label.Length);
        bundleId.CopyTo(context.AsSpan(Label.Length + keyId.Length));
        return context;
    }

    static int StoredSize(int length)
    {
        if (length <= MinStoredBytes) { return MinStoredBytes; }
        if (length > MiB) { return ((length + MiB - 1) / MiB) * MiB; }
        var size = MinStoredBytes;
        while (size < length) { size *= 2; }
        return size;
    }

    static FeedbackEnvelopeException InvalidEnvelope() => new("Invalid feedback envelope.");
}
