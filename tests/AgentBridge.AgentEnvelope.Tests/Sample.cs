using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.AgentEnvelope;
using Codec = AgentBridge.Roster.Codec;
using Framing = AgentBridge.Roster.Framing;
using P256 = AgentBridge.Roster.P256;

// Shared by the tests and the vector generator: one valid command and the key that signs it.
static class Sample
{
    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] Point(ECDsa key)
    {
        var q = key.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    static string B(int length, byte fill) => Codec.Base64Url(Enumerable.Repeat(fill, length).ToArray());

    public static EnvelopeBody Body(string action = "session.reply", Func<EnvelopeBody, EnvelopeBody>? edit = null)
    {
        var session = "agent-a1";
        var machine = new string('1', 32);
        var body = new EnvelopeBody(
            Request: "3f2b8c1e-5a4d-4e6f-9b7a-1c2d3e4f5a6b",
            Account: new string('a', 32),
            Generation: new string('b', 32),
            Heads: [B(32, 1)],
            Principal: new AgentPrincipal(machine, B(32, 2), session, B(12, 3), B(16, 4), "copilot", "agent-bridge"),
            Chain: new AgentChain("3f2b8c1e-5a4d-4e6f-9b7a-1c2d3e4f5a6b", 1_800_000_000, null, 1, 1, [new ChainSender(new string('a', 32), machine, session)]),
            Provenance: new AgentProvenance([new Origin(null, "user", 1_800_000_000)], OEligible.No, null),
            Action: action,
            Target: action switch
            {
                "sessions.list" or "launch.options" or "session.launch" or "sessions.status" => new AgentTarget(new string('a', 32), new string('2', 32), null, null),
                "session.reply" or "launch.result" => new AgentTarget(new string('a', 32), new string('2', 32), "agent-b2", B(12, 5)),
                _ => new AgentTarget(new string('a', 32), new string('2', 32), "agent-b2", null),
            },
            DataSha256: EnvelopeBody.HashOf(Encoding.UTF8.GetBytes("{\"text\":\"hello\"}")),
            IssuedAt: 1_800_000_000,
            ExpiresAt: 1_800_000_060);
        return edit is null ? body : edit(body);
    }

    // Signs `bodyBytes` as an envelope: over the agent-command framing unless `message` says otherwise.
    public static byte[] Wrap(ECDsa key, byte[] bodyBytes, byte[]? message = null, Func<byte[], byte[]>? sig = null)
    {
        var der = P256.Normalize(key.SignData(message ?? AgentCommandFraming.AgentCommand(bodyBytes), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return Envelope.Encode(bodyBytes, sig is null ? der : sig(der));
    }

    public static string Replace(byte[] body, string from, string to)
    {
        var text = Encoding.UTF8.GetString(body);
        if (!text.Contains(from)) { throw new InvalidOperationException("sample has no " + from); }
        return text.Replace(from, to);
    }
}

public class VectorCase
{
    public required string Name { get; init; }
    public required byte[] Envelope { get; init; }
    public string? RefusedBy { get; init; }
}
