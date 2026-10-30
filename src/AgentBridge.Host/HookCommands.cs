using System.Globalization;
using AgentBridge.Hooks;
using AgentBridge.Host.Core.Intake;

namespace AgentBridge.Host;

// agent-bridge-host hooks status: how hook handling stands on this machine (docs/host-hooks.md).
// Read-only: it reads the profile's intake record, the heartbeat's time and the hook log, and
// counts the spool only on a profile that has taken it over.
public static class HookCommands
{
    public static int Status(string profileFolder, string defaultRuntime, TextWriter output, DateTimeOffset now)
    {
        var (state, problem) = Directory.Exists(profileFolder) ? IntakeState.Load(profileFolder) : (null, null);
        var runtime = state?.RuntimeRoot ?? defaultRuntime;
        if (state is not null)
        {
            output.WriteLine($"hook intake: on, for agent-ha-bridge installation {state.InstallationId} (runtime {state.RuntimeRoot}), since {state.MigratedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        else
        {
            output.WriteLine(problem ?? "hook intake: off; this profile has not migrated from agent-ha-bridge, so the host never reads its spool");
        }

        var heartbeat = SpoolContract.HeartbeatPath(runtime);
        if (File.Exists(heartbeat))
        {
            var age = now.UtcDateTime - File.GetLastWriteTimeUtc(heartbeat);
            var writer = Safe(() => File.ReadAllText(heartbeat).Trim()) ?? "?";
            var fresh = age < SpoolContract.HeartbeatMaxAge;
            var whose = writer == Environment.ProcessId.ToString(CultureInfo.InvariantCulture) ? "this process" : $"pid {writer}";
            output.WriteLine($"heartbeat: {(fresh ? "fresh" : "stale")}, {(int)age.TotalSeconds} s old, written by {whose}{(fresh ? "; hooks are spooling" : "; hooks give their fixed reply (or agent-ha-bridge's run PowerShell)")}");
        }
        else
        {
            output.WriteLine("heartbeat: none; hooks give their fixed reply (or agent-ha-bridge's run PowerShell)");
        }

        if (state is not null)
        {
            var spool = SpoolContract.SpoolDirectory(runtime);
            var waiting = Safe(() => Directory.Exists(spool) ? Directory.GetFiles(spool, "*.json").Count(f => f.EndsWith(".json", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture) : "0") ?? "?";
            output.WriteLine($"spool: {waiting} event(s) waiting");
        }

        var window = TimeSpan.FromHours(24);
        output.WriteLine($"runs: {HookStats.Read(runtime, now, window).Describe(window)}");
        return 0;
    }

    static string? Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
