using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Roster;

// A signer's presence proof (§4.6, decision 3(a)): `key` signs with no assertion, `webauthn` needs
// the signer's own assertion over the role's challenge, and `none` signs nothing. Entries, vetoes
// and person commands all check it here.
public static class PresenceProof
{
    public static string? Check(byte[] body, DeviceDescriptor d, string role, Assertion? uv)
    {
        switch (d.Presence)
        {
            case Presence.Key:
                return uv is null ? null : Invalid.Presence;
            case Presence.WebAuthn when uv is not null:
                try { WebAuthnAssertion.Check(uv, d.WebAuthn!, Presence.Challenge(role, body)); }
                catch (RosterFormatException) { return Invalid.Presence; }
                return null;
            default:
                return Invalid.Presence;
        }
    }
}

// What a veto and a person command share as they travel: `{ "body", "sig", "uv"? }`, with `uv`
// holding only `by`'s assertion, sized over fixed content as an entry other than a removal is.
static class SignedObject
{
    static readonly string[] WrapperFields = ["body", "sig", "uv"];
    static readonly string[] UvFields = ["by"];

    public static (byte[] Body, byte[] Sig, Assertion? Uv) Parse(JsonElement rootElement, string what)
    {
        var root = StrictJson.Object(rootElement, what);
        StrictJson.Members(root, WrapperFields, ["body", "sig"], what);
        var body = Codec.FromBase64Url(StrictJson.String(root.GetProperty("body"), $"{what}.body"), $"{what}.body");
        var sig = Entry.Signature(root.GetProperty("sig"), $"{what}.sig");
        Assertion? uv = null;
        if (root.TryGetProperty("uv", out var u))
        {
            var o = StrictJson.Object(u, $"{what}.uv");
            StrictJson.Members(o, UvFields, [], $"{what}.uv");
            if (!o.TryGetProperty("by", out var by)) { throw new RosterFormatException(Refusal.Value, $"{what}.uv is empty"); }
            uv = Entry.ParseAssertion(by, $"{what}.uv.by");
        }
        var size = body.Length + sig.Length + (uv is null ? 0 : uv.AuthenticatorData.Length + uv.ClientDataJson.Length + uv.Signature.Length);
        if (size > Entry.MaxBytes) { throw new RosterFormatException(Refusal.Size, what); }
        return (body, sig, uv);
    }

    public static void Version(JsonElement root)
    {
        if (!root.TryGetProperty("v", out var v)) { throw new RosterFormatException(Refusal.Missing, "body.v"); }
        if (v.ValueKind != JsonValueKind.Number || v.GetRawText() != "1") { throw new RosterFormatException(Refusal.Version, "body.v"); }
    }

    public static string Time(JsonElement e, string what)
    {
        var at = StrictJson.String(e, what);
        return Times.IsUtc(at) ? at : throw new RosterFormatException(Refusal.Time, what);
    }

    public static string Canonical(byte[] body, byte[] sig, Assertion? uv)
    {
        var b = new StringBuilder();
        b.Append("{\"body\":\"").Append(Codec.Base64Url(body)).Append("\",\"sig\":\"").Append(Codec.Base64Url(sig)).Append('"');
        if (uv is not null)
        {
            b.Append(",\"uv\":{\"by\":");
            Entry.Append(b, uv);
            b.Append('}');
        }
        return b.Append('}').ToString();
    }
}

// A held reset's veto (§5.6): `body` exactly `{ v, op: "veto", account, generation, reset, by, at }`,
// `generation` the current one and `reset` the entry hash of the genesis the relay holds.
public sealed record VetoBody(string Account, string Generation, byte[] Reset, string By, string At);

public sealed class Veto
{
    static readonly string[] Fields = ["v", "op", "account", "generation", "reset", "by", "at"];

    public byte[] BodyBytes { get; }
    public VetoBody Body { get; }
    public byte[] Sig { get; }
    public Assertion? Uv { get; }

    Veto(byte[] bodyBytes, VetoBody body, byte[] sig, Assertion? uv)
    {
        BodyBytes = bodyBytes;
        Body = body;
        Sig = sig;
        Uv = uv;
    }

    // The wrapper's UTF-8 bytes as received. Throws RosterFormatException with the first defect.
    public static Veto Parse(byte[] wrapper)
    {
        using var doc = StrictJson.Parse(wrapper, "veto");
        var (body, sig, uv) = SignedObject.Parse(doc.RootElement, "veto");
        return new Veto(body, ParseBody(body), sig, uv);
    }

