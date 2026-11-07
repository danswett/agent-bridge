using System.Text.Json;

namespace AgentBridge.Roster;

public enum EntryOp { Genesis, Add, Remove, Rename, Merge, Confirm }

public static class RosterKinds
{
    public const string Host = "host";
    public const string App = "app";
    public const string Pwa = "pwa";
    public const string Phone = "phone";

    // The person's kinds (design §3.2): adding a class later means adding it here, and in
    // pwa/js/roster/entry.js PERSON_KINDS.
    public static bool IsPerson(string kind) => kind is App or Pwa or Phone;

    public static bool IsKnown(string kind) => kind is Host || IsPerson(kind);
}

public static class Presence
{
    public const string Key = "key";
    public const string WebAuthn = "webauthn";
    public const string None = "none";

    public const string RoleBy = "by";
    public const string RoleCosigner = "cosigner";
    // A veto's and a person command's signer (decided by A, Trust lead, 2026-11-04): roles of their
    // own, so an assertion made for one kind of signed object never stands for another.
    public const string RoleVeto = "veto";
    public const string RoleCommand = "command";

    // The challenge of a presence assertion for one signer's role (§3.2, `uv`):
    // SHA-256(F("agent-bridge presence v1", role, bodyBytes)).
    public static byte[] Challenge(string role, byte[] body) =>
        Framing.Sha256(Framing.F(Framing.Label(Framing.PresenceLabel), Framing.Label(role), body));
}

public sealed record WebAuthnCredential(string RpId, byte[] CredentialId, byte[] Key, bool Attested, bool BackupEligible);

public sealed record DeviceDescriptor(
    string Id, byte[] Dh, byte[]? Sign, string? SignAlg, string? Presence, WebAuthnCredential? WebAuthn,
    string Kind, string Platform, string Name,
    byte[]? HostSign = null, string? HostSignAlg = null, byte[]? HostSignProof = null)
{
    public bool IsPerson => RosterKinds.IsPerson(Kind);
}

public sealed record Approval(string Method, byte[]? Transcript, string? Shown);

public sealed record Target(string Id, byte[] Add);

