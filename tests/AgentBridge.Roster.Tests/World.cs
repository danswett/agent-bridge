using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Roster;
using static RosterFixture;

// A small language for roster scenarios (§10): named devices with fixed keys, and entries named by
// label, each signed by the device it names on the parents it names. Every key, signature and
// body is deterministic, so a scenario is the same bytes on every run and in the vectors.
public sealed class World
{
    readonly Dictionary<string, Signed> _entries = new(StringComparer.Ordinal);
    readonly List<string> _order = [];
    readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    int _at;

    public TimeSpan Now { get; set; }

    // What the entries made next carry instead of the generation's own account, generation and
    // sequence number, and the presence a named device is described with.
    public string AccountId { get; set; } = Account;
    public string GenerationId { get; set; } = Generation;
    public int SeqOffset { get; set; }
    public Dictionary<string, string> Presences { get; } = new(StringComparer.Ordinal);

    public static (TestKey Dh, TestKey Sign) Keys(string device) => (TestKey.Named($"{device} dh"), TestKey.Named($"{device} sign"));

    public string Id(string device)
    {
        var id = Keys(device).Dh.Device;
        _names[id] = device;
        return id;
    }

    public string NameOf(string id) => _names.TryGetValue(id, out var n) ? n : id;

    public Signed this[string label] => _entries[label];

    public IReadOnlyList<string> Labels => _order;

    public Entry Entry(string label) => AgentBridge.Roster.Entry.Parse(Encoding.UTF8.GetBytes(_entries[label].Wire));

    JsonObject Body(string op, string by, string[] parents)
    {
        var seq = parents.Length == 0 ? 0 : parents.Max(p => JsonNode.Parse(_entries[p].Body)!["seq"]!.GetValue<int>()) + 1;
        var b = new JsonObject { ["v"] = 1, ["op"] = op, ["account"] = AccountId, ["generation"] = GenerationId, ["seq"] = seq + SeqOffset };
        if (parents.Length > 0) { b["prev"] = Hashes(parents.Select(p => _entries[p].Hash)); }
        b["by"] = Id(by);
        b["at"] = $"2026-11-04T10:{_at / 60 % 60:D2}:{_at++ % 60:D2}Z";
        return b;
    }

    // A device's descriptor; `signWith` names another sign key, as a copy carries its maker's.
    public JsonObject Device(string device, string kind = "phone", string? signWith = null, string presence = Presence.Key)
    {
        var (dh, sign) = Keys(device);
        Id(device);
        if (kind == "host") { return HostDevice(dh, "windows", device); }
        return Person(dh, signWith is null ? sign : Keys(signWith).Sign, kind, kind == "pwa" ? "windows" : "ios", device, presence != Presence.Key ? presence : Presences.GetValueOrDefault(device, Presence.Key));
    }

    Signed Put(string label, JsonObject body, string by, string? cosigner = null, string? signAs = null)
    {
        var signed = new Signed(label, Bytes(body), Keys(signAs ?? by).Sign, cosigner is null ? null : Keys(cosigner).Sign, null, null);
        _entries[label] = signed;
        _order.Add(label);
        return signed;
    }

    public Signed Genesis(string device = "G", string label = "G")
    {
        var b = Body("genesis", device, []);
        b["device"] = Device(device);
        b["approval"] = new JsonObject { ["method"] = "genesis" };
        return Put(label, b, device);
    }

    // An add of `device` by `by`. A copy (`copyBy`) carries the device's dh with the copier's own
    // sign key, as a hostile approver's copy would.
    public Signed Add(string label, string by, string device, string parent, string kind = "phone", string? signAs = null, bool copy = false, string presence = Presence.Key, byte[]? transcript = null, string? signWith = null)
    {
        var b = Body("add", by, [parent]);
        b["device"] = Device(device, kind, copy ? by : signWith, presence);
        b["approval"] = new JsonObject { ["method"] = "code", ["transcript"] = Codec.Base64Url(transcript ?? System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(label))) };
        return Put(label, b, by, signAs: signAs);
    }

    // A host's v2 add. Its hostSign is `hostSignOf`'s sign key (its own by default), or that device's dh.
    public Signed AddHostV2(string label, string by, string device, string parent, string? hostSignOf = null, bool hostSignIsDh = false)
    {
        var b = Body("add", by, [parent]);
        b["v"] = 2;
        var k = Keys(hostSignOf ?? device);
        b["device"] = HostDeviceV2(Keys(device).Dh, hostSignIsDh ? k.Dh : k.Sign, "linux", device);
        Id(device);
        b["approval"] = new JsonObject { ["method"] = "code", ["transcript"] = Codec.Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(label))) };
        return Put(label, b, by);
    }

    public Signed Remove(string label, string by, string targetAdd, string parent, string[]? keep = null, string? cosigner = null, string? signAs = null, string? cosignAs = null)
    {
        var b = Body("remove", by, [parent]);
        if (cosigner is not null) { b.Insert(7, "cosigner", Id(cosigner)); }
        b["target"] = Target(targetAdd);
        b["keep"] = Hashes((keep ?? []).Select(k => _entries[k].Hash));
        var signed = new Signed(label, Bytes(b), Keys(signAs ?? by).Sign, cosigner is null ? null : Keys(cosignAs ?? cosigner).Sign, null, null);
        _entries[label] = signed;
        _order.Add(label);
        return signed;
    }

    public Signed Rename(string label, string by, string targetAdd, string parent, string name, string? signAs = null)
    {
        var b = Body("rename", by, [parent]);
        b["target"] = Target(targetAdd);
        b["name"] = name;
        return Put(label, b, by, signAs: signAs);
    }

    public Signed Confirm(string label, string by, string targetAdd, string parent, string? signAs = null)
    {
        var b = Body("confirm", by, [parent]);
        b["target"] = Target(targetAdd);
        return Put(label, b, by, signAs: signAs);
    }

    public Signed Merge(string label, string by, string[] parents, bool adopt = false, string? signAs = null)
    {
        var b = Body("merge", by, parents);
        if (adopt) { b["adopt"] = true; }
        return Put(label, b, by, signAs: signAs);
    }

    JsonObject Target(string addLabel)
    {
        var add = JsonNode.Parse(_entries[addLabel].Body)!;
        return new JsonObject { ["id"] = add["device"]!["id"]!.GetValue<string>(), ["add"] = Codec.Base64Url(_entries[addLabel].Hash) };
    }

    // A verifier fed these entries in this order (all of them by default), on this world's clock.
    public RosterVerifier Verifier(params string[] labels)
    {
        var v = new RosterVerifier(Account, Generation, () => Now);
        Feed(v, labels.Length == 0 ? [.. _order] : labels);
        return v;
    }

    public IReadOnlyList<Offered> Feed(RosterVerifier v, params string[] labels) => v.Offer(labels.Select(Entry));

    // The devices a verifier holds active, by name, in order.
    public string[] Active(RosterVerifier v) => [.. v.State.Active.Keys.Select(NameOf).Order(StringComparer.Ordinal)];

    public string[] Serving(RosterVerifier v) => [.. v.Serving.Keys.Select(NameOf).Order(StringComparer.Ordinal)];
}
