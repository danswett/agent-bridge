using AgentBridge.Roster;

namespace AgentBridge.Host;

internal sealed record VerifierSupportMember(string Id, byte[] Add, string Kind, byte[]? Key);
internal interface IVerifierSupportRoster
{
    string Account { get; }
    string Generation { get; }
    byte[] Genesis { get; }
    IReadOnlyList<byte[]> Heads { get; }
    IReadOnlyList<VerifierSupportMember> Members { get; }
    bool Covers(IReadOnlyList<byte[]> heads);
}
internal sealed record VerifierSupportContext(string Host, PersonCommand Command, byte[] Nonce,
    Instant IssuedAt, Instant ExpiresAt, Instant Now, Instant? HighWater = null, Instant? Floor = null);

internal sealed class RosterVerifierSupportView(RosterVerifier roster) : IVerifierSupportRoster
{
    public string Account => roster.Account;
    public string Generation => roster.Generation;
    public byte[] Genesis => [];
    public IReadOnlyList<byte[]> Heads => [];
    public IReadOnlyList<VerifierSupportMember> Members => [];
    public bool Covers(IReadOnlyList<byte[]> heads) => false;
}

// Fail-closed scaffold for the admitted tests-first head, pending hosted assertion-red proof.
internal static class VerifierSupport
{
    public static byte[] Frontier(IReadOnlyList<byte[]> heads) => [];
    public static byte[] Set(IReadOnlyList<VerifierSupportMember> members) => [];
    public static byte[] Message(byte[] body) => [];
    public static (byte[] Body, byte[] Signature) Parse(byte[] statement) => throw new RosterFormatException(Refusal.Value, "unsupported-verifier");
    public static string? Check(byte[] collection, IVerifierSupportRoster roster, VerifierSupportContext context) => "unsupported-verifier";
}
