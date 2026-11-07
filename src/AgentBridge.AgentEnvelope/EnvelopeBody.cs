using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.AgentEnvelope;

public enum OEligible { No, Yes, Unknown }

public sealed record AgentPrincipal(string Machine, string Add, string Session, string Instance, string Credential, string Agent, string Launcher);

public sealed record ChainSender(string Account, string Machine, string Session);

public sealed record AgentChain(string Root, long RootAt, string? Prev, int Depth, int Messages, IReadOnlyList<ChainSender> Via);

public sealed record Origin(string? Account, string Category, long FirstAt);

// `Origins` null is the single value "unknown" (design §4.3, `provenance`).
public sealed record AgentProvenance(IReadOnlyList<Origin>? Origins, OEligible OEligible, string? Window);

public sealed record AgentTarget(string Account, string Machine, string? Session, string? Instance);

// The body of an envelope (design §4.3). Binary values are held as their base64url text and ids as
// lowercase hex, so the text a signer wrote is the text a verifier reads.
public sealed record EnvelopeBody(
    string Request,
    string Account,
    string Generation,
    IReadOnlyList<string> Heads,
    AgentPrincipal Principal,
    AgentChain Chain,
    AgentProvenance Provenance,
    string Action,
    AgentTarget Target,
    string DataSha256,
    long IssuedAt,
    long ExpiresAt)
{
    public const int Version = 1;
    public const int MaxLifetimeSeconds = 120;
    public const int MaxHeads = 16;
    // The parser's own bound on a chain, whatever the policy's `maxDepth` is, so a hostile envelope
    // can't make a verifier walk or hold an unbounded list. A target still enforces its policy.
    public const int MaxChainDepth = 8;
    public const int MaxMessages = 10_000;
    public const int MaxOrigins = 32;

    // The agent actions an envelope can express (§4.3). Nothing else verifies: not
    // session.answer, launch.trust.answer, session.stop, session.transfer, attachment.*, update.*,
    // push.* or host.*. `sessions.subscribe` is for a person principal across a link (§7.12),
    // which this version of the envelope does not carry.
    public static readonly IReadOnlyList<string> Actions =
    [
        "sessions.status", "session.status", "sessions.list", "session.get", "session.read",
        "session.reply", "launch.options", "session.launch", "launch.result",
    ];

    // Whether an action names a target session and instance: (session, instance), each Absent,
    // Optional or Required.
    internal static (Need Session, Need Instance) Needs(string action) => action switch
    {
        "sessions.status" or "sessions.list" or "launch.options" or "session.launch" => (Need.Absent, Need.Absent),
        "session.status" or "session.get" or "session.read" => (Need.Required, Need.Optional),
        "session.reply" or "launch.result" => (Need.Required, Need.Required),
        _ => throw new EnvelopeException(Refusal.Action, "action"),
    };

    internal enum Need { Absent, Optional, Required }

    // The SHA-256 the signature binds the op's data bytes to. Constant time.
    public bool BindsData(ReadOnlySpan<byte> data)
    {
        if (!Codec.TryBase64Url(DataSha256, out var expected)) { return false; }
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), expected);
    }

    public static string HashOf(ReadOnlySpan<byte> data) => Codec.Base64Url(SHA256.HashData(data));

    // The one canonical body: members in the design's order, no whitespace, absent members
    // omitted. Every string a body holds is plain ASCII with no character JSON escapes, so there
    // is nothing to escape and nothing to choose.
    public byte[] Encode()
    {
        var s = new StringBuilder(512);
        s.Append("{\"v\":1,\"request\":").Append(Q(Request));
        s.Append(",\"account\":").Append(Q(Account));
        s.Append(",\"generation\":").Append(Q(Generation));
        s.Append(",\"heads\":[").AppendJoin(',', Heads.Select(Q)).Append(']');
        s.Append(",\"principal\":{\"kind\":\"agent\",\"machine\":").Append(Q(Principal.Machine));
        s.Append(",\"add\":").Append(Q(Principal.Add));
        s.Append(",\"session\":").Append(Q(Principal.Session));
        s.Append(",\"instance\":").Append(Q(Principal.Instance));
        s.Append(",\"credential\":").Append(Q(Principal.Credential));
        s.Append(",\"agent\":").Append(Q(Principal.Agent));
        s.Append(",\"launcher\":").Append(Q(Principal.Launcher)).Append('}');
        s.Append(",\"chain\":{\"root\":").Append(Q(Chain.Root));
        s.Append(",\"rootAt\":").Append(Chain.RootAt);
        if (Chain.Prev is not null) { s.Append(",\"prev\":").Append(Q(Chain.Prev)); }
        s.Append(",\"depth\":").Append(Chain.Depth);
        s.Append(",\"messages\":").Append(Chain.Messages);
        s.Append(",\"via\":[").AppendJoin(',', Chain.Via.Select(v =>
            $"{{\"account\":{Q(v.Account)},\"machine\":{Q(v.Machine)},\"session\":{Q(v.Session)}}}")).Append("]}");
        s.Append(",\"provenance\":{\"origins\":");
        if (Provenance.Origins is null) { s.Append("\"unknown\""); }
        else
        {
            s.Append('[').AppendJoin(',', Provenance.Origins.Select(o =>
                "{" + (o.Account is null ? "" : $"\"account\":{Q(o.Account)},") + $"\"category\":{Q(o.Category)},\"firstAt\":{o.FirstAt}}}")).Append(']');
        }
        s.Append(",\"oEligible\":").Append(Provenance.OEligible switch { OEligible.Yes => "true", OEligible.No => "false", _ => "\"unknown\"" });
        if (Provenance.Window is not null) { s.Append(",\"window\":").Append(Q(Provenance.Window)); }
        s.Append('}');
        s.Append(",\"action\":").Append(Q(Action));
        s.Append(",\"target\":{\"account\":").Append(Q(Target.Account));
        s.Append(",\"machine\":").Append(Q(Target.Machine));
        if (Target.Session is not null) { s.Append(",\"session\":").Append(Q(Target.Session)); }
        if (Target.Instance is not null) { s.Append(",\"instance\":").Append(Q(Target.Instance)); }
        s.Append('}');
        s.Append(",\"dataSha256\":").Append(Q(DataSha256));
        s.Append(",\"issuedAt\":").Append(IssuedAt);
        s.Append(",\"expiresAt\":").Append(ExpiresAt).Append('}');
        return Encoding.UTF8.GetBytes(s.ToString());
    }

    static string Q(string text) => "\"" + text + "\"";

    // The body in `bytes`, read strictly. It is refused unless `bytes` are exactly the body's
    // canonical encoding: a second spelling of the same body (member order, whitespace, escapes) is
    // a second set of signed bytes for one command, which §4.4's "same request, different bytes is
    // replayed" rule must not have to tell apart.
    public static EnvelopeBody Parse(ReadOnlyMemory<byte> bytes)
    {
        using var doc = StrictJson.Parse(bytes, "body");
        var body = Read(doc.RootElement);
        if (!body.Encode().AsSpan().SequenceEqual(bytes.Span)) { throw new EnvelopeException(Refusal.NonCanonical, "body"); }
        return body;
    }

    static readonly string[] Top =
        ["v", "request", "account", "generation", "heads", "principal", "chain", "provenance", "action", "target", "dataSha256", "issuedAt", "expiresAt"];

    static EnvelopeBody Read(JsonElement root)
    {
        var o = StrictJson.Object(root, "body");
        StrictJson.Members(o, Top, Top, "body");

        if (StrictJson.Integer(o.GetProperty("v"), "v") != Version) { throw new EnvelopeException(Refusal.Version, "v"); }
        var request = Uuid(o, "request");
        var account = Id(o, "account", "account");
        var generation = Id(o, "generation", "generation");

        var heads = new List<string>();
        foreach (var h in StrictJson.Array(o.GetProperty("heads"), "heads"))
        {
            var text = StrictJson.String(h, "heads");
            Codec.CheckBytes(text, 32, "heads");
            heads.Add(text);
        }
        if (heads.Count is 0 or > MaxHeads) { throw new EnvelopeException(Refusal.Length, "heads"); }

        var principal = ReadPrincipal(o.GetProperty("principal"));
        var chain = ReadChain(o.GetProperty("chain"));
        var provenance = ReadProvenance(o.GetProperty("provenance"));

        var action = StrictJson.String(o.GetProperty("action"), "action");
        var needs = Needs(action);
        var target = ReadTarget(o.GetProperty("target"), needs);

        var dataSha256 = StrictJson.String(o.GetProperty("dataSha256"), "dataSha256");
        Codec.CheckBytes(dataSha256, 32, "dataSha256");

        var issuedAt = StrictJson.Integer(o.GetProperty("issuedAt"), "issuedAt");
        var expiresAt = StrictJson.Integer(o.GetProperty("expiresAt"), "expiresAt");
        if (expiresAt <= issuedAt || expiresAt - issuedAt > MaxLifetimeSeconds) { throw new EnvelopeException(Refusal.Lifetime, "expiresAt"); }

        // A first message's chain is its own: it names itself as the root, holds its sender alone,
        // and has no predecessor. Any other message is driven by an envelope (`prev`) of a
        // conversation that began earlier.
        if (chain.Prev is null && (chain.Root != request || chain.Messages != 1 || chain.Via.Count != 1)) { throw new EnvelopeException(Refusal.Chain, "chain"); }
        if (chain.Prev is not null && chain.Root == request) { throw new EnvelopeException(Refusal.Chain, "chain.root"); }
        var sender = chain.Via[^1];
        if (sender.Account != account || sender.Machine != principal.Machine || sender.Session != principal.Session) { throw new EnvelopeException(Refusal.Chain, "chain.via"); }

        return new EnvelopeBody(request, account, generation, heads, principal, chain, provenance, action, target, dataSha256, issuedAt, expiresAt);
    }

    static AgentPrincipal ReadPrincipal(JsonElement e)
    {
        var o = StrictJson.Object(e, "principal");
        string[] names = ["kind", "machine", "add", "session", "instance", "credential", "agent", "launcher"];
        StrictJson.Members(o, names, names, "principal");
        // Connectors and people sign nothing of this kind: a person principal is for §7.12's
        // cross-account reads, which this version of the envelope does not carry.
        if (StrictJson.String(o.GetProperty("kind"), "principal.kind") != "agent") { throw new EnvelopeException(Refusal.Value, "principal.kind"); }
        var machine = Id(o, "machine", "principal.machine");
        var add = Bytes(o, "add", 32, "principal.add");
        var session = Chars(o, "session", 1, 128, SessionChar, "principal.session");
        var instance = Bytes(o, "instance", 12, "principal.instance");
        var credential = Bytes(o, "credential", 16, "principal.credential");
        var agent = Token(o, "agent", "principal.agent");
        var launcher = Token(o, "launcher", "principal.launcher");
        return new AgentPrincipal(machine, add, session, instance, credential, agent, launcher);
    }

    static AgentChain ReadChain(JsonElement e)
    {
        var o = StrictJson.Object(e, "chain");
        StrictJson.Members(o, ["root", "rootAt", "prev", "depth", "messages", "via"], ["root", "rootAt", "depth", "messages", "via"], "chain");
        var root = Uuid(o, "root", "chain.root");
        var rootAt = StrictJson.Integer(o.GetProperty("rootAt"), "chain.rootAt");
        string? prev = null;
        if (o.TryGetProperty("prev", out var p))
        {
            prev = StrictJson.String(p, "chain.prev");
            Codec.CheckBytes(prev, 32, "chain.prev");
        }
        var depth = StrictJson.Integer(o.GetProperty("depth"), "chain.depth");
        var messages = StrictJson.Integer(o.GetProperty("messages"), "chain.messages");
        if (depth is < 1 or > MaxChainDepth) { throw new EnvelopeException(Refusal.Value, "chain.depth"); }
        if (messages is < 1 or > MaxMessages) { throw new EnvelopeException(Refusal.Value, "chain.messages"); }

        var via = new List<ChainSender>();
        foreach (var item in StrictJson.Array(o.GetProperty("via"), "chain.via"))
        {
            var v = StrictJson.Object(item, "chain.via");
            string[] names = ["account", "machine", "session"];
            StrictJson.Members(v, names, names, "chain.via");
            via.Add(new ChainSender(Id(v, "account", "chain.via.account"), Id(v, "machine", "chain.via.machine"),
                Chars(v, "session", 1, 128, SessionChar, "chain.via.session")));
        }
        // `via` is the distinct sending sessions, this sender included, so it never outgrows
        // `depth` (which also counts the target) and never repeats a session.
        if (via.Count < 1 || via.Count > depth) { throw new EnvelopeException(Refusal.Chain, "chain.via"); }
        if (via.Select(v => (v.Account, v.Machine, v.Session)).Distinct().Count() != via.Count) { throw new EnvelopeException(Refusal.Chain, "chain.via"); }
        return new AgentChain(root, rootAt, prev, (int)depth, (int)messages, via);
    }

    static AgentProvenance ReadProvenance(JsonElement e)
    {
        var o = StrictJson.Object(e, "provenance");
        StrictJson.Members(o, ["origins", "oEligible", "window"], ["origins", "oEligible"], "provenance");

        List<Origin>? origins = null;
        var originsElement = o.GetProperty("origins");
        if (originsElement.ValueKind == JsonValueKind.String)
        {
            if (originsElement.GetString() != "unknown") { throw new EnvelopeException(Refusal.Value, "provenance.origins"); }
        }
        else
        {
            origins = [];
            foreach (var item in StrictJson.Array(originsElement, "provenance.origins"))
            {
                var v = StrictJson.Object(item, "provenance.origins");
                StrictJson.Members(v, ["account", "category", "firstAt"], ["category", "firstAt"], "provenance.origins");
                var account = v.TryGetProperty("account", out _) ? Id(v, "account", "provenance.origins.account") : null;
                var category = Chars(v, "category", 1, 64, CategoryChar, "provenance.origins.category");
                if (!(category[0] is >= 'a' and <= 'z')) { throw new EnvelopeException(Refusal.Value, "provenance.origins.category"); }
                origins.Add(new Origin(account, category, StrictJson.Integer(v.GetProperty("firstAt"), "provenance.origins.firstAt")));
            }
            if (origins.Count > MaxOrigins) { throw new EnvelopeException(Refusal.Length, "provenance.origins"); }
        }

        var eligible = o.GetProperty("oEligible");
        var oEligible = eligible.ValueKind switch
        {
            JsonValueKind.True => OEligible.Yes,
            JsonValueKind.False => OEligible.No,
            JsonValueKind.String when eligible.GetString() == "unknown" => OEligible.Unknown,
            JsonValueKind.String => throw new EnvelopeException(Refusal.Value, "provenance.oEligible"),
            _ => throw new EnvelopeException(Refusal.Type, "provenance.oEligible"),
        };
        var window = o.TryGetProperty("window", out _) ? Chars(o, "window", 1, 64, WindowChar, "provenance.window") : null;
        return new AgentProvenance(origins, oEligible, window);
    }

    static AgentTarget ReadTarget(JsonElement e, (Need Session, Need Instance) needs)
    {
        var o = StrictJson.Object(e, "target");
        // `link` and `repo` belong to a command across accounts (§4.3), which v1 does not carry
        // (§7.9): they are unknown members here, so an envelope that names a link can never be
        // mistaken for a same-account one.
        StrictJson.Members(o, ["account", "machine", "session", "instance"], ["account", "machine"], "target");
        var account = Id(o, "account", "target.account");
        var machine = Id(o, "machine", "target.machine");
        string? session = null, instance = null;
        if (o.TryGetProperty("session", out _)) { session = Chars(o, "session", 1, 128, SessionChar, "target.session"); }
        if (o.TryGetProperty("instance", out _)) { instance = Bytes(o, "instance", 12, "target.instance"); }
        if ((needs.Session == Need.Required) != (session is not null) && needs.Session != Need.Optional) { throw new EnvelopeException(Refusal.Target, "target.session"); }
        if (needs.Instance == Need.Absent && instance is not null || needs.Instance == Need.Required && instance is null) { throw new EnvelopeException(Refusal.Target, "target.instance"); }
        if (instance is not null && session is null) { throw new EnvelopeException(Refusal.Target, "target.instance"); }
        return new AgentTarget(account, machine, session, instance);
    }

    static bool SessionChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-';
    static bool CategoryChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-';
    static bool WindowChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
    static bool TokenChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';

    static string Chars(JsonElement o, string name, int min, int max, Func<char, bool> allowed, string where)
    {
        var text = StrictJson.String(o.GetProperty(name), where);
        if (text.Length < min || text.Length > max) { throw new EnvelopeException(Refusal.Length, where); }
        foreach (var c in text)
        {
            if (!allowed(c)) { throw new EnvelopeException(Refusal.Value, where); }
        }
        return text;
    }

    static string Token(JsonElement o, string name, string where)
    {
        var text = Chars(o, name, 1, 32, TokenChar, where);
        return text[0] is >= 'a' and <= 'z' ? text : throw new EnvelopeException(Refusal.Value, where);
    }

    static string Id(JsonElement o, string name, string where)
    {
        var text = StrictJson.String(o.GetProperty(name), where);
        return Codec.IsId(text) ? text : throw new EnvelopeException(Refusal.Value, where);
    }

    static string Uuid(JsonElement o, string name, string? where = null)
    {
        where ??= name;
        var text = StrictJson.String(o.GetProperty(name), where);
        return Guid.TryParseExact(text, "D", out var g) && g.ToString("D") == text ? text : throw new EnvelopeException(Refusal.Value, where);
    }

    static string Bytes(JsonElement o, string name, int length, string where)
    {
        var text = StrictJson.String(o.GetProperty(name), where);
        Codec.CheckBytes(text, length, where);
        return text;
    }
}
