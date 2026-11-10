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
// And this launcher's record of itself, for an app opened after it started (#420). Only for
// serve (LauncherFiles.ForServe).
using var files = LauncherFiles.ForServe(args, profileFolder, log);
DateTime startedUtc;
using (var self = Process.GetCurrentProcess()) { startedUtc = self.StartTime.ToUniversalTime(); }
// The host ends with this launcher (HostJob, #420).
HostJob? job = null;
if (OperatingSystem.IsWindows())
{
    try { job = HostJob.Create(); }
    catch (Win32Exception e) { log($"the host cannot be tied to this launcher, so it is started without being watched for host.restart: {e.Message}"); }
}
var supervisor = new HostSupervisor
{
    RunHost = async ct =>
    {
        // Whether the app's Stop finds this launcher without the logon task. When it may not, the
        // host looks for the stop marker itself once its pipe is open (CheckStopVariable).
        var recorded = files is null || files.Holding;
        if (job is not null && OperatingSystem.IsWindows())
        {
            // Watched: host.restart hands its replacement to this launcher, since the host cannot
            // outlive it and be left believing it is watched.
            var extra = new Dictionary<string, string> { [AgentBridge.Local.HostLauncherContract.Variable] = "1" };
            if (!recorded) { extra[AgentBridge.Local.HostLauncherContract.CheckStopVariable] = "1"; }
            try
            {
                using var child = job.Start(exe, args, extra, AppContext.BaseDirectory);
                return await child.WaitForExitAsync(ct);
            }
            // Gone (an update replacing it, say): nothing to start, and not a crash.
            catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3)
            {
                log($"the host could not be started: {e.Message}");
                return 1;
            }
            catch (Win32Exception e) { log($"the host could not be started tied to this launcher, so it is started without being watched for host.restart: {e.Message}"); }
        }
        // Not tied to this launcher, so it could outlive it: never told it is watched (it starts
        // its own replacement after host.restart), and told to look for the app's stop itself.
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in args) { start.ArgumentList.Add(a); }
        if (files is not null) { start.Environment[AgentBridge.Local.HostLauncherContract.CheckStopVariable] = "1"; }
        Process? host;
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
    Stopped = () => files?.Stopped() ?? false,
    ClearStopped = () => files?.ClearStoppedBefore(startedUtc),
    BeforeStart = () => files?.HoldRecord(Environment.ProcessId, startedUtc),
};
try { return await supervisor.RunAsync(); }
finally
{
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