// An entry's body, decoded and checked by itself (§3.2): everything that needs no other entry.
// Whether it is valid in its parents' state (§3.3) is the acceptance rule's to say.
public sealed record EntryBody(
    EntryOp Op, string Account, string Generation, int Seq, IReadOnlyList<byte[]> Prev, string By, string? Cosigner,
    string At, DeviceDescriptor? Device, Approval? Approval, Target? Target, IReadOnlyList<byte[]> Keep,     string? Name, bool Adopt, int Version = 1)
{
    public const int MaxKeep = 512;
    public const int MaxNameScalars = 64;
    public const int MaxNameBytes = 256;
    public const int MaxAccountBytes = 128;
    public const int MaxRpIdLength = 253;
    public const int MaxCredentialIdBytes = 1023;
    public const string SignAlgorithm = "p256-sha256-der";

    static readonly string[] Order = ["v", "op", "account", "generation", "seq", "prev", "by", "cosigner", "at", "device", "approval", "target", "keep", "name", "adopt"];

    static readonly string[] Common = ["v", "op", "account", "generation", "seq", "by", "at"];

    // Required members per op, and the optional ones it allows besides.
    static (string[] Required, string[] Optional) Fields(EntryOp op) => op switch
    {
        EntryOp.Genesis => ([.. Common, "device", "approval"], []),
        EntryOp.Add => ([.. Common, "prev", "device", "approval"], []),
        EntryOp.Remove => ([.. Common, "prev", "target", "keep"], ["cosigner"]),
        EntryOp.Rename => ([.. Common, "prev", "target", "name"], []),
        EntryOp.Confirm => ([.. Common, "prev", "target"], []),
        EntryOp.Merge => ([.. Common, "prev"], ["adopt"]),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    static EntryOp? ParseOp(string op) => op switch
    {
        "genesis" => EntryOp.Genesis,
        "add" => EntryOp.Add,
        "remove" => EntryOp.Remove,
        "rename" => EntryOp.Rename,
        "merge" => EntryOp.Merge,
        "confirm" => EntryOp.Confirm,
        _ => null,
    };

    public static string OpName(EntryOp op) => op.ToString().ToLowerInvariant();

    // The body's exact bytes, decoded from `body` in its wrapper. Throws RosterFormatException
    // with the first defect, in the order docs/protocol/devices.md gives.
    public static EntryBody Parse(byte[] bodyBytes)
    {
        using var doc = StrictJson.Parse(bodyBytes, "body");
        var root = StrictJson.Object(doc.RootElement, "body");

        if (!root.TryGetProperty("v", out var v)) { throw new RosterFormatException(Refusal.Missing, "body.v"); }
        // Version 2 is a host's add carrying a `hostSign` key (agent-principals.md §8.10, V1).
        if (v.ValueKind != JsonValueKind.Number || v.GetRawText() is not ("1" or "2")) { throw new RosterFormatException(Refusal.Version, "body.v"); }
        var version = v.GetRawText() == "2" ? 2 : 1;
        if (!root.TryGetProperty("op", out var opElement)) { throw new RosterFormatException(Refusal.Missing, "body.op"); }
        var op = ParseOp(StrictJson.String(opElement, "body.op")) ?? throw new RosterFormatException(Refusal.Value, "body.op");
        if (version == 2 && op != EntryOp.Add) { throw new RosterFormatException(Refusal.Version, "body.v: 2 is only for a host's add"); }
        var (required, optional) = Fields(op);
        StrictJson.Members(root, [.. required, .. optional], Order.Where(required.Contains), "body");

        var account = ParseAccount(StrictJson.String(root.GetProperty("account"), "body.account"));
        var generation = StrictJson.String(root.GetProperty("generation"), "body.generation");
        if (!Codec.IsId(generation)) { throw new RosterFormatException(Refusal.Id, "body.generation"); }
        var seq = StrictJson.Integer(root.GetProperty("seq"), "body.seq") ?? throw new RosterFormatException(Refusal.Seq, "body.seq");
        if ((op == EntryOp.Genesis) != (seq == 0)) { throw new RosterFormatException(Refusal.Seq, "body.seq"); }
        IReadOnlyList<byte[]> prev = root.TryGetProperty("prev", out var p) ? Hashes(p, "body.prev", op == EntryOp.Merge ? 2 : 1, op == EntryOp.Merge ? 4 : 1) : [];
        var by = DeviceId(root.GetProperty("by"), "body.by");
        var cosigner = root.TryGetProperty("cosigner", out var c) ? DeviceId(c, "body.cosigner") : null;
        var at = Time(StrictJson.String(root.GetProperty("at"), "body.at"));
        var device = root.TryGetProperty("device", out var d) ? ParseDevice(d, version, account, generation) : null;
        var approval = root.TryGetProperty("approval", out var a) ? ParseApproval(a, op) : null;
        var target = root.TryGetProperty("target", out var t) ? ParseTarget(t) : null;
        IReadOnlyList<byte[]> keep = root.TryGetProperty("keep", out var k) ? Hashes(k, "body.keep", 0, MaxKeep) : [];
        var name = root.TryGetProperty("name", out var n) ? DeviceName(n, "body.name") : null;
        var adopt = false;
        if (root.TryGetProperty("adopt", out var ad) && !(adopt = StrictJson.Bool(ad, "body.adopt"))) { throw new RosterFormatException(Refusal.Value, "body.adopt"); }

        // What the body alone says about its signers (§3.3).
        if (op == EntryOp.Genesis)
        {
            if (!device!.IsPerson) { throw new RosterFormatException(Refusal.Device, "a genesis adds a person's device"); }
            if (by != device.Id) { throw new RosterFormatException(Refusal.Signer, "a genesis is signed by the device it adds"); }
        }
        if (op == EntryOp.Remove)
        {
            if (target!.Id == by && (keep.Count > 0 || cosigner is not null)) { throw new RosterFormatException(Refusal.Signer, "a self-removal keeps nothing and has no cosigner"); }
            if (cosigner is not null && (cosigner == by || cosigner == target.Id)) { throw new RosterFormatException(Refusal.Signer, "a cosigner is neither the signer nor the target"); }
        }
        return new EntryBody(op, account, generation, seq, prev, by, cosigner, at, device, approval, target, keep, name, adopt, version);
    }

    // The relay's account id: 1 to 128 UTF-8 bytes with no control character.
    internal static string ParseAccount(string account) =>
        account.Length > 0 && StrictJson.Utf8Bytes(account) <= MaxAccountBytes && !StrictJson.HasControl(account)
            ? account : throw new RosterFormatException(Refusal.Account, "body.account");

    internal static string DeviceId(JsonElement e, string what)
    {
        var id = StrictJson.String(e, what);
        return Codec.IsId(id) ? id : throw new RosterFormatException(Refusal.Id, what);
    }

    // Entry hashes in head order, ascending by raw bytes and so distinct: prev and keep.
    internal static IReadOnlyList<byte[]> Hashes(JsonElement e, string what, int min, int max)
    {
        if (e.ValueKind != JsonValueKind.Array) { throw new RosterFormatException(Refusal.Type, what); }
        var count = e.GetArrayLength();
        if (count < min || count > max) { throw new RosterFormatException(Refusal.Count, what); }
        var hashes = new List<byte[]>(count);
        foreach (var item in e.EnumerateArray())
        {
            hashes.Add(Codec.FromBase64Url(StrictJson.String(item, what), 32, what));
        }
        for (var i = 1; i < hashes.Count; i++)
        {
            if (Ids.HeadOrder.Compare(hashes[i - 1], hashes[i]) >= 0) { throw new RosterFormatException(Refusal.Order, what); }
        }
        return hashes;
    }

    // RFC 3339 in UTC to the second, with up to nine fraction digits: shown to people, never used
    // for a decision.
    static string Time(string at)
    {
        if (!Times.IsUtc(at)) { throw new RosterFormatException(Refusal.Time, "body.at"); }
        return at;
    }

    static string DeviceName(JsonElement e, string what)
    {
        var name = StrictJson.String(e, what);
        var scalars = StrictJson.UnicodeScalars(name);
        return scalars is >= 1 and <= MaxNameScalars && StrictJson.Utf8Bytes(name) <= MaxNameBytes && !StrictJson.HasControl(name)
            ? name : throw new RosterFormatException(Refusal.Name, what);
    }

    static readonly string[] DeviceOrder = ["id", "dh", "sign", "signAlg", "presence", "webauthn", "kind", "platform", "name"];

    static readonly string[] HostV2Order = ["id", "dh", "hostSign", "hostSignAlg", "hostSignProof", "kind", "platform", "name"];

    static DeviceDescriptor ParseDevice(JsonElement e, int version, string account, string generation)
    {
        var d = StrictJson.Object(e, "body.device");
        if (!d.TryGetProperty("kind", out var kindElement)) { throw new RosterFormatException(Refusal.Missing, "body.device.kind"); }
        var kind = StrictJson.String(kindElement, "body.device.kind");
        if (!RosterKinds.IsKnown(kind)) { throw new RosterFormatException(Refusal.Value, "body.device.kind"); }
        var person = RosterKinds.IsPerson(kind);
        if (version == 2 && person) { throw new RosterFormatException(Refusal.Version, "body.v: 2 is only for a host's add"); }
        string? presence = null;
        if (person)
        {
            if (!d.TryGetProperty("presence", out var pe)) { throw new RosterFormatException(Refusal.Missing, "body.device.presence"); }
            presence = StrictJson.String(pe, "body.device.presence");
            if (presence is not (Presence.Key or Presence.WebAuthn or Presence.None)) { throw new RosterFormatException(Refusal.Value, "body.device.presence"); }
        }
        string[] fields = !person ? (version == 2 ? HostV2Order : ["id", "dh", "kind", "platform", "name"])
            : presence == Presence.WebAuthn ? DeviceOrder
            : ["id", "dh", "sign", "signAlg", "presence", "kind", "platform", "name"];
        StrictJson.Members(d, fields, fields, "body.device");

        var id = DeviceId(d.GetProperty("id"), "body.device.id");
        var dh = P256.Point(StrictJson.String(d.GetProperty("dh"), "body.device.dh"), "body.device.dh");
        if (Ids.Device(dh) != id) { throw new RosterFormatException(Refusal.Device, "body.device.id is not its dh key's"); }
        byte[]? sign = null;
        string? signAlg = null;
        WebAuthnCredential? webauthn = null;
        if (person)
        {
            sign = P256.Point(StrictJson.String(d.GetProperty("sign"), "body.device.sign"), "body.device.sign");
            signAlg = StrictJson.String(d.GetProperty("signAlg"), "body.device.signAlg");
            if (signAlg != SignAlgorithm) { throw new RosterFormatException(Refusal.Value, "body.device.signAlg"); }
            if (presence == Presence.WebAuthn) { webauthn = Credential(d.GetProperty("webauthn")); }
        }
        byte[]? hostSign = null;
        string? hostSignAlg = null;
        byte[]? hostSignProof = null;
        if (version == 2)
        {
            hostSign = P256.Point(StrictJson.String(d.GetProperty("hostSign"), "body.device.hostSign"), "body.device.hostSign");
            hostSignAlg = StrictJson.String(d.GetProperty("hostSignAlg"), "body.device.hostSignAlg");
            if (hostSignAlg != SignAlgorithm) { throw new RosterFormatException(Refusal.Value, "body.device.hostSignAlg"); }
            hostSignProof = Codec.FromBase64Url(StrictJson.String(d.GetProperty("hostSignProof"), "body.device.hostSignProof"), "body.device.hostSignProof");
            P256.CheckRosterSignature(hostSignProof, "body.device.hostSignProof");
        }
        var platform = StrictJson.String(d.GetProperty("platform"), "body.device.platform");
        if (platform is not ("windows" or "macos" or "linux" or "ios" or "ipados" or "android")) { throw new RosterFormatException(Refusal.Value, "body.device.platform"); }
        var name = DeviceName(d.GetProperty("name"), "body.device.name");
        if (hostSign is not null && !P256.Verify(hostSign, HostKey.Binding(account, generation, dh), hostSignProof!)) { throw new RosterFormatException(Refusal.HostProof, "body.device.hostSignProof"); }
        return new DeviceDescriptor(id, dh, sign, signAlg, presence, webauthn, kind, platform, name, hostSign, hostSignAlg, hostSignProof);
    }

    static readonly string[] CredentialFields = ["rpId", "credentialId", "key", "attested", "backupEligible"];

    static WebAuthnCredential Credential(JsonElement e)
    {
        var w = StrictJson.Object(e, "body.device.webauthn");
        StrictJson.Members(w, CredentialFields, CredentialFields, "body.device.webauthn");
        var rpId = StrictJson.String(w.GetProperty("rpId"), "body.device.webauthn.rpId");
        if (!IsRpId(rpId)) { throw new RosterFormatException(Refusal.Value, "body.device.webauthn.rpId"); }
        var credentialId = Codec.FromBase64Url(StrictJson.String(w.GetProperty("credentialId"), "body.device.webauthn.credentialId"), "body.device.webauthn.credentialId");
        if (credentialId.Length is 0 or > MaxCredentialIdBytes) { throw new RosterFormatException(Refusal.Length, "body.device.webauthn.credentialId"); }
        var key = P256.Point(StrictJson.String(w.GetProperty("key"), "body.device.webauthn.key"), "body.device.webauthn.key");
        var attested = StrictJson.Bool(w.GetProperty("attested"), "body.device.webauthn.attested");
        var backupEligible = StrictJson.Bool(w.GetProperty("backupEligible"), "body.device.webauthn.backupEligible");
        return new WebAuthnCredential(rpId, credentialId, key, attested, backupEligible);
    }

    // A lowercase DNS name: labels of letters, digits and inner hyphens, at most 253 characters.
    internal static bool IsRpId(string rpId)
    {
        if (rpId.Length is 0 or > MaxRpIdLength) { return false; }
        foreach (var label in rpId.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') { return false; }
            foreach (var c in label)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) { return false; }
            }
        }
        return true;
    }

    // Exactly one of the member sets of §3.2, by method; the genesis method only on a genesis.
    // Every defect in it is the one refusal, `approval`.
    static Approval ParseApproval(JsonElement e, EntryOp op)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String)
        {
            throw new RosterFormatException(Refusal.Approval, "body.approval.method");
        }
        var method = m.GetString()!;
        string[] fields = method switch
        {
            "code" => ["method", "transcript"],
            "qr" => ["method", "transcript", "shown"],
            "grandfather" or "genesis" => ["method"],
            _ => throw new RosterFormatException(Refusal.Approval, "body.approval.method"),
        };
        if ((op == EntryOp.Genesis) != (method == "genesis")) { throw new RosterFormatException(Refusal.Approval, "body.approval.method"); }
        var count = 0;
        foreach (var member in e.EnumerateObject())
        {
            if (!fields.Contains(member.Name)) { throw new RosterFormatException(Refusal.Approval, $"body.approval.{member.Name}"); }
            count++;
        }
        if (count != fields.Length) { throw new RosterFormatException(Refusal.Approval, "body.approval"); }
        byte[]? transcript = null;
        if (e.TryGetProperty("transcript", out var tr))
        {
            if (tr.ValueKind != JsonValueKind.String || !Codec.TryBase64Url(tr.GetString()!, out transcript) || transcript.Length != 32)
            {
                throw new RosterFormatException(Refusal.Approval, "body.approval.transcript");
            }
        }
        string? shown = null;
        if (e.TryGetProperty("shown", out var s))
        {
            shown = s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (shown is not ("approver" or "joiner")) { throw new RosterFormatException(Refusal.Approval, "body.approval.shown"); }
        }
        return new Approval(method, transcript, shown);
    }

    static readonly string[] TargetFields = ["id", "add"];

    static Target ParseTarget(JsonElement e)
    {
        var t = StrictJson.Object(e, "body.target");
        StrictJson.Members(t, TargetFields, TargetFields, "body.target");
        var id = DeviceId(t.GetProperty("id"), "body.target.id");
        var add = Codec.FromBase64Url(StrictJson.String(t.GetProperty("add"), "body.target.add"), 32, "body.target.add");
        return new Target(id, add);
    }
}

