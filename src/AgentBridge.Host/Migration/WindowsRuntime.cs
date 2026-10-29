using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Local;

namespace AgentBridge.Host.Migration;

// agent-ha-bridge's scheduled tasks and processes on this Windows machine, for this user.
//
// Stopping follows agent-ha-bridge's own Stop-BridgeOwnedRuntime (hooks/bridge-platform.ps1):
// the processes are found by command line (pwsh -File <hooksDir>\agent-bridge-<role>.ps1) and by
// their receipts in runtime\<role>.process.json, and only one whose receipt (when it has one)
// matches its start time is stopped; the supervisor first, so it cannot start the daemon again.
// It is a hard stop, as that function's: the daemon keeps no state in memory that a restart
// does not rebuild from its runtime folder.
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentHaBridgeRuntime(AgentHaBridgeProbe? probe = null) : IAgentHaBridgeRuntime
{
    readonly AgentHaBridgeProbe _probe = probe ?? new AgentHaBridgeProbe();

    static readonly (string Role, string Script)[] Roles =
        [("supervisor", "agent-bridge-supervisor.ps1"), ("daemon", "agent-bridge-daemon.ps1"), ("devbox-keepawake", "agent-bridge-devbox-keepawake.ps1")];

    public string? Active() => _probe.Active();

    public ScheduledTaskState? Task(string name) => WithTask(name, task => new ScheduledTaskState((bool)Get(task, "Enabled")!, (int)Get(task, "State")! == 4), missing: true);

    public void SetEnabled(string name, bool enabled) => WithTask(name, task => { task.GetType().InvokeMember("Enabled", BindingFlags.SetProperty, null, task, [enabled]); return true; });

    public void Start(string name) => WithTask(name, task => { Call(task, "Run", (object?)null); return true; });

    public void Stop(AgentHaBridgeInstall install)
    {
        // The running task instances first (wscript and its launcher), so the scheduler no longer
        // counts them as running; a task that is not running ignores this.
        foreach (var name in new[] { install.TaskName, install.DevBoxTaskName })
        {
            WithTask(name, task => { Call(task, "Stop", 0); return true; }, missing: true);
        }
        foreach (var (role, script) in Roles)
        {
            foreach (var process in Owned(install, role, script))
            {
                using (process)
                {
                    try { process.Kill(); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
                    {
                        throw new IOException($"agent-ha-bridge's {role} (process {process.Id}) did not stop");
                    }
                }
            }
        }
        // As Stop-BridgeOwnedRuntime: one that came back while the others stopped means something
        // is still starting it.
        foreach (var (role, script) in Roles)
        {
            foreach (var process in Owned(install, role, script))
            {
                var id = process.Id;
                process.Dispose();
                throw new IOException($"agent-ha-bridge's {role} started again (process {id}) while it was being stopped");
            }
        }
    }

    // This installation's processes in a role: pwsh or powershell running that script by its
    // full path, and, where a receipt names the process, started when the receipt says.
    static List<Process> Owned(AgentHaBridgeInstall install, string role, string script)
    {
        var path = Regex.Escape(Path.Combine(install.HooksDir, script));
        var pattern = new Regex($"(?i:-File)\\s+(?:\"{path}\"|'{path}'|{path})(?=\\s|$)", RegexOptions.IgnoreCase);
        var receipt = Receipt(install, role);
        var owned = new List<Process>();
        foreach (var process in Process.GetProcessesByName("pwsh").Concat(Process.GetProcessesByName("powershell")))
        {
            var keep = false;
            try
            {
                keep = process.Id != Environment.ProcessId
                    && SystemProcessInspector.ReadCommandLine(process.Id) is { } commandLine && pattern.IsMatch(commandLine)
                    && (receipt is not { } r || r.Pid != process.Id || r.StartedUtcTicks == process.StartTime.ToUniversalTime().Ticks);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (keep) { owned.Add(process); } else { process.Dispose(); }
        }
        return owned;
    }

    static (int Pid, long StartedUtcTicks)? Receipt(AgentHaBridgeInstall install, string role)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(install.RuntimeRoot, $"{role}.process.json")));
            var r = doc.RootElement;
            return (r.GetProperty("pid").GetInt32(), r.GetProperty("startedUtcTicks").GetInt64());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    // Task Scheduler's COM service, as AgentHaBridgeProbe reads it.
    static T WithTask<T>(string name, Func<object, T> use, bool missing = false)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Task Scheduler is not available");
            service = Activator.CreateInstance(type) ?? throw new IOException("Task Scheduler is not available");
            Call(service, "Connect");
            folder = Call(service, "GetFolder", @"\")!;
            try { task = Call(folder, "GetTask", name)!; }
            catch (TargetInvocationException e) when (missing && e.InnerException is COMException or FileNotFoundException) { return default!; }
            return use(task);
        }
        catch (TargetInvocationException e) when (e.InnerException is { } inner)
        {
            throw new IOException($"Task Scheduler refused {name}: {inner.Message}", inner);
        }
        finally
        {
            foreach (var o in new[] { task, folder, service })
            {
                if (o is not null && Marshal.IsComObject(o)) { Marshal.ReleaseComObject(o); }
            }
        }
    }

    static object? Call(object target, string name, params object?[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
}

// The host serving a profile, reached on its local pipe (docs/protocol/local-pipe.md).
public sealed class LocalMigrationHost(string profileFolder) : IMigrationHost
{
    public async Task<bool> RunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await AgentBridge.Local.LocalClient.ConnectAsync(AgentBridge.Local.LocalEndpoint.For(profileFolder), ct: cancellationToken);
            await channel.CallAsync(HostOps.Status, ct: cancellationToken);
            return true;
        }
        catch (Exception e) when (e is IOException or AgentBridge.Local.HostErrorException or TimeoutException) { return false; }
    }

    public async Task<string?> RestartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await AgentBridge.Local.LocalClient.ConnectAsync(AgentBridge.Local.LocalEndpoint.For(profileFolder), ct: cancellationToken);
            await channel.CallAsync(HostOps.Restart, ct: cancellationToken);
            return null;
        }
        catch (Exception e) when (e is IOException or AgentBridge.Local.HostErrorException or TimeoutException) { return e.Message; }
    }
}

// The hook, run as an agent runs it: no shell, stdin closed after the event.
public sealed class ProcessHookRunner : IHookRunner
{
    public (int Exit, string Stdout) Run(string executable, IReadOnlyList<string> arguments, string stdin)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) { start.ArgumentList.Add(a); }
        using var process = Process.Start(start) ?? throw new IOException($"{executable} did not start");
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill();
            throw new IOException($"{executable} did not finish within 10 s");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult());
    }
}
