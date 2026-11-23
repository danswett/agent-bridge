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
public sealed class FleetRemoteEndSender(IFleetLaunchLink link, AgentCredentials credentials,
    Func<(IEnvelopeSigner Sign, byte[] Point)?> signer, Func<bool> enabled, TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    readonly TimeProvider time = clock ?? TimeProvider.System;

    // True when the request names a host other than this one (by device id or node id): the local stop gate never serves it.
    public bool IsRemote(string? target) =>
        target is not null && !(link.Self.Length >= 16 && (target == link.Self || target == "agent_bridge_" + link.Self[..16]));

    public async Task<FleetStopReceipt> SendAsync(AgentCredential caller, FleetStopInput input, CancellationToken ct)
    {
        FleetStopReceipt Refuse(string reason) => new(input.Request, input.Grant, 0, "refused", reason);
        if (!enabled()) { return Refuse("policy"); }
        var account = link.Account;
        if (string.IsNullOrEmpty(account)) { return Refuse("target-offline"); }
        GrantSubject subject;
        try { subject = FleetGrantLaunch.Subject(credentials, account, link.Self, caller); }
        catch (FleetLaunchDenied) { return Refuse("policy"); }
        if (signer() is not { } sign) { return Refuse("policy"); }
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
        try
        {
            var reply = await link.RequestAsync(device, account, FleetRemoteEnd.Op, request, ct);
            var receipt = reply.ValueKind == JsonValueKind.Object ? reply.Deserialize<FleetStopReceipt>(ProtocolJson.Options) : null;
            return receipt is not null && receipt.Request == input.Request && receipt.Grant == input.Grant ? receipt : Unconfirmed();
        }
        catch (LitePeerNotTrustedException) { return Refuse("peer-not-trusted"); }
        catch (LitePeerOfflineException) { return Refuse("target-offline"); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return Unconfirmed(); }

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
