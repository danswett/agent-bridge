using System.Diagnostics;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;

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
// every registered gate as OR (any Allowed wins): forced-ask sessions launched here or
// allow-all sessions with the all-launched switch or the master-switch-plus-exact-id grant.
public interface IReplyTargetGate
{
    Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession);
}

// What this host knows of a session: the permission mode it launched it with, if it launched it.
public interface ILiteLaunches
{
    string? PermissionsOf(string session);
}

// Registers the delivery routine's forced-ask and allowlisted allow-all gates;
// the routine never names a gate itself.
public static class LiteComposition
{
    public static LiteDeliver Create(ISessionSource source, ISessionReplier replier, IConsoleInjector injector, Func<AgentMessagingSettings> currentSettings, Func<bool> modeOn, Func<string?> account, LaunchService launcher, string folder, P256KeyPair key, Action<string> log, SessionDrivers? drivers = null, DeliveryGate? gate = null)
    {
        var launches = new LiteLaunchLog(id => LiveProcess(source, id), folder, key, log);
        launcher.SessionStarted += launches.Launched;
        source.Changed += (_, _) => launches.Prune();
        IReplyTargetGate[] gates = [new ForcedAskGate(launches), new AllowlistedAllTargetsGate(launches, currentSettings)];
        // The lane (S4) exists only when the host supplies its delivery gate. It stays off until laneDelivery is on; the one pump
        // wakes on the composition's Offered event, and S6 owns stopping it.
        LaneComposition? lane = null; DeliveryDispatchPump? pump = null; LaneNoticeSink? notices = null;
        if (gate is not null)
        {
            lane = new LaneComposition(currentSettings, modeOn, gate, replier, LaneScreens.Create(source, injector, TimeProvider.System), folder, log);
            pump = new DeliveryDispatchPump(lane, source, log);
            notices = new LaneNoticeSink(lane);
        }
        return new LiteDeliver(gates, source, replier, injector, log, targetFence: TargetFence(launches, currentSettings, modeOn), account: account) { Drivers = drivers, Composition = lane, Pump = pump, Notices = notices };
    }

    internal static Func<string, Func<bool>> TargetFence(LiteLaunchLog launches, Func<AgentMessagingSettings> settings, Func<bool> modeOn)
    {
        var all = new AllowlistedAllTargetsGate(launches, settings);
        return id =>
        {
            var admitted = launches.Capture(id);
            return () => modeOn() && settings().RemoteEnabled && admitted is not null && launches.Capture(id) == admitted
                && (admitted.Permissions == LaunchProtocol.Ask || all.Allows(id));
        };
    }

    static SessionProcess? LiveProcess(ISessionSource source, string id)
    {
        if (source.Get(id) is not { ProcessId: { } pid } || source.ProcessOf(id) is not { StartedAt: { } at } bound || bound.Id != pid) { return null; }
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && new DateTimeOffset(process.StartTime.ToUniversalTime()) == at ? bound : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
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

// Either the all-launched switch or the existing master-plus-list grant, for live launches here.
public sealed class AllowlistedAllTargetsGate(ILiteLaunches launches, Func<AgentMessagingSettings> currentSettings) : IReplyTargetGate
{
    public Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession)
        => Task.FromResult(Allows(targetSession) ? GateDecision.Allow() : GateDecision.Refuse("the allow-all session is not a live launch here with an enabled grant"));

    internal bool Allows(string targetSession)
    {
        var settings = currentSettings();
        return launches.PermissionsOf(targetSession) == LaunchProtocol.AllowAll
            && settings.RemoteEnabled
            && (settings.AllowAllLaunchedTargets || (settings.AllowAllTargets && settings.AllowAllTargetSessions.Contains(targetSession, StringComparer.Ordinal)));
    }
}
