using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

// Where things are: the app's own folder (Velopack's current\, or a build folder), which
// carries the host, its launcher and the injector; and the host's data root and profile.
public sealed record InstallLayout(string AppFolder, string DataRoot, string Profile)
{
    public string ProfileFolder => Path.Combine(DataRoot, Profile);
    public string HostExe => Path.Combine(AppFolder, OperatingSystem.IsWindows() ? "agent-bridge-host.exe" : "agent-bridge-host");
    public string LauncherExe => Path.Combine(AppFolder, OperatingSystem.IsWindows() ? "agent-bridge-hostw.exe" : "agent-bridge-hostw");
    public string AppDataFolder => Path.Combine(DataRoot, "app");
    public string LogFile => Path.Combine(ProfileFolder, "logs", "host.log");
    public IReadOnlyList<string> ServeArguments => ["serve", "--profile", Profile];
}

// The logon task, behind an interface: Task Scheduler in the app, a fake in tests.
public interface ILogonTasks
{
    bool Exists(string name);
    void Register(string name, string xml);
    void Delete(string name);
    // Starts the task's action now.
    void Run(string name);
}

// Starting and ending processes, behind an interface for the same reason.
public interface IProcesses
{
    // Starts a program with no window, detached from the app.
    int Start(string path, IReadOnlyList<string> arguments);
    bool IsRunning(int pid);
    void Kill(int pid);
}

public sealed class SystemProcesses : IProcesses
{
    public int Start(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (var a in arguments) { start.ArgumentList.Add(a); }
        using var p = Process.Start(start) ?? throw new InvalidOperationException($"{path} did not start");
        return p.Id;
    }

    public bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: false); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