    static VetoBody ParseBody(byte[] bytes)
    {
        using var doc = StrictJson.Parse(bytes, "body");
        var root = StrictJson.Object(doc.RootElement, "body");
        SignedObject.Version(root);
        if (!root.TryGetProperty("op", out var op)) { throw new RosterFormatException(Refusal.Missing, "body.op"); }
        if (StrictJson.String(op, "body.op") != "veto") { throw new RosterFormatException(Refusal.Value, "body.op"); }
        StrictJson.Members(root, Fields, Fields, "body");
        var account = EntryBody.ParseAccount(StrictJson.String(root.GetProperty("account"), "body.account"));
        var generation = StrictJson.String(root.GetProperty("generation"), "body.generation");
        if (!Codec.IsId(generation)) { throw new RosterFormatException(Refusal.Id, "body.generation"); }
        var reset = Codec.FromBase64Url(StrictJson.String(root.GetProperty("reset"), "body.reset"), 32, "body.reset");
        var by = EntryBody.DeviceId(root.GetProperty("by"), "body.by");
        var at = SignedObject.Time(root.GetProperty("at"), "body.at");
        return new VetoBody(account, generation, reset, by, at);
    }

    // What `by` signs: F("agent-bridge reset veto v1", bodyBytes).
    public static byte[] Signed(byte[] body) => Framing.F(Framing.Label(Framing.VetoLabel), body);

    public static string Canonical(byte[] body, byte[] sig, Assertion? uv) => SignedObject.Canonical(body, sig, uv);

    // Whether the relay takes this veto of the reset whose genesis it holds (`held`): null if so,
    // else the code. `current` is its copy of the current generation; any active person's device
    // there may veto, on probation or not.
    public static string? Check(Veto veto, RosterVerifier current, byte[] held)
    {
        var b = veto.Body;
        if (b.Account != current.Account) { return Invalid.Account; }
        if (b.Generation != current.Generation) { return Invalid.Generation; }
        if (!b.Reset.AsSpan().SequenceEqual(held)) { return Invalid.Reset; }
        if (current.State.ActiveAdd(b.By) is not { } add || !current.Graph[add].Body.Device!.IsPerson) { return Invalid.Signer; }
        var d = current.Graph[add].Body.Device!;
        if (!P256.Verify(d.Sign!, Signed(veto.BodyBytes), veto.Sig)) { return Invalid.Signature; }
        return PresenceProof.Check(veto.BodyBytes, d, Presence.RoleVeto, veto.Uv);
    }
}

// The person-only actions a person command carries (§7.2).
public static class CommandAction
{
    public const string Transfer = "transfer";
    public const string FolderTrustAnswer = "folder-trust-answer";
    public const string Update = "update";
    public const string Forget = "forget";
    public const string Attest = "attest";

    // The members each action has besides the common ones, in the body and in its target. Every
    // member an action has is required, and no other is allowed.
    internal static (string[] Body, string[] Target)? Members(string action) => action switch
    {
        Transfer => (["to"], ["session"]),
        FolderTrustAnswer => (["choice", "nonce"], ["question"]),
        Update => (["nonce"], []),
        Forget => ([], []),
        Attest => (["nonce"], []),
        _ => null,
    };

    public static bool NeedsNonce(string action) => Members(action) is { } m && m.Body.Contains("nonce");
}

public sealed record CommandTarget(string Machine, string? Session, string? Question);

public sealed record PersonCommandBody(
    string Action, string Request, string By, CommandTarget Target, string? To, string? Choice, byte[]? Nonce,
    string IssuedAt, string ExpiresAt, IReadOnlyList<byte[]> Heads);

// What a host is asked to check a person command against. `Now` is the later of the host's wall
// clock and its persisted high-water mark, and `Floor` the floor a local clock reset set; the
// mark, the floor and the durable record of used requests are the host's to keep (R6, R2), not
// the library's. `Nonce` is the one this host gave the channel in `hello`, and `Previous` the heads
// of the last attestation it took from another device, for `attest`.
public sealed record CommandContext(string Host, string Action, Instant Now, Instant? Floor = null, byte[]? Nonce = null, IReadOnlyList<byte[]>? Previous = null);

public sealed partial class PersonCommand
{
    public const int MaxLifetimeSeconds = 600;
    public const int SkewSeconds = 120;
    public const int MaxHeads = 4;
    public const int MaxQuestion = 128;
    public const int MaxChoiceBytes = 256;

    static readonly string[] Common = ["v", "action", "request", "by", "target", "issuedAt", "expiresAt", "heads"];

    public byte[] BodyBytes { get; }
    public PersonCommandBody Body { get; }
    public byte[] Sig { get; }
    public Assertion? Uv { get; }

