using System.Security.Cryptography;
using System.Text.Json;
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
    public byte[] Genesis => roster.Graph.Count == 0 ? [] : roster.Graph[0].Hash.ToArray();
    public IReadOnlyList<byte[]> Heads => roster.Heads.Select(h => h.ToArray()).ToArray();
    public IReadOnlyList<VerifierSupportMember> Members => roster.State.Active.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
    {
        var node = roster.Graph[p.Value]; var device = node.Body.Device!;
        return new VerifierSupportMember(p.Key, node.Hash.ToArray(), device.Kind,
            device.IsPerson || roster.SignerKeyCollides(p.Value) ? null : device.HostSign?.ToArray());
    }).ToArray();
    public bool Covers(IReadOnlyList<byte[]> heads) => heads.All(roster.Holds) && Heads.All(h =>
        roster.Graph.Find(h) is { } parent && heads.Any(n => roster.Graph.Find(n) is { } child && (parent == child || roster.Graph.IsAncestor(parent, child))));
}

// The view and retained challenge are local trust inputs, never claims reconstructed from wire.
// The caller must serialize its final recheck with roster updates; this grants no command authority.
internal static class VerifierSupport
{
    public const int MaxCollectionBytes = 129 * 1024, MaxStatementBytes = 2048, MaxStatements = 64;
    const string Unsupported = "unsupported-verifier";
    static readonly string[] Fields = ["v", "account", "generation", "genesis", "by", "add", "purpose", "key", "versions", "frontier", "verifiers", "target", "request", "consent", "nonce", "issuedAt", "expiresAt"];
    static readonly string[] Hashes = ["genesis", "add", "key", "frontier", "verifiers", "consent", "nonce"];
    static string Text(JsonElement body, string field) => StrictJson.String(body.GetProperty(field), field);
    static string B(byte[] bytes) => Codec.Base64Url(bytes);
    static string Purpose(VerifierSupportMember member) => member.Kind == RosterKinds.Host ? "hostSign" : "capSign";
    static RosterFormatException Bad(string code) => new(code, "verifier-support");

    public static byte[] Frontier(IReadOnlyList<byte[]> heads)
    {
        if (heads.Count is < 1 or > 4) { throw Bad(Refusal.Count); }
        for (var i = 0; i < heads.Count; i++)
        {
            if (heads[i].Length != 32) { throw Bad(Refusal.Length); }
            if (i > 0 && Ids.HeadOrder.Compare(heads[i - 1], heads[i]) >= 0) { throw Bad(Refusal.Order); }
        }
        return Framing.Sha256(Framing.F([Framing.Label("agent-bridge verifier frontier v1"), .. heads]));
    }
    public static byte[] Set(IReadOnlyList<VerifierSupportMember> members)
    {
        if (members.Count is < 1 or > MaxStatements) { throw Bad(Refusal.Count); }
        var ordered = members.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        foreach (var m in ordered)
        {
            if (!Codec.IsId(m.Id) || m.Add.Length != 32 || !RosterKinds.IsKnown(m.Kind) || m.Key is null || !P256.IsPoint(m.Key)) { throw Bad(Refusal.Value); }
        }
        if (ordered.Select(m => m.Id).Distinct().Count() != ordered.Length || ordered.Select(m => B(m.Key!)).Distinct().Count() != ordered.Length) { throw Bad(Refusal.Value); }
        return Framing.Sha256(Framing.F([Framing.Label("agent-bridge verifier set v1"), .. ordered.Select(m =>
            Framing.F(Convert.FromHexString(m.Id), m.Add, Framing.Label(m.Kind), Framing.Label(Purpose(m)), m.Key!))]));
    }
    public static byte[] Message(byte[] body) => Framing.F(Framing.Label("agent-bridge verifier support v1"), body);

