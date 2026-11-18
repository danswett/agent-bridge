using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

internal sealed class EndSelfService(ISessionSource source, ISessionLauncher? launcher, ISessionStopper? stops, Func<DateTimeOffset> now)
{
    sealed record Receipt(AgentCredential Sender, string Machine, string Target, DateTimeOffset Until);
    sealed record Grant(Guid Receipt, Receipt Proof, DateTimeOffset Until);
    readonly Lock gate = new();
    readonly Dictionary<Guid, Receipt> receipts = [];
    readonly Dictionary<Guid, Grant> grants = [];

    internal void Record(AgentCredential caller, LiteSendInput input, LiteOutcome outcome)
    {
        if (outcome.Kind != LiteKind.Delivered || caller.Kind != "session"
            || !Guid.TryParseExact(outcome.Request, "D", out var id) || id == Guid.Empty) { return; }
        lock (gate)
        {
            Prune();
            if (receipts.Count + grants.Count >= 1024) { throw Refused("receipt capacity reached; delivery completed"); }
            if (!receipts.TryAdd(id, new(caller, input.Machine, input.Session, now().AddMinutes(10))))
            { throw Refused("duplicate delivery receipt"); }
        }
    }

    internal async Task<object> HandleAsync(string op, JsonElement data, IProtocolChannel channel,
        AgentCredentials? credentials, ICallerGuard callers, AgentAuditLog? audit)
    {
        bool Person() => callers.Classify(channel, fresh: true).Kind == CallerKind.HostCli && credentials?.IsCredentialed(channel) == false;
        if (credentials is null || op != "agent.receipt" && !Person()) { throw Refused("only a non-agent person using the host command line may admit or execute"); }
        var fields = op == "EndSelf" ? new[] { "request", "grant" } : ["receipt", "machine", "target"];
        if (data.ValueKind != JsonValueKind.Object || data.GetRawText().Length > 2048
            || data.EnumerateObject().Count() != fields.Length
            || data.EnumerateObject().Any(p => !fields.Contains(p.Name) || p.Value.ValueKind != JsonValueKind.String)
            || data.EnumerateObject().Select(p => p.Name).Distinct().Count() != fields.Length) { throw Refused("invalid fields"); }
        string Field(string key) => data.GetProperty(key).GetString()!;
        var id = Guid.TryParseExact(Field(op == "EndSelf" ? "grant" : "receipt"), "D", out var parsed) ? parsed : Guid.Empty;
        Grant selected;
        lock (gate)
        {
            Prune();
            var proof = receipts.GetValueOrDefault(id);
            bool Target() => proof is not null && proof.Machine == Field("machine") && proof.Target == Field("target");
            if (op == "agent.receipt")
            {
                var caller = credentials.Of(channel);
                if (caller is not { Kind: "session" } || caller.Scope == AgentPolicy.ScopeNone) { throw Refused("a live session credential is required"); }
                return new { confirmed = Target() && proof!.Sender.Id == caller.Id && credentials.IsLive(proof.Sender) };
            }
            if (audit is null || stops is not StopService || !stops.Available) { throw Refused("audited stopping is unavailable"); }
            if (op == "session.endSelf.admit")
            {
                if (!Target() || !Eligible(proof!, credentials)) { throw Refused("a confirmed receipt from this one-shot incarnation is required"); }
                var grant = Guid.NewGuid();
                if (!audit.Append(new("local", grant.ToString(), "person:local", [], op, proof!.Sender.Session, "admitted")
                    { Grant = id.ToString() })) { throw Refused("admission audit is unavailable"); }
                receipts.Remove(id);
                grants.Add(grant, new(id, proof, now().AddMinutes(1)));
                return new { grant = grant.ToString() };
            }
            if (!Guid.TryParseExact(Field("request"), "D", out var request) || request == Guid.Empty
                || !grants.Remove(id, out selected!) || !Eligible(selected.Proof, credentials)) { throw Refused("grant absent, consumed or stale"); }
        }
        // Consume before dispatch: an audit failure or interrupted stop must not permit replay.
        return await StopAudit.StopAsync((StopService)stops!, source, audit!,
            new StopRequest { Request = Field("request"), Id = selected.Proof.Sender.Session }, "local", "person:local",
            admitted: (pid, at) => Person() && selected.Until > now() && Eligible(selected.Proof, credentials)
                && pid == selected.Proof.Sender.RootPid && at == selected.Proof.Sender.RootStartUtc,
            grant: $"{id}/{selected.Receipt}");
    }

    bool Eligible(Receipt proof, AgentCredentials credentials)
    {
        var sender = proof.Sender;
        var process = new SessionProcess(sender.RootPid, sender.RootStartUtc);
        return proof.Until > now() && credentials.IsLive(sender) && source.ProcessOf(sender.Session) == process
            && launcher?.TrustedRoleOf(sender.Session, process) == "one-shot";
    }

    void Prune()
    {
        foreach (var id in receipts.Where(p => p.Value.Until <= now()).Select(p => p.Key).ToArray()) { receipts.Remove(id); }
        foreach (var id in grants.Where(p => p.Value.Until <= now()).Select(p => p.Key).ToArray()) { grants.Remove(id); }
    }
    static InvalidOperationException Refused(string why) => new("refused: EndSelf: " + why);
}
