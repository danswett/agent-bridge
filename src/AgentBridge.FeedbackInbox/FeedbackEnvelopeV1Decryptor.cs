using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace AgentBridge.FeedbackInbox;

public sealed class FeedbackEnvelopeV1Decryptor : IFeedbackEnvelopeDecryptor
{
    static readonly byte[] Label = "slipgates feedback envelope v1"u8.ToArray();
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<DecryptedFeedback> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        IFeedbackPrivateKeyProvider keys,
        CancellationToken cancellationToken)
    {
        var envelope = FeedbackEnvelopeV1.Parse(ciphertext);
        using var privateKey = await keys.GetAsync(envelope.KeyId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (privateKey.Bytes.Length != 32)
        {
            throw new InvalidOperationException("The configured feedback recipient key is invalid.");
        }

        using var key = Key.Import(KeyAgreementAlgorithm.X25519, privateKey.Bytes, KeyBlobFormat.RawPrivateKey);
        var peer = PublicKey.Import(KeyAgreementAlgorithm.X25519, envelope.EphemeralPublicKey.Span, KeyBlobFormat.RawPublicKey);
        using var secret = KeyAgreementAlgorithm.X25519.Agree(key, peer)
            ?? throw new FeedbackEnvelopeException("invalid-envelope");

        var keyId = Encoding.ASCII.GetBytes(envelope.KeyId);
        var context = new byte[Label.Length + keyId.Length + envelope.BundleId.Length];
        Label.CopyTo(context, 0);
        keyId.CopyTo(context, Label.Length);
        envelope.BundleId.Span.CopyTo(context.AsSpan(Label.Length + keyId.Length));
        var derivedKey = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(secret, envelope.Salt.Span, context, 32);
        var plain = new byte[envelope.Ciphertext.Length - 16];
        try
        {
            using var aes = new AesGcm(derivedKey, 16);
            try
            {
                aes.Decrypt(
                    envelope.Nonce.Span,
                    envelope.Ciphertext.Span[..^16],
                    envelope.Ciphertext.Span[^16..],
                    plain,
                    context);
            }
            catch (CryptographicException)
            {
                throw new FeedbackEnvelopeException("invalid-envelope");
            }
            try
            {
                // Client v1 has no reviewed public classification vocabulary; keep it private.
                return new DecryptedFeedback(null, StrictUtf8.GetString(plain));
            }
            catch (DecoderFallbackException)
            {
                throw new FeedbackEnvelopeException("invalid-envelope");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