// The task's definition: a per-user logon trigger, the user's interactive token, least
// privilege (so the host runs unelevated where it can reach the person's consoles), restarted
// when it fails, never stopped for battery or time, and one instance at a time.
public static class LogonTaskXml
{
    public static string Build(string userId, string launcher, IReadOnlyList<string> arguments, string description)
    {
        string X(string s) => SecurityElement.Escape(s) ?? "";
        var args = string.Join(" ", arguments.Select(Quote));
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{X(description)}</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{X(userId)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{X(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{X(Quote(launcher))}</Command>
                  <Arguments>{X(args)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    // Windows command-line quoting, for the few plain arguments the host takes.
    public static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => !char.IsWhiteSpace(c) && c != '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    // Ours alone (G3): never agent-ha-bridge's AgentBridgeDaemon*, which the G5 probe looks for,
    // and per user, since the task folder is shared by everyone on the machine.
    public static string TaskName(string profile, string userSid) =>
        $"AgentBridgeHost_{profile}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)), 0, 4).ToLowerInvariant()}";
}

public enum HostRunState { Unknown, Running, Starting, Stopped, Missing }

// Installs and runs the host for the person at this machine: start at sign-in (the logon
// task), start, stop and restart now, and how it is doing. Everything goes through the host's
// own pipe when the host is there, and through Task Scheduler or a direct start when not.
public sealed class HostManager
{
    readonly InstallLayout layout;
    readonly ILogonTasks tasks;
    readonly IProcesses processes;
    readonly Func<CancellationToken, Task<LocalChannel>> connect;
    readonly string userId;
    readonly string userSid;

    public HostManager(InstallLayout layout, ILogonTasks tasks, IProcesses processes, Func<CancellationToken, Task<LocalChannel>>? connect = null, string? userId = null, string? userSid = null)
    {
        this.layout = layout;
        this.tasks = tasks;
        this.processes = processes;
        this.connect = connect ?? (ct => LocalClient.ConnectAsync(LocalEndpoint.For(layout.ProfileFolder), TimeSpan.FromSeconds(1), ct));
        this.userId = userId ?? CurrentUserId();
        this.userSid = userSid ?? CurrentUserSid();
    }

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);
    // Whether this app runs elevated; tests pretend.
    public Func<bool> Elevated { get; init; } = PeerPolicy.SelfElevated;

    public InstallLayout Layout => layout;
    public string TaskName => LogonTaskXml.TaskName(layout.Profile, userSid);
    public bool HostInstalled => File.Exists(layout.HostExe) && File.Exists(layout.LauncherExe);
    public bool StartsAtSignIn => tasks.Exists(TaskName);

    public void SetStartAtSignIn(bool on)
    {
        if (on)
        {
            if (!HostInstalled) { throw new InvalidOperationException($"the host is not beside the app ({layout.HostExe})"); }
            tasks.Register(TaskName, LogonTaskXml.Build(userId, layout.LauncherExe, layout.ServeArguments, $"Agent Bridge host for profile \"{layout.Profile}\": shows this machine's agent sessions to the Agent Bridge app and the user's devices."));
        }
        else if (tasks.Exists(TaskName)) { tasks.Delete(TaskName); }
    }

    // Whether the host answers on its pipe now.
    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            await using var ch = await connect(ct);
            await ch.HelloAsync(ct);
            return true;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    // Starts the host if it is not running: through the logon task when there is one, so it
    // runs as it would at sign-in, otherwise through its launcher. Waits until it answers. An
    // elevated app never starts it directly: the host would be elevated too, unable to type
    // into ordinary agent windows and refusing the app once it runs unelevated again. The
    // task always runs it unelevated.
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (await IsRunningAsync(ct)) { return; }
        if (!HostInstalled) { throw new InvalidOperationException($"the host is not beside the app ({layout.HostExe})"); }
        if (tasks.Exists(TaskName)) { tasks.Run(TaskName); }
        else if (Elevated()) { throw new InvalidOperationException("this app is running as administrator, so it will not start the host; turn on starting the host at sign-in, or open the app normally"); }
        else { processes.Start(layout.LauncherExe, layout.ServeArguments); }
        await WaitAsync(running: true, StartTimeout, ct);
    }

    // Asks the host to stop and waits for its pipe to go; a host that will not go is ended. One
    // that answers no is not: it is running, and refused this process (one inside an agent
    // session, design §6.3 invariant 13), so ending it anyway would be the very thing it refused.
    // The 2026-10-29 review found an agent could run this through the app's uninstall hook.
    public async Task StopAsync(CancellationToken ct = default)
    {
        int? pid = null;
        try
        {
            await using var ch = await connect(ct);
            await ch.HelloAsync(ct);
            pid = (await ch.CallAsync<HostStatus>(HostOps.Status, ct: ct)).ProcessId;
            try { await ch.CallAsync(HostOps.Stop, ct: ct); }
            catch (HostErrorException e) { throw new HostRefusedException(e.Message); }
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException) { }
        try { await WaitAsync(running: false, StopTimeout, ct); }
        catch (TimeoutException) when (pid is { } p)
        {
            processes.Kill(p);
            await WaitAsync(running: false, StopTimeout, ct);
        }
        // Its pipe is gone; wait for the process too, so its files can be replaced (an update).
        var deadline = DateTime.UtcNow + StopTimeout;
        while (pid is { } p && processes.IsRunning(p) && DateTime.UtcNow < deadline) { await Task.Delay(Poll, ct); }
    }

    // Restarting is stopping first, so a refused stop starts nothing either.
    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    async Task WaitAsync(bool running, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (await IsRunningAsync(ct) != running)
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException(running ? "the host did not start; see its log" : "the host did not stop"); }
            await Task.Delay(Poll, ct);
        }
    }

    // The last lines of the host's log, for the Settings page: read from the file, so they are
    // there when the host is not.
    public string LogTail(int lines = 200)
    {
        try
        {
            if (!File.Exists(layout.LogFile)) { return "(no log yet)"; }
            using var stream = new FileStream(layout.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            stream.Seek(Math.Max(0, length - 64 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var all = reader.ReadToEnd().TrimEnd('\r', '\n').Split('\n');
            return string.Join('\n', all.TakeLast(lines)).TrimEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"(the log could not be read: {e.Message})"; }
    }

    static string CurrentUserId() =>
        OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : Environment.UserName;

    static string CurrentUserSid() =>
        OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName : Environment.UserName;
}

// Task Scheduler through its COM API (Schedule.Service), as agent-ha-bridge's probe reads it:
// a user may register a task for themselves without elevation.
[SupportedOSPlatform("windows")]
public sealed class WindowsLogonTasks : ILogonTasks
{
    const int CreateOrUpdate = 6;
    const int LogonInteractiveToken = 3;

    public bool Exists(string name) => Use(folder =>
    {
        try { Release(Call(folder, "GetTask", name)); return true; }
        catch (Exception e) when (e is COMException or FileNotFoundException) { return false; }
    });

    public void Register(string name, string xml) => Use(folder =>
    {
        Release(Call(folder, "RegisterTask", name, xml, CreateOrUpdate, null, null, LogonInteractiveToken, null));
        return true;
    });

    public void Delete(string name) => Use(folder => { Call(folder, "DeleteTask", name, 0); return true; });

    public void Run(string name) => Use(folder =>
    {
        var task = Call(folder, "GetTask", name)!;
        try { Release(Call(task, "Run", (object?)null)); }
        finally { Release(task); }
        return true;
    });

    static T Use<T>(Func<object, T> act)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available");
        var service = Activator.CreateInstance(type)!;
        object? folder = null;
        try
        {
            Call(service, "Connect");
            folder = Call(service, "GetFolder", @"\")!;
            return act(folder);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    // Late binding wraps every COM failure in TargetInvocationException, which slipped past
    // callers' filters for COMException/UnauthorizedAccessException: a policy-denied
    // registration on first run ended the app. Callers see the real exception instead.
    internal static object? Call(object target, string name, params object?[] args)
    {
        try { return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, target, args); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerException);
            throw;
        }
    }

    static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) { Marshal.ReleaseComObject(o); }
    }
}

// The host answered a stop or restart with no, and is still running.
public sealed class HostRefusedException(string reason) : InvalidOperationException(reason);
