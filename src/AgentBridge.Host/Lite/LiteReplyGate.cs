using System.Collections.Concurrent;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

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
// every registered gate as OR (any Allowed wins), so a gate only ever adds targets: L5's default
// allows forced-ask sessions launched here, and L5b adds named allow-all sessions.
public interface IReplyTargetGate
{
    Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession);
}

// What this host knows of a session: the permission mode it launched it with, if it launched it.
public interface ILiteLaunches
{
    string? PermissionsOf(string session);
}

// unknown and refused: the safe direction.
public sealed class LiteLaunchLog : ILiteLaunches
{
    readonly ConcurrentDictionary<string, string> modes = new(StringComparer.Ordinal);
    public void Launched(string session, LaunchedWith with) => modes[session] = with.Permissions;
    public string? PermissionsOf(string session) => modes.GetValueOrDefault(session);
}

// The one registration point for the delivery routine and its gates. L5b adds its allowlist gate to
// `gates` here; the routine never names a gate itself.
public static class LiteComposition
{
    public static LiteDeliver Create(ISessionSource source, ISessionReplier replier, IConsoleInjector injector, Func<bool> allowAllTargets, LaunchService launcher, Action<string> log)
    {
        var launches = new LiteLaunchLog();
        launcher.SessionStarted += launches.Launched;
        IReplyTargetGate[] gates = [new ForcedAskGate(launches, allowAllTargets)];
        return new LiteDeliver(gates, source, replier, injector, log);
    }
}

// The default gate: a session this host launched in forced-ask mode. Allow-all passes only when
// agentMessaging.allowAllTargets is on (default off).
public sealed class ForcedAskGate(ILiteLaunches launches, Func<bool> allowAllTargets) : IReplyTargetGate
{
    public Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession) => Task.FromResult(launches.PermissionsOf(targetSession) switch
    {
        LaunchProtocol.Ask => GateDecision.Allow(),
        LaunchProtocol.AllowAll when allowAllTargets() => GateDecision.Allow(),
        LaunchProtocol.AllowAll => GateDecision.Refuse("the session runs allow-all"),
        _ => GateDecision.Refuse("the session was not launched here in forced-ask mode"),
    });
}