    public static (byte[] Body, byte[] Signature) Parse(byte[] statement)
    {
        if (statement.Length > MaxStatementBytes) { throw Bad(Refusal.Size); }
        using var wrapper = StrictJson.Parse(statement, "support");
        var root = StrictJson.Object(wrapper.RootElement, "support");
        StrictJson.Members(root, ["body", "sig"], ["body", "sig"], "support");
        var (body, signature, _) = SignedObject.Parse(root, "support");
        using var document = StrictJson.Parse(body, "support.body");
        var b = StrictJson.Object(document.RootElement, "support.body");
        SignedObject.Version(b);
        StrictJson.Members(b, Fields, Fields, "support.body");
        EntryBody.ParseAccount(Text(b, "account"));
        foreach (var field in new[] { "generation", "by", "target" })
        {
            if (!Codec.IsId(Text(b, field))) { throw Bad(Refusal.Id); }
        }
        foreach (var field in Hashes) { Codec.FromBase64Url(Text(b, field), 32, field); }
        if (Text(b, "purpose") is not ("hostSign" or "capSign")) { throw Bad(Refusal.Value); }
        var request = Text(b, "request");
        if (!Guid.TryParseExact(request, "D", out var uuid) || uuid.ToString("D") != request) { throw Bad(Refusal.Value); }
        var versions = b.GetProperty("versions");
        if (versions.ValueKind != JsonValueKind.Array) { throw Bad(Refusal.Type); }
        if (versions.GetArrayLength() is < 1 or > 8) { throw Bad(Refusal.Count); }
        var previous = 0;
        foreach (var v in versions.EnumerateArray())
        {
            var value = StrictJson.Integer(v, "versions") ?? throw Bad(Refusal.Value);
            if (value is < 1 or > 255) { throw Bad(Refusal.Value); }
            if (value <= previous) { throw Bad(Refusal.Order); }
            previous = value;
        }
        SignedObject.Time(b.GetProperty("issuedAt"), "issuedAt");
        SignedObject.Time(b.GetProperty("expiresAt"), "expiresAt");
        return (body, signature);
    }

    public static string? Check(byte[] collection, IVerifierSupportRoster roster, VerifierSupportContext context)
    {
        try
        {
            if (collection.Length > MaxCollectionBytes) { return Unsupported; }
            var members = roster.Members.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
            if (members.Length is < 1 or > MaxStatements || members.Any(m => m.Key is null)) { return Unsupported; }
            var set = Set(members); var frontier = Frontier(roster.Heads);
            var command = PersonCommand.Parse(Framing.Utf8(PersonCommand.Canonical(context.Command.BodyBytes, context.Command.Sig, context.Command.Uv)));
            var c = command.Body; var issued = context.IssuedAt; var expires = context.ExpiresAt;
            var now = context.HighWater is { } high && high > context.Now ? high : context.Now;
            if (!members.Any(m => m.Id == context.Host && m.Kind == RosterKinds.Host) || c.Action != CommandAction.AcceptHostSign ||
                c.Target.Machine != context.Host || context.Nonce.Length != 32 || c.Nonce is null || c.Nonce.AsSpan().SequenceEqual(context.Nonce) ||
                expires <= issued || expires > issued.AddSeconds(600) || issued > now.AddSeconds(120) || expires < now ||
                (context.Floor is { } floor && issued <= floor) || issued < Instant.Parse(c.IssuedAt) || expires > Instant.Parse(c.ExpiresAt) ||
                !roster.Covers(c.Heads)) { return Unsupported; }
            var consent = Framing.Sha256(PersonCommand.Signed(command.BodyBytes));
            using var document = StrictJson.Parse(collection, "support.collection");
            var entries = document.RootElement;
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != members.Length) { return Unsupported; }
            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                var (body, signature) = Parse(Framing.Utf8(entry.GetRawText()));
                using var parsed = StrictJson.Parse(body, "support.body");
                var b = parsed.RootElement; var member = members[index++];
                var expected = new Dictionary<string, string> {
                    ["account"] = roster.Account, ["generation"] = roster.Generation, ["genesis"] = B(roster.Genesis),
                    ["by"] = member.Id, ["add"] = B(member.Add), ["purpose"] = Purpose(member), ["key"] = B(Framing.Sha256(member.Key!)),
                    ["frontier"] = B(frontier), ["verifiers"] = B(set), ["target"] = context.Host, ["request"] = c.Request,
                    ["consent"] = B(consent), ["nonce"] = B(context.Nonce) };
                if (expected.Any(p => Text(b, p.Key) != p.Value) || !b.GetProperty("versions").EnumerateArray().Any(v => v.GetInt32() == 2) ||
                    Instant.Parse(Text(b, "issuedAt")) != issued || Instant.Parse(Text(b, "expiresAt")) != expires ||
                    !P256.Verify(member.Key!, Message(body), signature)) { return Unsupported; }
            }
            return null;
        }
        catch (RosterFormatException) { return Unsupported; }
        catch (CryptographicException) { return Unsupported; }
    }
}
