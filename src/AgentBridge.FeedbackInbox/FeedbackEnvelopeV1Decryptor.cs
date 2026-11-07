using System.Security.Cryptography;
using System.Text;
using SharedEnvelope = AgentBridge.FeedbackEnvelope.FeedbackEnvelopeV1;
using SharedEnvelopeException = AgentBridge.FeedbackEnvelope.FeedbackEnvelopeException;

namespace AgentBridge.FeedbackInbox;

public sealed class FeedbackEnvelopeV1Decryptor : IFeedbackEnvelopeDecryptor
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<DecryptedFeedback> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        IFeedbackPrivateKeyProvider keys,
        CancellationToken cancellationToken)
    {
        string keyId;
        try { keyId = SharedEnvelope.Parse(ciphertext).KeyId; }
        catch (SharedEnvelopeException) { throw new FeedbackEnvelopeException("invalid-envelope"); }
        using var privateKey = await keys.GetAsync(keyId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (privateKey.Bytes.Length != 32)
        {
            throw new InvalidOperationException("The configured feedback recipient key is invalid.");
        }

        byte[] plain;
        try { plain = SharedEnvelope.Open(ciphertext, privateKey.Bytes); }
        catch (SharedEnvelopeException) { throw new FeedbackEnvelopeException("invalid-envelope"); }
        try
        {
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
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
