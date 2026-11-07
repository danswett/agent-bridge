using System.Buffers.Binary;
using System.Text;
using AgentBridge.FeedbackInbox;
using Xunit;

namespace AgentBridge.FeedbackInbox.Tests;

public sealed class FeedbackEnvelopeV1Tests
{
    static byte[] Envelope()
    {
        var bytes = new byte[4096];
        "SGBF"u8.CopyTo(bytes);
        bytes[4] = 1;
        bytes[5] = 3;
        "key"u8.CopyTo(bytes.AsSpan(6));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6 + 3 + 16 + 32 + 32 + 12), 16);
        return bytes;
    }

    [Fact]
    public void Reads_the_client_v1_header()
    {
        var parsed = FeedbackEnvelopeV1.Parse(Envelope());
        Assert.Equal("key", parsed.KeyId);
        Assert.Equal(16, parsed.BundleId.Length);
        Assert.Equal(32, parsed.EphemeralPublicKey.Length);
        Assert.Equal(32, parsed.Salt.Length);
        Assert.Equal(12, parsed.Nonce.Length);
        Assert.Equal(16, parsed.Ciphertext.Length);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 2)]
    [InlineData(5, 0)]
    [InlineData(5, 65)]
    [InlineData(6, 32)]
    [InlineData(4095, 1)]
    public void Rejects_invalid_headers_or_nonzero_padding(int offset, byte value)
    {
        var bytes = Envelope();
        bytes[offset] = value;
        Assert.Throws<FeedbackEnvelopeException>(() => FeedbackEnvelopeV1.Parse(bytes));
    }

    [Fact]
    public void Rejects_bad_ciphertext_lengths_and_noncanonical_buckets()
    {
        var bytes = Envelope();
        var offset = 6 + 3 + 16 + 32 + 32 + 12;
        foreach (var length in new uint[] { 0, 15, uint.MaxValue })
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), length);
            Assert.Throws<FeedbackEnvelopeException>(() => FeedbackEnvelopeV1.Parse(bytes));
        }
        Assert.Throws<FeedbackEnvelopeException>(() => FeedbackEnvelopeV1.Parse(new byte[4095]));
        var oversizedBucket = new byte[8192];
        Envelope().CopyTo(oversizedBucket, 0);
        Assert.Throws<FeedbackEnvelopeException>(() => FeedbackEnvelopeV1.Parse(oversizedBucket));
    }
}
