using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.FeedbackEnvelope;
using NSec.Cryptography;
using Xunit;
using SharedEnvelopeException = AgentBridge.FeedbackEnvelope.FeedbackEnvelopeException;

namespace AgentBridge.FeedbackInbox.Tests;

public sealed class FeedbackEnvelopeSharedTests
{
    const string KeyId = "fixture-v1";

    [Theory]
    [InlineData(1, 4096)]
    [InlineData(4000, 8192)]
    [InlineData(1_048_576, 2_097_152)]
    public void Synthetic_feedback_round_trips_at_bucket_boundaries(int size, int storedSize)
    {
        using var recipient = NewRecipient();
        var plain = RandomNumberGenerator.GetBytes(size);
        var bundleId = RandomNumberGenerator.GetBytes(16);
        var envelope = FeedbackEnvelopeV1.Seal(plain, KeyId, recipient.PublicKey.Export(KeyBlobFormat.RawPublicKey), bundleId);
        var header = FeedbackEnvelopeV1.Parse(envelope);
        Assert.Equal(storedSize, envelope.Length);
        Assert.Equal(KeyId, header.KeyId);
        Assert.Equal(bundleId, header.BundleId.ToArray());
        var privateKey = recipient.Export(KeyBlobFormat.RawPrivateKey);
        try { Assert.Equal(plain, FeedbackEnvelopeV1.Open(envelope, privateKey)); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(6 + 10)]
    [InlineData(6 + 10 + 16)]
    [InlineData(6 + 10 + 16 + 32)]
    [InlineData(6 + 10 + 16 + 32 + 32)]
    [InlineData(6 + 10 + 16 + 32 + 32 + 12)]
    [InlineData(6 + 10 + 16 + 32 + 32 + 12 + 4)]
    public void Authenticated_fields_cannot_be_modified(int offset)
    {
        using var recipient = NewRecipient();
        var envelope = FeedbackEnvelopeV1.Seal("synthetic"u8, KeyId,
            recipient.PublicKey.Export(KeyBlobFormat.RawPublicKey), RandomNumberGenerator.GetBytes(16));
        envelope[offset] ^= 1;
        var privateKey = recipient.Export(KeyBlobFormat.RawPrivateKey);
        try { Assert.Throws<SharedEnvelopeException>(() => FeedbackEnvelopeV1.Open(envelope, privateKey)); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    [Fact]
    public void Wrong_recipient_invalid_public_key_and_padding_are_rejected()
    {
        using var recipient = NewRecipient();
        using var wrong = NewRecipient();
        var bundleId = RandomNumberGenerator.GetBytes(16);
        var envelope = FeedbackEnvelopeV1.Seal("synthetic"u8, KeyId, recipient.PublicKey.Export(KeyBlobFormat.RawPublicKey), bundleId);
        var privateKey = wrong.Export(KeyBlobFormat.RawPrivateKey);
        try { Assert.Throws<SharedEnvelopeException>(() => FeedbackEnvelopeV1.Open(envelope, privateKey)); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        Assert.Throws<SharedEnvelopeException>(() => FeedbackEnvelopeV1.Seal("synthetic"u8, KeyId, new byte[32], bundleId));
        envelope[^1] = 1;
        Assert.Throws<SharedEnvelopeException>(() => FeedbackEnvelopeV1.Parse(envelope));
    }

    [Fact]
    public void Seal_rejects_bad_inputs_and_oversized_feedback()
    {
        using var recipient = NewRecipient();
        var pub = recipient.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var id = new byte[16];
        Assert.Throws<ArgumentException>(() => FeedbackEnvelopeV1.Seal([], KeyId, pub, id));
        Assert.Throws<ArgumentException>(() => FeedbackEnvelopeV1.Seal("x"u8, "bad\n", pub, id));
        Assert.Throws<ArgumentException>(() => FeedbackEnvelopeV1.Seal("x"u8, KeyId, pub, new byte[15]));
        Assert.Throws<ArgumentException>(() => FeedbackEnvelopeV1.Seal(new byte[8 * 1024 * 1024], KeyId, pub, id));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(4000)]
    [InlineData(1_048_576)]
    [Trait("Category", "Process")]
    public async Task Synthetic_vectors_interoperate_with_independent_webcrypto(int size)
    {
        using var recipient = NewRecipient();
        var privateKey = recipient.Export(KeyBlobFormat.RawPrivateKey);
        var publicKey = recipient.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var bundleId = RandomNumberGenerator.GetBytes(16);
        var plaintext = RandomNumberGenerator.GetBytes(size);
        try
        {
            var browser = await RunWebCrypto(new
            {
                mode = "seal", keyId = KeyId, publicKey = Convert.ToBase64String(publicKey),
                bundleId = Convert.ToBase64String(bundleId), plaintext = Convert.ToBase64String(plaintext)
            });
            Assert.Equal(plaintext, FeedbackEnvelopeV1.Open(browser, privateKey));
            var envelope = FeedbackEnvelopeV1.Seal(plaintext, KeyId, publicKey, bundleId);
            var opened = await RunWebCrypto(new
            {
                mode = "open", privateKey = Convert.ToBase64String(privateKey),
                envelope = Convert.ToBase64String(envelope)
            });
            Assert.Equal(plaintext, opened);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    static async Task<byte[]> RunWebCrypto(object fixture)
    {
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "feedback-webcrypto.mjs"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The WebCrypto fixture could not start.");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(fixture));
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) { throw new InvalidOperationException("The synthetic WebCrypto fixture failed."); }
        return Convert.FromBase64String(output);
    }

    internal static Key NewRecipient() => Key.Create(KeyAgreementAlgorithm.X25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
}