    PersonCommand(byte[] bodyBytes, PersonCommandBody body, byte[] sig, Assertion? uv)
    {
        BodyBytes = bodyBytes;
        Body = body;
        Sig = sig;
        Uv = uv;
    }

    public static PersonCommand Parse(byte[] wrapper)
    {
        using var doc = StrictJson.Parse(wrapper, "command");
        var (body, sig, uv) = SignedObject.Parse(doc.RootElement, "command");
        return new PersonCommand(body, ParseBody(body), sig, uv);
    }

    static PersonCommandBody ParseBody(byte[] bytes)
    {
        using var doc = StrictJson.Parse(bytes, "body");
        var root = StrictJson.Object(doc.RootElement, "body");
        SignedObject.Version(root);
        if (!root.TryGetProperty("action", out var a)) { throw new RosterFormatException(Refusal.Missing, "body.action"); }
        var action = StrictJson.String(a, "body.action");
        if (CommandAction.Members(action) is not { } extra) { throw new RosterFormatException(Refusal.Value, "body.action"); }
        string[] fields = [.. Common, .. extra.Body];
        StrictJson.Members(root, fields, fields, "body");

        var request = StrictJson.String(root.GetProperty("request"), "body.request");
        if (!Uuid().IsMatch(request)) { throw new RosterFormatException(Refusal.Value, "body.request"); }
        var by = EntryBody.DeviceId(root.GetProperty("by"), "body.by");
        var t = StrictJson.Object(root.GetProperty("target"), "body.target");
        string[] targetFields = ["machine", .. extra.Target];
        StrictJson.Members(t, targetFields, targetFields, "body.target");
        var machine = EntryBody.DeviceId(t.GetProperty("machine"), "body.target.machine");
        string? session = null, question = null;
        if (t.TryGetProperty("session", out var s))
        {
            session = StrictJson.String(s, "body.target.session");
            if (!Uuid().IsMatch(session)) { throw new RosterFormatException(Refusal.Value, "body.target.session"); }
        }
        if (t.TryGetProperty("question", out var q))
        {
            question = StrictJson.String(q, "body.target.question");
            if (question.Length is 0 or > MaxQuestion || !QuestionId().IsMatch(question)) { throw new RosterFormatException(Refusal.Value, "body.target.question"); }
        }
        var to = root.TryGetProperty("to", out var toElement) ? EntryBody.DeviceId(toElement, "body.to") : null;
        string? choice = null;
        if (root.TryGetProperty("choice", out var c))
        {
            choice = StrictJson.String(c, "body.choice");
            if (choice.Length == 0 || StrictJson.Utf8Bytes(choice) > MaxChoiceBytes || StrictJson.HasControl(choice)) { throw new RosterFormatException(Refusal.Value, "body.choice"); }
        }
        var nonce = root.TryGetProperty("nonce", out var n) ? Codec.FromBase64Url(StrictJson.String(n, "body.nonce"), 32, "body.nonce") : null;
        var issuedAt = SignedObject.Time(root.GetProperty("issuedAt"), "body.issuedAt");
        var expiresAt = SignedObject.Time(root.GetProperty("expiresAt"), "body.expiresAt");
        var heads = EntryBody.Hashes(root.GetProperty("heads"), "body.heads", 1, MaxHeads);
        return new PersonCommandBody(action, request, by, new CommandTarget(machine, session, question), to, choice, nonce, issuedAt, expiresAt, heads);
    }

    // What `by` signs: F("agent-bridge person command v1", bodyBytes).
    public static byte[] Signed(byte[] body) => Framing.F(Framing.Label(Framing.PersonCommandLabel), body);

    public static string Canonical(byte[] body, byte[] sig, Assertion? uv) => SignedObject.Canonical(body, sig, uv);

