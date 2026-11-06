using System.Reflection;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

// Whether this profile's hook setup for an agent finished, as the host says it in its readiness
// evidence (`hookSetup`: unknown, notSetUp, incomplete, completed). Completed is setup done, not
// hooks proven: a new session still has to show them working (#783 D2).
public enum HookSetup { Unknown, NotSetUp, Incomplete, Completed }

public static class HookSetups
{
    // The host's field is AgentReadiness.HookSetup (#705 review, r4474869190). This reads it by name,
    // so the app builds and behaves as before against a host and protocol without it, and uses it
    // once it is there: a host that doesn't send it, and a build without it, read Unknown.
    static readonly PropertyInfo? Field = typeof(AgentReadiness).GetProperty("HookSetup");

    public static HookSetup Of(AgentReadiness agent) => Parse(Field?.GetValue(agent)?.ToString());

    // The wire's names and the enum's alike; anything else is Unknown.
    public static HookSetup Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "notsetup" => HookSetup.NotSetUp,
        "incomplete" => HookSetup.Incomplete,
        "completed" => HookSetup.Completed,
        _ => HookSetup.Unknown,
    };
}
