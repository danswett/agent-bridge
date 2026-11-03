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
// And this launcher's record of itself, for an app opened after it started (#420).
var files = profileFolder is null ? null : new LauncherFiles(profileFolder, log);
DateTime startedUtc;
using (var self = Process.GetCurrentProcess()) { startedUtc = self.StartTime.ToUniversalTime(); }
// The host ends with this launcher (HostJob, #420). Without one it is still started and watched,
// as before the job existed; only ending this launcher no longer ends it.
HostJob? job = null;
if (OperatingSystem.IsWindows())
{
    try { job = HostJob.Create(); }
    catch (Win32Exception e) { log($"the host will not end with this launcher: {e.Message}"); }
}
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
            if (job is not null && OperatingSystem.IsWindows())
            {
                try { job.Add(host); }
                catch (Win32Exception e) { log($"the host will not end with this launcher: {e.Message}"); }
            }
            await host.WaitForExitAsync(ct);
            return host.ExitCode;
        }
    },
    Log = log,
    Stopped = () => files?.Stopped() ?? false,
    ClearStopped = () => files?.ClearStopped(),
};
files?.RecordSelf(Environment.ProcessId, startedUtc);
try { return await supervisor.RunAsync(); }
finally
{
    files?.ForgetSelf(Environment.ProcessId, startedUtc);
    if (OperatingSystem.IsWindows()) { job?.Dispose(); }
}

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
