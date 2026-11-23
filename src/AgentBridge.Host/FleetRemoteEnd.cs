using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using Codec = AgentBridge.Roster.Codec;

namespace AgentBridge.Host;

// G10 (docs/design/fleet-grants.md, "Remote targets" and "Remote subject attestation"): serves a signed fleet.end from another host of
// this account, in the same fixed order as FleetRemoteLaunch and with every refusal before any side effect: own-peer trust, per-peer
// rate cap, envelope verify (action fleet.end), request binding, replay with the restart warm-up, then the receiving host's own grant
// is the authority through the shared stop gate (FleetStopCommands), which audits, counts and stops without force or confirmation.
public sealed class FleetRemoteEnd(FleetStopCommands stop, GrantStore grants, TimeProvider? clock = null, DateTimeOffset? startedAt = null)
{
    public const string Op = FleetStopCommands.Op;
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly RateCap rates = new(clock ?? TimeProvider.System);
    readonly FleetActionReplayCache replays = new(4096, TimeSpan.FromSeconds(30), startedAt ?? (clock ?? TimeProvider.System).GetUtcNow());

    public async Task<FleetStopReceipt> ReceiveAsync(LiteChannel channel, JsonElement data)
    {
        if (channel.Untrusted is not null || !rates.Allow(channel.PeerId, Op)) { return Refuse("policy"); }
        FleetActionSigned signed; FleetStopInput input; JsonElement end; byte[] point = [];
        try
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("envelope", out var e) || e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("body", out var body) || !e.TryGetProperty("signature", out var sig)
                || !data.TryGetProperty("end", out end) || !data.TryGetProperty("signPoint", out var p)
                || body.GetString() is not { } b || sig.GetString() is not { } s || !Codec.TryBase64Url(p.GetString() ?? "", out point))
            { return Refuse("bad-argument"); }
            signed = new(b, s);
            input = end.Deserialize<FleetStopInput>(ProtocolJson.Options) ?? throw new JsonException();
            if (input.Version != 1 || input.Request == Guid.Empty || input.Grant == Guid.Empty) { return Refuse("bad-argument"); }
        }
        catch (Exception x) when (x is InvalidOperationException or JsonException)
        { return Refuse("bad-argument"); }

        var now = time.GetUtcNow();
        FleetActionBody action;
        try { action = FleetActionEnvelope.Verify(signed, point, channel.Account, channel.PeerId, channel.Self, now, FleetActionEnvelope.EndAction); }
        catch (InvalidDataException) { return Refuse("policy", input); }
        if (action.GrantId != input.Grant.ToString("D") || action.Request != input.Request
            || action.PayloadSha256 != FleetActionEnvelope.PayloadSha256(JsonSerializer.SerializeToUtf8Bytes(input, ProtocolJson.Options))
            || !replays.TryAdd(action, now)) { return Refuse("policy", input); }

        var found = grants.TryGetUsable(input.Grant);
        if (!found.Usable) { return Refuse(FleetGrantLaunch.StoreReason(found.Problem), input); }
        // The envelope's generation is diagnostic only: the stop gate checks the generation this host's own store holds.
        var subject = found.Grant!.Subjects.FirstOrDefault(s => s.Account == action.Account && s.SourceHost == action.SourceDevice
            && FleetActionEnvelope.SubjectDigest(s.Account, s.SourceHost, s.Session, s.Credential, s.ProcessInstance.Pid,
                s.ProcessInstance.StartedAt.UtcTicks) == action.Subject);
        return subject is null ? Refuse("scope", input) : await stop.HandleRemoteAsync(subject, end);
    }

    static FleetStopReceipt Refuse(string reason, FleetStopInput? input = null) =>
        new(input?.Request ?? Guid.Empty, input?.Grant ?? Guid.Empty, 0, "refused", reason);
}
