using System.Diagnostics;
using System.ComponentModel;
using AgentBridge.HostLauncher;

// agent-bridge-hostw: starts agent-bridge-host beside it with no console window and waits for
// it, returning its exit code. The logon task runs this, so nothing flashes on screen at
// sign-in. A host that crashed is started again a few times (HostSupervisor, #106); one that
// stopped on purpose never is. The host stays a console program for the CLI. docs/app-windows.md.
var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "agent-bridge-host.exe" : "agent-bridge-host");
var profileFolder = LauncherLog.ProfileFolder(args);
var log = LauncherLog.For(profileFolder);
// The app's Stop (HostLauncherContract.StopMarker): no host is started again while it is there.
var stopMarker = profileFolder is null ? null : Path.Combine(profileFolder, AgentBridge.Local.HostLauncherContract.StopMarker);
var supervisor = new HostSupervisor
{
    RunHost = async ct =>
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in args) { start.ArgumentList.Add(a); }
        // Tells the host it is watched, so host.restart hands its replacement to this launcher.
        start.Environment[AgentBridge.Local.HostLauncherContract.Variable] = "1";
        Process? host;
        // Gone or unreadable (an update replacing it, say): nothing to start, and not a crash.
        try { host = Process.Start(start); }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            log($"the host could not be started: {e.Message}");
            return 1;
        }
        if (host is null)
        {
            log("the host could not be started: no process was created");
            return 1;
        }
        using (host)
        {
            await host.WaitForExitAsync(ct);
            return host.ExitCode;
        }
    },
    Log = log,
    Stopped = () => stopMarker is not null && File.Exists(stopMarker),
    ClearStopped = () =>
    {
        try { if (stopMarker is not null) { File.Delete(stopMarker); } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"an earlier stop could not be cleared: {e.Message}"); }
    },
};
return await supervisor.RunAsync();

// The host's own log (<profile>\logs\host.log), which the app shows, appended a line at a time
// as the host appends it; written only while no host runs. Its folder by the host's own rule
// (ProfileFolders, compiled in).
static class LauncherLog
{
    // The profile's folder, or null for a name the host would refuse: the host's own rule.
    public static string? ProfileFolder(string[] args)
    {
        var i = Array.IndexOf(args, "--profile");
        var profile = i >= 0 && i + 1 < args.Length ? args[i + 1] : "default";
        return AgentBridge.Transport.ProfileFolders.IsValidProfile(profile) ? Path.Combine(AgentBridge.Transport.ProfileFolders.DefaultRoot, profile) : null;
    }

    public static Action<string> For(string? profileFolder)
    {
        if (profileFolder is null) { return _ => { }; }
        var file = Path.Combine(profileFolder, "logs", "host.log");
        return message =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} agent-bridge-hostw: {message}{Environment.NewLine}");
            }
            // Nowhere else to say it; the host is still started again.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        };
    }
}
