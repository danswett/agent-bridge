using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using Codec = AgentBridge.Roster.Codec;

namespace AgentBridge.Host;

// G9b2b (docs/design/fleet-grants.md, "Remote targets" and "Remote subject attestation"): serves a signed fleet.launch from another
// host of this account. Fixed order, every refusal before any side effect: own-peer trust, per-peer rate cap, envelope verify,
// request binding, replay, then the receiving host's own grant is the authority through the shared launch pipeline.
// signPoint is believed only because it arrived on this channel, which the sender's device key authenticates (as in lite);
// the hostSign suspension is temporary, and a signed roster replaces this later.
public sealed class FleetRemoteLaunch(AgentPipeLaunch launch, GrantStore grants, TimeProvider? clock = null, DateTimeOffset? startedAt = null,
    ILiteMode? mode = null, AgentAuditLog? audit = null)
{
    public const string Op = "fleet.launch";
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly RateCap rates = new(clock ?? TimeProvider.System);
    // Warm-up: for about 2.5 minutes after a start nothing is accepted, so an envelope sealed before a restart cannot replay.
    readonly FleetActionReplayCache replays = new(4096, TimeSpan.FromSeconds(30), startedAt ?? (clock ?? TimeProvider.System).GetUtcNow());

    public async Task<FleetLaunchReceipt> ReceiveAsync(LiteChannel channel, JsonElement data)
    {
        // The lite kill (ILiteMode.On, the same switch as lite messaging) turns remote launch off, before any parse or side effect.
        if (mode is { On: false }) { return Refuse("kill-switch"); }
        if (channel.Untrusted is not null || !rates.Allow(channel.PeerId, Op)) { return Refuse("policy"); }
        FleetActionSigned signed; FleetLaunchInput input; byte[] point = [];
        try
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("envelope", out var e) || e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("body", out var body) || !e.TryGetProperty("signature", out var sig)
                || !data.TryGetProperty("launch", out var launchData) || !data.TryGetProperty("signPoint", out var p)
                || body.GetString() is not { } b || sig.GetString() is not { } s || !Codec.TryBase64Url(p.GetString() ?? "", out point))
            { return Refuse("bad-argument"); }
            signed = new(b, s);
            input = FleetGrantLaunch.Parse(launchData);
        }
        catch (Exception x) when (x is FleetLaunchDenied or InvalidOperationException or JsonException)
        { return Refuse("bad-argument"); }

        var now = time.GetUtcNow();
        FleetActionBody action;
        try { action = FleetActionEnvelope.Verify(signed, point, channel.Account, channel.PeerId, channel.Self, now); }
        catch (InvalidDataException) { return Refuse("policy", input); }
        if (action.GrantId != input.Grant.ToString("D") || action.Request != input.Request
            || action.PayloadSha256 != FleetActionEnvelope.PayloadSha256(JsonSerializer.SerializeToUtf8Bytes(input, ProtocolJson.Options))
            || !replays.TryAdd(action, now)) { return Refuse("policy", input); }

        var found = grants.TryGetUsable(input.Grant);
        if (!found.Usable) { return RefuseAudited(FleetGrantLaunch.StoreReason(found.Problem), input, action.SourceDevice); }
        // The envelope's generation is diagnostic only. As for a local launch (G5), the generation is the one this host's own
        // store holds when the launch is prepared, and dispatch rechecks that same generation, so revoke or reissue refuses.
        var subject = found.Grant!.Subjects.FirstOrDefault(s => s.Account == action.Account && s.SourceHost == action.SourceDevice
            && FleetActionEnvelope.SubjectDigest(s.Account, s.SourceHost, s.Session, s.Credential, s.ProcessInstance.Pid,
                s.ProcessInstance.StartedAt.UtcTicks) == action.Subject);
        return subject is null ? RefuseAudited("scope", input, action.SourceDevice) : await launch.LaunchGrantedRemoteAsync(subject, input);
    }

    // A verified caller's refusal before the shared pipeline is audited like any refused use (fleet-grants.md): the row names the source host,
    // not a session (none is matched yet). If the row cannot be written, nothing proceeds and the refusal says so.
    FleetLaunchReceipt RefuseAudited(string reason, FleetLaunchInput input, string sourceDevice) =>
        audit is null || audit.Append(new AgentAuditEntry("relay", input.Request.ToString("D"), AgentProtocol.Principal(sourceDevice, "unmatched"), [],
            LaunchProtocol.Launch, "remote", "refused", reason) { Grant = input.Grant.ToString("D") })
            ? Refuse(reason, input) : Refuse("audit-unavailable", input);

    static FleetLaunchReceipt Refuse(string reason, FleetLaunchInput? input = null) =>
        FleetGrantLaunch.Receipt(null, new(LaunchOutcome.Refused), input, reason);
}
