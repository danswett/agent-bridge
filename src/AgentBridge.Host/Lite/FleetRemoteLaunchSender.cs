using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Codec = AgentBridge.Roster.Codec;

namespace AgentBridge.Host.Lite;

// What the sender needs of the relay link for one fleet request to a peer: who this host is, who is present, one request.
// The real link is the relay connection (G9c2); tests use a fake. RequestAsync must honour `ct` and throw
// LitePeerOfflineException when nothing could be sent.
public interface IFleetLaunchLink
{
    string Self { get; }
    string Account { get; }
    IReadOnlyList<PresenceEntry> Presence { get; }
    Task<JsonElement> RequestAsync(string peerId, string account, string op, JsonElement request, CancellationToken ct);
}

// G9c1 (docs/design/fleet-grants.md, "Remote targets"): the source host of a remote `fleet launch --target`. It judges the
// local agent credential afresh (the AP1 check the receiver relies on), seals a FleetActionEnvelope with the host key, sends once,
// and reports a typed receipt. It never retries, never queues for an offline peer and never falls back to HA.
public sealed class FleetRemoteLaunchSender(IFleetLaunchLink link, AgentCredentials credentials, ILiteMode mode,
    Func<(IEnvelopeSigner Sign, byte[] Point)?> signer, TimeProvider? clock = null,
    AgentAuditLog? audit = null, TimeSpan? watchEvery = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly LocalApi.RefusalAudits refusals = new(clock ?? TimeProvider.System);

    // True when the request names a host other than this one (by device id or node id): the local grant path never serves it.
    public bool IsRemote(string? target) =>
        target is not null && !(link.Self.Length >= 16 && (target == link.Self || target == "agent_bridge_" + link.Self[..16]));

    // ILiteMode.On is the remote-launch kill switch on the source host: it reads the agentMessaging.remote setting and any recorded lite kill, the same check
    // AgentLiteSender makes for messaging, and it is made before AND after signing and while waiting for the reply.
    bool On() => mode.On;

    public async Task<FleetLaunchReceipt> SendAsync(AgentCredential caller, FleetLaunchInput input, CancellationToken ct)
    {
        // A refusal before anything is sent is the source host's own refused use: it is audited here with the same row and the same hourly limiter every other
        // refused launch uses (LocalApi.AuditRefusal, the function the receiver audit of #4170 also writes through). If the row cannot be written, that is the refusal.
        FleetLaunchReceipt Refuse(string reason)
        {
            if (audit is not null)
            {
                try { LocalApi.AuditRefusal(credentials.PrincipalOf(caller), caller.Chain, LaunchProtocol.Launch, JsonSerializer.SerializeToElement(input, ProtocolJson.Options), audit, reason, refusals); }
                catch (InvalidOperationException) { return FleetGrantLaunch.Receipt(null, new(LaunchOutcome.Refused), input, "audit-unavailable"); }
            }
            return FleetGrantLaunch.Receipt(null, new(LaunchOutcome.Refused), input, reason);
        }
        if (!On()) { return Refuse("kill-switch"); }
        var account = link.Account;
        if (string.IsNullOrEmpty(account)) { return Refuse("target-offline"); }
        GrantSubject subject;
        try { subject = FleetGrantLaunch.Subject(credentials, account, link.Self, caller); }
        catch (FleetLaunchDenied) { return Refuse("policy"); }
        var signed = signer();
        // Making the key can wait on its file lock: a kill, a settings change or a caller that lapsed meanwhile signs and sends nothing.
        if (!On()) { return Refuse("kill-switch"); }
        if (signed is not { } sign) { return Refuse("policy"); }
        try { FleetGrantLaunch.Subject(credentials, account, link.Self, caller); }
        catch (FleetLaunchDenied) { return Refuse("policy"); }
        var device = Resolve(input.Target);
        if (device is null) { return Refuse(input.Target is null ? "policy" : "target-offline"); }
        if (device == link.Self) { return Refuse("policy"); }

        // Generation 0: the sender never learns the grant generation (person-only state); the receiver fences on its own (G5).
        var body = new FleetActionBody(account, link.Self, FleetActionEnvelope.SubjectDigest(subject.Account, subject.SourceHost,
            subject.Session, subject.Credential, subject.ProcessInstance.Pid, subject.ProcessInstance.StartedAt.UtcTicks), device,
            FleetActionEnvelope.LaunchAction, input.Grant.ToString("D"), 0, input.Request, FleetActionEnvelope.NewNonce(),
            time.GetUtcNow() + Lifetime, FleetActionEnvelope.PayloadSha256(JsonSerializer.SerializeToUtf8Bytes(input, ProtocolJson.Options)));
        JsonElement request;
        try
        {
            request = JsonSerializer.SerializeToElement(new { envelope = FleetActionEnvelope.Seal(body, sign.Sign), launch = input,
                signPoint = Codec.Base64Url(sign.Point) }, ProtocolJson.Options);
        }
        catch (ArgumentException) { return Refuse("policy"); }

        // From here a request may have reached the peer: any failure, a cancel, or a kill while waiting is unconfirmed, never a resend with a fresh nonce.
        using var flight = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watching = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            try
            {
                using var tick = new PeriodicTimer(watchEvery ?? TimeSpan.FromMilliseconds(500));
                while (await tick.WaitForNextTickAsync(watching.Token))
                {
                    if (!On()) { flight.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var reply = await link.RequestAsync(device, account, FleetRemoteLaunch.Op, request, flight.Token);
            var receipt = reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("result", out _)
                ? reply.Deserialize<FleetLaunchReceipt>(ProtocolJson.Options) : null;
            // The peer's receipt is rebuilt from fixed fields only (outcome, canonical session id, permissions, counters, a fixed reason code): no free-text
            // reason, no peer cwd or trust question is passed through. Anything that does not fit is not a result this host can show.
            return Normalize(receipt, input) ?? Unconfirmed();
        }
        catch (LitePeerNotTrustedException) { return Refuse("peer-not-trusted"); }
        catch (LitePeerOfflineException) { return Refuse("target-offline"); }
        catch (Exception) { return Unconfirmed(); }
        finally { watching.Cancel(); await watcher; }

        FleetLaunchReceipt Unconfirmed() => FleetGrantLaunch.Receipt(null, new(LaunchOutcome.Unconfirmed), input, null);
    }

    static FleetLaunchReceipt? Normalize(FleetLaunchReceipt? r, FleetLaunchInput input)
    {
        if (r is null || r.Request != input.Request || r.Grant != input.Grant || r.Result is null || !Enum.IsDefined(r.Result.Outcome) || r.Generation < 0
            || r.Permissions is not (null or "ask" or "allowAll")) { return null; }
        string? session = null;
        if (r.Result.Session is { } full)
        {
            if ((!Guid.TryParseExact(full, "D", out var id) && !Guid.TryParseExact(full, "N", out id)) || id == Guid.Empty) { return null; }
            session = id.ToString("D");
        }
        if (r.Registered && (r.Result.Outcome != LaunchOutcome.Started || session is null || r.Permissions is null)) { return null; }
        var caps = r.Caps is { } c && c.Hourly is > 0 and <= 1000 && c.Daily is > 0 and <= 5000 && c.Live is > 0 and <= 100 ? c : null;
        var usage = r.Usage is { } u && u.Hourly >= 0 && u.Daily >= 0 && u.Live >= 0 ? u : null;
        // A reason outside the fixed refusal codes is never shown: any unknown one becomes the one generic code, "policy".
        var code = r.Reason is null ? null : FleetLaunchCommands.Reasons.Contains(r.Reason, StringComparer.Ordinal) ? r.Reason : "policy";
        return new(input.Request, input.Grant, r.Generation, new(r.Result.Outcome) { Session = session }, r.Permissions, r.Registered, usage, caps, code);
    }

    // A 32-hex device id, or the 29-character node id (agent_bridge_ + first 16 hex) resolved against present hosts only.
    string? Resolve(string? target)
    {
        if (target is null) { return null; }
        var hosts = link.Presence.Where(p => p.Kind == "host").Select(p => p.Id).ToList();
        if (Wire.IsDeviceId(target)) { return target == link.Self || hosts.Contains(target) ? target : null; }
        if (target.Length != 29 || !target.StartsWith("agent_bridge_", StringComparison.Ordinal)) { return null; }
        var matches = hosts.Where(id => id.StartsWith(target[13..], StringComparison.Ordinal)).Distinct().ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
