using System.Collections.Concurrent;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Roster;

namespace AgentBridge.Host.Lite;

public static class LiteCodes
{
    public const string NotReplyTarget = "notReplyTarget";
    public const string Busy = "busy";
    public const string ComposerNotEmpty = "composerNotEmpty";
    public const string NoSuchSession = "noSuchSession";
}

public sealed record GateDecision(bool Allowed, string? Code, string? Reason)
{
    public static GateDecision Allow() => new(true, null, null);
    public static GateDecision Refuse(string reason) => new(false, LiteCodes.NotReplyTarget, reason);
}

// Decides whether a session may be typed into by a verified lite message. The host composes
// every registered gate as OR (any Allowed wins): forced-ask sessions launched here, allow-all
// sessions with the machine switch enabled and an exact session-id grant, or fleet targets.
public interface IReplyTargetGate
{
    Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession);
}

// What this host knows of a session: the permission mode it launched it with, if it launched it,
// and whether Home Assistant's launch path or an agent under a grant launched it (a fleet target).
public interface ILiteLaunches
{
    string? PermissionsOf(string session);
    bool IsFleetTarget(string session);
}

// unknown and refused: the safe direction. In memory only, so a restart forgets every fleet target
// (relay A has no persistence). A fleet entry holds the exact id and the process LaunchService started for it, as it started it: a session
// whose live process is not that one or under it (an npm shim's cmd.exe starts the agent) is not it. An Unverified entry (a tombstone, also
// what an entry seen gone or replaced becomes) authorizes nothing; a note with a lower Generation than the held entry is ignored.
public sealed class LiteLaunchLog(Func<string, SessionProcess?>? current = null, Func<Started, int, bool?>? owns = null) : ILiteLaunches
{
    readonly ConcurrentDictionary<string, string> modes = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Started> fleet = new(StringComparer.Ordinal);
    public void Launched(string session, LaunchedWith with) => modes[session] = with.Permissions;
    public string? PermissionsOf(string session) => modes.GetValueOrDefault(session);
    public void FleetLaunched(string session, Started started) => fleet.AddOrUpdate(session, started, (_, held) => held.Generation > started.Generation ? held : started);
    public bool IsFleetTarget(string session)
    {
        if (!fleet.TryGetValue(session, out var launched) || launched.Unverified) { return false; }
        // Unknown (the started process gone or its tree unreadable) refuses and keeps the entry; a known other process ends it.
        var own = current?.Invoke(session) is { StartedAt: not null } live ? owns?.Invoke(launched, live.Id) : false;
        if (own == false) { fleet.TryUpdate(session, launched with { Unverified = true }, launched); } // only this entry, never a newer launch's
        return own == true;
    }
}

// Registers the delivery routine's forced-ask, allowlisted allow-all and fleet-target gates;
// the routine never names a gate itself.
public static class LiteComposition
{
    public static LiteDeliver Create(ISessionSource source, ISessionReplier replier, IConsoleInjector injector, Func<AgentMessagingSettings> currentSettings, LaunchService launcher,
        Func<RosterVerifier?> roster, Action<string> log)
    {
        var launches = new LiteLaunchLog(source.ProcessOf, launcher.Owns);
        launcher.SessionStarted += launches.Launched;
        // Only the host raises this, from its own launch path; nothing a caller or session sends sets it.
        launcher.FleetTargetStarted += launches.FleetLaunched;
        // Checked against the live process, not taken as an ending: an older generation's late removal keeps a newer entry.
        source.Changed += (_, change) => launches.IsFleetTarget(change.Id);
        IReplyTargetGate[] gates = [new ForcedAskGate(launches), new AllowlistedAllTargetsGate(launches, currentSettings), new FleetTargetGate(launches, roster)];
        return new LiteDeliver(gates, source, replier, injector, log);
    }
}

// The default gate: only a session this host launched in forced-ask mode.
public sealed class ForcedAskGate(ILiteLaunches launches) : IReplyTargetGate
{
    public Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession) => Task.FromResult(launches.PermissionsOf(targetSession) switch
    {
        LaunchProtocol.Ask => GateDecision.Allow(),
        _ => GateDecision.Refuse("the session was not launched here in forced-ask mode"),
    });
}

// Allow-all sessions need both the machine switch and an exact, per-session grant.
public sealed class AllowlistedAllTargetsGate(ILiteLaunches launches, Func<AgentMessagingSettings> currentSettings) : IReplyTargetGate
{
    public Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession)
    {
        var settings = currentSettings();
        return Task.FromResult(launches.PermissionsOf(targetSession) == LaunchProtocol.AllowAll
            && settings.RemoteEnabled
            && settings.AllowAllTargets
            && settings.AllowAllTargetSessions.Contains(targetSession, StringComparer.Ordinal)
                ? GateDecision.Allow()
                : GateDecision.Refuse("the allow-all session is not enabled and listed"));
    }
}

// A session Home Assistant's launch path or an agent under a grant launched, typed into only when the sending machine is a serving host in
// the pinned, verified device roster (owner, Nov 20 12:37 PT; about the serving host, not the agent principal). With no pin, a roster not
// yet verified, or any other sender, it refuses. Permission and trust prompts stay person-only: LiteDeliver types only into an idle session with no question.
public sealed class FleetTargetGate(ILiteLaunches launches, Func<RosterVerifier?> roster) : IReplyTargetGate
{
    public const string Reason = "the session is not a fleet target for a serving-host sender";
    // Three missed 5-minute observe refreshes (#181); until then a pinned host refuses 15 minutes after verifying.
    public static readonly TimeSpan MaxRosterAge = TimeSpan.FromMinutes(15);

    public Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession) =>
        Task.FromResult(launches.IsFleetTarget(targetSession) && ServingHost(env.Body.Principal.Machine) ? GateDecision.Allow() : GateDecision.Refuse(Reason));

    bool ServingHost(string machine)
    {
        RosterVerifier? verified;
        try { verified = roster(); }
        catch (InvalidOperationException) { return false; } // a pin that is unreadable, another account's or not yet verified
        if (verified is null) { return false; }
        // The roster reader holds the verifier's lock while it changes it (RelayFleetRoster.Hosts).
        lock (verified)
        {
            return verified.Serving.TryGetValue(machine, out var at) && verified.Graph[at].Body.Device?.Kind == "host";
        }
    }
}