// The roster's `at`: RFC 3339 in UTC, to the second with up to nine fraction digits, on a real
// calendar date. Shown to people only (§3.2).
public static class Times
{
    public static bool IsUtc(string at)
    {
        // yyyy-MM-ddTHH:mm:ss[.f{1,9}]Z
        if (at.Length < 20 || at.Length > 30 || at[^1] != 'Z') { return false; }
        if (at[4] != '-' || at[7] != '-' || at[10] != 'T' || at[13] != ':' || at[16] != ':') { return false; }
        if (!Digits(at, 0, 4, out var year) || !Digits(at, 5, 2, out var month) || !Digits(at, 8, 2, out var day)
            || !Digits(at, 11, 2, out var hour) || !Digits(at, 14, 2, out var minute) || !Digits(at, 17, 2, out var second)) { return false; }
        if (at.Length > 20)
        {
            if (at[19] != '.' || at.Length < 22 || !Digits(at, 20, at.Length - 21, out _)) { return false; }
        }
        if (year < 1 || month is < 1 or > 12 || hour > 23 || minute > 59 || second > 59) { return false; }
        return day >= 1 && day <= DateTime.DaysInMonth(year, month);
    }

    static bool Digits(string s, int start, int count, out int value)
    {
        value = 0;
        for (var i = start; i < start + count; i++)
        {
            if (s[i] is < '0' or > '9') { return false; }
            value = value * 10 + (s[i] - '0');
        }
        return true;
    }
}
