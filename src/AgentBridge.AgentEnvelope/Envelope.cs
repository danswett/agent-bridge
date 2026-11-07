using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.AgentEnvelope;

// Signs a framed message with a host's hostSign key. A software key and a TPM-held key differ
// only in how they implement this; the envelope never sees the key.
public interface IEnvelopeSigner
{
    // A P-256 ECDSA/SHA-256 signature over `message` as DER.
    byte[] Sign(ReadOnlySpan<byte> message);
}

public sealed class EcdsaEnvelopeSigner(ECDsa key) : IEnvelopeSigner
{
    public byte[] Sign(ReadOnlySpan<byte> message) =>
        key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
}

// An envelope whose shape is checked but whose signature is not yet: nothing in `Body` may be
// acted on until `Verify` returns.
public sealed class UnverifiedEnvelope
{
    internal UnverifiedEnvelope(byte[] bodyBytes, byte[] signature, EnvelopeBody body)
    {
        BodyBytes = bodyBytes;
        Signature = signature;
        Body = body;
    }

    public byte[] BodyBytes { get; }
    public byte[] Signature { get; }
    public EnvelopeBody Body { get; }

    // `hostSignPoint` is the uncompressed P-256 point of the hostSign key that the roster binds to
    // `Body.Principal.Add`; resolving it is the caller's job (roster v2).
    public VerifiedEnvelope Verify(ReadOnlySpan<byte> hostSignPoint)
    {
        if (!P256.IsPoint(hostSignPoint)) { throw new EnvelopeException(Refusal.Point, "hostSign"); }
        if (!P256.Verify(hostSignPoint, Framing.AgentCommand(BodyBytes), Signature)) { throw new EnvelopeException(Refusal.Signature, "sig"); }
        return new VerifiedEnvelope(Body, BodyBytes);
    }
}

public sealed record VerifiedEnvelope(EnvelopeBody Body, byte[] BodyBytes);

public static class Envelope
{
    public const int MaxBytes = 2048;

    public static UnverifiedEnvelope Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxBytes) { throw new EnvelopeException(Refusal.TooLarge, "envelope"); }
        using var doc = StrictJson.Parse(bytes, "envelope");
        var o = StrictJson.Object(doc.RootElement, "envelope");
        StrictJson.Members(o, ["body", "sig"], ["body", "sig"], "envelope");

        var bodyText = StrictJson.String(o.GetProperty("body"), "body");
        var sigText = StrictJson.String(o.GetProperty("sig"), "sig");
        if (!Codec.TryBase64Url(bodyText, out var bodyBytes)) { throw new EnvelopeException(Refusal.Base64, "body"); }
        if (!Codec.TryBase64Url(sigText, out var sig)) { throw new EnvelopeException(Refusal.Base64, "sig"); }

        // The outer object is itself canonical, so one envelope has one spelling.
        if (!Encode(bodyBytes, sig).AsSpan().SequenceEqual(bytes.Span)) { throw new EnvelopeException(Refusal.NonCanonical, "envelope"); }

        P256.CheckSignatureShape(sig, "sig");
        var body = EnvelopeBody.Parse(bodyBytes);
        return new UnverifiedEnvelope(bodyBytes, sig, body);
    }

    public static byte[] Encode(ReadOnlySpan<byte> bodyBytes, ReadOnlySpan<byte> sig) =>
        Encoding.UTF8.GetBytes($"{{\"body\":\"{Codec.Base64Url(bodyBytes)}\",\"sig\":\"{Codec.Base64Url(sig)}\"}}");

    public static byte[] Seal(EnvelopeBody body, IEnvelopeSigner signer)
    {
        var bodyBytes = body.Encode();
        // Encode does not escape (every valid field is plain ASCII), so a body that is not valid
        // would sign to bytes no verifier accepts, or to a different structure. Read it back first.
        EnvelopeBody.Parse(bodyBytes);
        var sig = P256.Normalize(signer.Sign(Framing.AgentCommand(bodyBytes)));
        var envelope = Encode(bodyBytes, sig);
        if (envelope.Length > MaxBytes) { throw new EnvelopeException(Refusal.TooLarge, "envelope"); }
        return envelope;
    }
}
