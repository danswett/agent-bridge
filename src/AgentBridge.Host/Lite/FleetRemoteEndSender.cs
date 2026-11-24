using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Codec = AgentBridge.Roster.Codec;

namespace AgentBridge.Host.Lite;

// G10 PR2 (docs/design/fleet-grants.md, "Remote targets"): the source host of a remote `fleet end --target`. DORMANT: it does nothing
// unless the host turns it on, and Program builds it only then. It judges the local agent credential afresh (the AP1 check the receiver
// relies on), seals a fleet.end FleetActionEnvelope with the host key, sends once over the relay link, and reports a typed receipt.
// It never retries, never queues for an offline peer and never falls back to HA. A lost reply is unconfirmed (exit 7).
public sealed class FleetRemoteEndSender(IFleetLaunchLink link, AgentCredentials credentials, ILiteMode mode,
    Func<(IEnvelopeSigner Sign, byte[] Point)?> signer, TimeProvider? clock = null, AgentAuditLog? audit = null, TimeSpan? watchEvery = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly LocalApi.RefusalAudits refusals = new(clock ?? TimeProvider.System);

    // True when the request names a host other than this one (by device id or node id): the local stop gate never serves it.
    public bool IsRemote(string? target) =>
        target is not null && !(link.Self.Length >= 16 && (target == link.Self || target == "agent_bridge_" + link.Self[..16]));

    public async Task<FleetStopReceipt> SendAsync(AgentCredential caller, FleetStopInput input, CancellationToken ct)
    {
        // A refusal before anything is sent is this host's own refused use: audited through the same row and hourly limiter as every other refused
        // stop; if the row cannot be written, that is the refusal. ILiteMode.On is the kill switch (the agentMessaging.remote setting and any lite kill).
        FleetStopReceipt Refuse(string reason)
        {
            if (audit is not null)
            {
                try { LocalApi.AuditRefusal(credentials.PrincipalOf(caller), caller.Chain, Ops.SessionStop, JsonSerializer.SerializeToElement(input, ProtocolJson.Options), audit, reason, refusals); }
                catch (InvalidOperationException) { return new(input.Request, input.Grant, 0, "refused", "audit-unavailable"); }
            }
            return new(input.Request, input.Grant, 0, "refused", reason);
        }
        if (!mode.On) { return Refuse("kill-switch"); }
        var account = link.Account;
        if (string.IsNullOrEmpty(account)) { return Refuse("target-offline"); }
        GrantSubject subject;
        try { subject = FleetGrantLaunch.Subject(credentials, account, link.Self, caller); }
        catch (FleetLaunchDenied) { return Refuse("policy"); }
        var signed = signer();
        // Making the key can wait on its file lock: a kill that lands meanwhile signs and sends nothing.
        if (!mode.On) { return Refuse("kill-switch"); }
        if (signed is not { } sign) { return Refuse("policy"); }
        var device = Resolve(input.Target);
        if (device is null) { return Refuse("target-offline"); }
        if (device == link.Self) { return Refuse("policy"); }

        // The receiver checks the target is itself; generation 0 is diagnostic only (the receiver fences on its own grant generation).
        var sent = input with { Target = device };
        var body = new FleetActionBody(account, link.Self, FleetActionEnvelope.SubjectDigest(subject.Account, subject.SourceHost,
            subject.Session, subject.Credential, subject.ProcessInstance.Pid, subject.ProcessInstance.StartedAt.UtcTicks), device,
            FleetActionEnvelope.EndAction, input.Grant.ToString("D"), 0, input.Request, FleetActionEnvelope.NewNonce(),
            time.GetUtcNow() + Lifetime, FleetActionEnvelope.PayloadSha256(JsonSerializer.SerializeToUtf8Bytes(sent, ProtocolJson.Options)));
        JsonElement request;
        try
        {
            request = JsonSerializer.SerializeToElement(new { envelope = FleetActionEnvelope.Seal(body, sign.Sign), end = sent,
                signPoint = Codec.Base64Url(sign.Point) }, ProtocolJson.Options);
        }
        catch (ArgumentException) { return Refuse("policy"); }

        // From here a request may have reached the peer: any failure is unconfirmed, never a resend with a fresh nonce.
        using var flight = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watching = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            try
            {
                using var tick = new PeriodicTimer(watchEvery ?? TimeSpan.FromMilliseconds(500));
                while (await tick.WaitForNextTickAsync(watching.Token)) { if (!mode.On) { flight.Cancel(); return; } }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var reply = await link.RequestAsync(device, account, FleetRemoteEnd.Op, request, flight.Token);
            var receipt = reply.ValueKind == JsonValueKind.Object ? reply.Deserialize<FleetStopReceipt>(ProtocolJson.Options) : null;
            return receipt is not null && receipt.Request == input.Request && receipt.Grant == input.Grant ? receipt : Unconfirmed();
        }
        catch (LitePeerNotTrustedException) { return Refuse("peer-not-trusted"); }
        catch (LitePeerOfflineException) { return Refuse("target-offline"); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return Unconfirmed(); }
        finally { watching.Cancel(); await watcher; }

        FleetStopReceipt Unconfirmed() => new(input.Request, input.Grant, 0, "unconfirmed", null);
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
