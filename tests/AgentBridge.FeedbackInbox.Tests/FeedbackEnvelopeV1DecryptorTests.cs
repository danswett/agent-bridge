using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.FeedbackInbox;
using NSec.Cryptography;
using Xunit;

namespace AgentBridge.FeedbackInbox.Tests;

public sealed class FeedbackEnvelopeV1DecryptorTests
{
    const string KeyId = "fixture-key";
    internal const string PrivateText = "{\"schema\":\"slipgates-feedback.v1\",\"note\":\"fixture only\"}";
    static readonly byte[] Label = "slipgates feedback envelope v1"u8.ToArray();

    internal static (byte[] Envelope, FakeKeys Keys) CreateFixture()
    {
        var exportable = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        using var recipient = Key.Create(KeyAgreementAlgorithm.X25519, exportable);
        using var ephemeral = Key.Create(KeyAgreementAlgorithm.X25519);
        using var secret = KeyAgreementAlgorithm.X25519.Agree(ephemeral, recipient.PublicKey)!;
        var id = RandomNumberGenerator.GetBytes(16);
        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var context = Label.Concat(Encoding.ASCII.GetBytes(KeyId)).Concat(id).ToArray();
        var aesKey = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(secret, salt, context, 32);
        var plain = Encoding.UTF8.GetBytes(PrivateText);
        var bytes = new byte[4096];
        "SGBF"u8.CopyTo(bytes);
        bytes[4] = 1;
        bytes[5] = (byte)KeyId.Length;
        var at = 6;
        Encoding.ASCII.GetBytes(KeyId).CopyTo(bytes, at); at += KeyId.Length;
        id.CopyTo(bytes, at); at += id.Length;
        ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey).CopyTo(bytes, at); at += 32;
        salt.CopyTo(bytes, at); at += salt.Length;
        nonce.CopyTo(bytes, at); at += nonce.Length;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)(plain.Length + 16)); at += 4;
        using (var aes = new AesGcm(aesKey, 16))
        {
            aes.Encrypt(nonce, plain, bytes.AsSpan(at, plain.Length), bytes.AsSpan(at + plain.Length, 16), context);
        }
        CryptographicOperations.ZeroMemory(aesKey);
        return (bytes, new FakeKeys(recipient.Export(KeyBlobFormat.RawPrivateKey)));
    }

    [Fact]
    public async Task Decrypts_authenticated_feedback_but_keeps_it_unclassified()
    {
        var (bytes, keys) = CreateFixture();
        var result = await new FeedbackEnvelopeV1Decryptor().DecryptAsync(bytes, keys, CancellationToken.None);
        Assert.Null(result.Metadata);
        Assert.Equal(PrivateText, result.PrivateContent);
        Assert.Equal(KeyId, keys.RequestedId);
        Assert.All(keys.Returned!, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(6 + 11)]
    [InlineData(6 + 11 + 16)]
    [InlineData(6 + 11 + 16 + 32)]
    [InlineData(6 + 11 + 16 + 32 + 32)]
    [InlineData(6 + 11 + 16 + 32 + 32 + 12 + 4)]
    public async Task Rejects_tampering_with_authenticated_fields(int offset)
    {
        var (bytes, keys) = CreateFixture();
        bytes[offset] ^= 1;
        await Assert.ThrowsAsync<FeedbackEnvelopeException>(
            () => new FeedbackEnvelopeV1Decryptor().DecryptAsync(bytes, keys, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_wrong_recipient_and_invalid_ephemeral_key()
    {
        var (bytes, _) = CreateFixture();
        var (_, wrongKeys) = CreateFixture();
        await Assert.ThrowsAsync<FeedbackEnvelopeException>(
            () => new FeedbackEnvelopeV1Decryptor().DecryptAsync(bytes, wrongKeys, CancellationToken.None));
        var (otherBytes, keys) = CreateFixture();
        otherBytes.AsSpan(6 + KeyId.Length + 16, 32).Clear();
        await Assert.ThrowsAsync<FeedbackEnvelopeException>(
            () => new FeedbackEnvelopeV1Decryptor().DecryptAsync(otherBytes, keys, CancellationToken.None));
    }

    [Fact]
    public async Task Unavailable_key_is_a_transient_failure_not_a_bad_bundle()
    {
        var (bytes, _) = CreateFixture();
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => new FeedbackEnvelopeV1Decryptor().DecryptAsync(bytes, new UnavailableKeys(), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_malformed_shared_envelope_before_requesting_a_key()
    {
        var (bytes, keys) = CreateFixture();
        bytes[0] = 0;
        var error = await Assert.ThrowsAsync<FeedbackEnvelopeException>(
            () => new FeedbackEnvelopeV1Decryptor().DecryptAsync(bytes, keys, CancellationToken.None));
        Assert.Equal("invalid-envelope", error.Code);
        Assert.Null(keys.RequestedId);
    }

    [Fact]
    public async Task Shared_seal_output_is_decrypted_and_invalid_utf8_is_rejected()
    {
        using var recipient = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var publicKey = recipient.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var keys = new FakeKeys(recipient.Export(KeyBlobFormat.RawPrivateKey));
        var id = RandomNumberGenerator.GetBytes(16);
        var envelope = AgentBridge.FeedbackEnvelope.FeedbackEnvelopeV1.Seal(
            Encoding.UTF8.GetBytes(PrivateText), KeyId, publicKey, id);
        var decryptor = new FeedbackEnvelopeV1Decryptor();
        var result = await decryptor.DecryptAsync(envelope, keys, CancellationToken.None);
        Assert.Equal(PrivateText, result.PrivateContent);
        Assert.Null(result.Metadata);
        Assert.All(keys.Returned!, b => Assert.Equal(0, b));

        var invalid = AgentBridge.FeedbackEnvelope.FeedbackEnvelopeV1.Seal([0xff], KeyId, publicKey, id);
        await Assert.ThrowsAsync<FeedbackEnvelopeException>(
            () => decryptor.DecryptAsync(invalid, keys, CancellationToken.None));
        Assert.All(keys.Returned!, b => Assert.Equal(0, b));
    }

    internal sealed class FakeKeys(byte[] key) : IFeedbackPrivateKeyProvider
    {
        public string? RequestedId { get; private set; }
        public byte[]? Returned { get; private set; }

        public Task<FeedbackPrivateKey> GetAsync(string keyId, CancellationToken cancellationToken)
        {
            RequestedId = keyId;
            Returned = key.ToArray();
            return Task.FromResult(new FeedbackPrivateKey(Returned));
        }
    }

    sealed class UnavailableKeys : IFeedbackPrivateKeyProvider
    {
        public Task<FeedbackPrivateKey> GetAsync(string keyId, CancellationToken cancellationToken) =>
            throw new FeedbackInboxTransientException("The recipient key is unavailable.");
    }
}