    // The stateless checks of §7.2: null when the host may act (once it has recorded the request
    // durably, which is its own obligation), else the code. In this order, so one defect has one
    // code everywhere.
    public static string? Check(PersonCommand command, RosterVerifier roster, CommandContext context)
    {
        var b = command.Body;
        if (b.Action != context.Action) { return Invalid.Action; }
        if (b.Target.Machine != context.Host) { return Invalid.Machine; }
        if (b.By == b.Target.Machine || b.By == b.To) { return Invalid.Signer; }
        if (roster.State.ActiveAdd(b.By) is not { } add || !roster.Graph[add].Body.Device!.IsPerson) { return Invalid.Signer; }
        if (roster.OnProbation(add) || roster.Held.Contains(b.By)) { return Invalid.Probation; }
        var d = roster.Graph[add].Body.Device!;
        if (!P256.Verify(d.Sign!, Signed(command.BodyBytes), command.Sig)) { return Invalid.Signature; }
        if (PresenceProof.Check(command.BodyBytes, d, Presence.RoleCommand, command.Uv) is { } noPresence) { return noPresence; }

        var issued = Instant.Parse(b.IssuedAt);
        var expires = Instant.Parse(b.ExpiresAt);
        if (expires <= issued || expires > issued.AddSeconds(MaxLifetimeSeconds)) { return Invalid.Lifetime; }
        if (issued > context.Now.AddSeconds(SkewSeconds)) { return Invalid.Future; }
        if (expires < context.Now) { return Invalid.Expired; }
        if (context.Floor is { } floor && issued <= floor) { return Invalid.Floor; }

        if (CommandAction.NeedsNonce(b.Action) && (context.Nonce is not { Length: 32 } nonce || !CryptographicOperations.FixedTimeEquals(nonce, b.Nonce)))
        {
            return Invalid.Nonce;
        }
        if (b.Heads.Any(h => !roster.Holds(h))) { return Invalid.Heads; }
        if (context.Previous is { } previous)
        {
            var heads = b.Heads.Select(h => roster.Graph.Find(h)!.Value).ToList();
            foreach (var p in previous)
            {
                if (roster.Graph.Find(p) is not { } pi || !heads.Any(h => h == pi || roster.Graph.IsAncestor(pi, h))) { return Invalid.Regress; }
            }
        }
        return null;
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex Uuid();

    [GeneratedRegex("^[A-Za-z0-9._:-]+$")]
    private static partial Regex QuestionId();
}

// A head attestation (§6.3): a rostered device's heads, and for a host's attestation to a
// person's device the enrollment adds it has revoked in its applied state, MACed for one host
// with the key a signed command for that host uses. The input begins with its own label, so an
// attestation's MAC never verifies as a signed command's (whose input is the body's JSON bytes)
// nor the reverse (decided by A, Trust lead, 2026-11-04).
//
//   key = HKDF-SHA256(ikm: ECDH(attester, host), salt: "agent-bridge command v1",
//                     info: "<attester device id>|<host device id>", 32 bytes)
//   mac = HMAC-SHA256(key, F("agent-bridge roster head v1", account, generation, heads, host id,
//                            nonce, revoked))
//
// heads and revoked are each one part: their 32-byte hashes in head order, joined; revoked is
// always there, and empty when nothing is.
public static class HeadAttestation
{
    static readonly byte[] Salt = "agent-bridge command v1"u8.ToArray();

    // From the raw ECDH secret (the shared point's x), which the caller's key store computes.
    public static byte[] Key(byte[] sharedSecret, string attester, string host) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, Salt, Encoding.ASCII.GetBytes($"{attester}|{host}"));

    public static byte[] Message(string account, string generation, IReadOnlyList<byte[]> heads, string host, byte[] nonce, IReadOnlyList<byte[]> revoked)
    {
        if (heads.Count == 0) { throw new ArgumentException("an attestation names at least one head", nameof(heads)); }
        if (nonce.Length != 32) { throw new ArgumentException("the nonce is 32 bytes", nameof(nonce)); }
        return Framing.F(Framing.Label(Framing.HeadLabel), Framing.Utf8(account), Framing.Utf8(generation), Joined(heads, nameof(heads)), Framing.Utf8(host), nonce, Joined(revoked, nameof(revoked)));
    }

    public static byte[] Mac(byte[] key, byte[] message) => HMACSHA256.HashData(key, message);

    public static bool Verify(byte[] key, byte[] message, byte[] mac) =>
        mac.Length == 32 && CryptographicOperations.FixedTimeEquals(Mac(key, message), mac);

    // The enrollment adds a verifier's applied state revokes, in head order.
    public static IReadOnlyList<byte[]> Revoked(RosterVerifier v)
    {
        var s = v.State;
        return [.. v.Graph.Nodes.Where(n => n.IsAdd && s.IsRevoked(n.Index)).Select(n => n.Hash).Order(Ids.HeadOrder)];
    }

    static byte[] Joined(IReadOnlyList<byte[]> hashes, string what)
    {
        var joined = new byte[hashes.Count * 32];
        for (var i = 0; i < hashes.Count; i++)
        {
            if (hashes[i].Length != 32) { throw new ArgumentException("an entry hash is 32 bytes", what); }
            if (i > 0 && Ids.HeadOrder.Compare(hashes[i - 1], hashes[i]) >= 0) { throw new ArgumentException("hashes are in head order, each once", what); }
            hashes[i].CopyTo(joined, i * 32);
        }
        return joined;
    }
}
