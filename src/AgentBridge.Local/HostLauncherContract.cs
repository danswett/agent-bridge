namespace AgentBridge.Local;

// Between agent-bridge-hostw (the logon task's launcher) and the host it runs (#106). Compiled
// into both, so neither can drift: the launcher has no other dependency, and keeps its copy to
// itself.
#if AGENT_BRIDGE_HOSTW
internal
#else
public
#endif
static class HostLauncherContract
{
    // Set to "1" by the launcher for a host it starts in its kill-on-close job, so one that cannot
    // outlive it. The host clears it at once, so nothing it starts in turn (an agent, a
    // replacement) inherits it.
    public const string Variable = "AGENT_BRIDGE_HOSTW";

    // Set to "1" by the launcher when the app's Stop might not reach the host it starts any other
    // way: the host was started outside the launcher's job, or the launcher could not record itself
    // for the app (#420's review). Such a host looks for StopMarker once its pipe is open, and
    // exits without serving if it is there. Cleared at once, as Variable is.
    public const string CheckStopVariable = "AGENT_BRIDGE_HOSTW_CHECK_STOP";

    // The launcher's stop marker, in the profile's folder: written by the app's Stop, and while it
    // is there a launcher starts no host again, after a crash or a restart. A launcher clears one
    // written before it started, since starting one (at sign-in, or the app's Start) is a start on
    // purpose; one written since stands.
    public const string StopMarker = "host.stopped";

    // The launcher's record of itself, in the profile's folder: "<pid> <start time, UTC ticks>".
    // Held open by the serving launcher for as long as it runs, so no second launcher can write
    // over it, and deleted when its handle closes, however the launcher ends. An app opened after
    // the launcher started (with no logon task, so nothing else knows it) finds it there, reading
    // it with every share mode, and a pid since reused is told apart by its start time (#420).
    public const string LauncherRecord = "launcher.pid";

    public static string FormatLauncher(int pid, DateTime startedUtc) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{pid} {startedUtc.Ticks}");

    public static bool TryParseLauncher(string text, out int pid, out long startedTicks)
    {
        pid = 0;
        startedTicks = 0;
        var parts = text.Trim().Split(' ');
        return parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pid)
            && long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out startedTicks);
    }

    // A host under the launcher ends with this after host.restart, once fully stopped: the
    // launcher starts it again at once, still watching it, rather than the host starting a
    // replacement of its own that nothing watches. A small code, so never mistaken for a crash.
    public const int Restart = 75;
}
