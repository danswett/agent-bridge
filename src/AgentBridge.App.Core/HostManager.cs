using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Host.Core.Usage;

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
    // Whether an instance of the task is running; false when there is no such task.
    bool IsRunning(string name);
    // Ends the task's running instance, if any.
    void Stop(string name);
}

// Starting and ending processes, behind an interface for the same reason.
public interface IProcesses
{
    // Starts a program with no window, detached from the app.
    int Start(string path, IReadOnlyList<string> arguments);
    bool IsRunning(int pid);
    void Kill(int pid);
    // When a process started, so a pid given to another since is not taken for it; null when that
    // cannot be read.
    DateTimeOffset? StartedAt(int pid) => null;
}

public sealed class SystemProcesses : IProcesses
{
    // The processes this started, by handle: whether one runs, and ending it, are asked of the
    // process itself, never of whatever has its pid by then (a launcher long gone, its pid reused).
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> started = new();

    public int Start(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (var a in arguments) { start.ArgumentList.Add(a); }
        var p = Process.Start(start) ?? throw new InvalidOperationException($"{path} did not start");
        started[p.Id] = p;
        return p.Id;
    }

    public bool IsRunning(int pid)
    {
        if (started.TryGetValue(pid, out var mine))
        {
            if (!mine.HasExited) { return true; }
            if (started.TryRemove(pid, out _)) { mine.Dispose(); }
            return false;
        }
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Kill(int pid)
    {
        if (started.TryGetValue(pid, out var mine))
        {
            try { if (!mine.HasExited) { mine.Kill(entireProcessTree: false); } }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return;
        }
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: false); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public DateTimeOffset? StartedAt(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
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
        OtherProfiles = ProfilesBeside;
        PackageHosts = HostsFromApp;
    }

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);
    // The clock every start and stop timeout is measured on; tests step or hold it. The stop's
    // once read the wall clock, so a test holding a host mid-stop raced it: on a busy Arm64
    // runner the stop gave up after its 1 s before the test let the host go (#425).
    public TimeProvider Time { get; init; } = TimeProvider.System;
    // Whether this app runs elevated; tests pretend.
    public Func<bool> Elevated { get; init; } = PeerPolicy.SelfElevated;

    public InstallLayout Layout => layout;
    public string TaskName => LogonTaskXml.TaskName(layout.Profile, userSid);
    public bool HostInstalled => File.Exists(layout.HostExe) && File.Exists(layout.LauncherExe);
    public bool StartsAtSignIn => tasks.Exists(TaskName);

    public async Task<string> RemoveOwnedHooksAsync(ICommandRunner commands, CancellationToken ct = default)
    {
        var result = await commands.RunAsync(layout.HostExe, ["hooks", "cleanup", "--profile", layout.Profile], TimeSpan.FromSeconds(10), ct);
        if (!result.Ran) { throw new InvalidOperationException("Hook cleanup could not start; reinstall the app to repair the retained profile."); }
        if (result.TimedOut) { throw new TimeoutException("Hook cleanup timed out; profile data and the private runtime were retained for repair."); }
        if (result.ExitCode != 0) { throw new InvalidOperationException($"Hook cleanup incomplete (exit {result.ExitCode}); reinstall the app and use Settings to repair the retained profile."); }
        return result.Output.Trim();
    }

    public void SetStartAtSignIn(bool on)
    {
        if (on)
        {
            if (!HostInstalled) { throw new InvalidOperationException($"the host is not beside the app ({layout.HostExe})"); }
            tasks.Register(TaskName, LogonTaskXml.Build(userId, layout.LauncherExe, layout.ServeArguments, $"{Product.Name} host for profile \"{layout.Profile}\": shows this machine's agent sessions to the {Product.App} and the user's devices."));
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

    // As IsRunningAsync, for stopping: a host that refuses this app's connection (one of them
    // elevated and the other not, LocalRefusedException) is running, and refused, so it and its
    // launcher are left alone rather than taken for a host that is not there and ended by force.
    async Task<bool> AnswersAsync(CancellationToken ct)
    {
        try
        {
            await using var ch = await connect(ct);
            await ch.HelloAsync(ct);
            return true;
        }
        catch (LocalRefusedException e) { throw new HostRefusedException(e.Message); }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    // Starts the host if it is not running: through the logon task when there is one, so it
    // runs as it would at sign-in, otherwise through its launcher. Waits until it answers. An
    // elevated app never starts it directly: the host would be elevated too, unable to type
    // into ordinary agent windows and refusing the app once it runs unelevated again. The
    // task always runs it unelevated.
    public async Task StartAsync(CancellationToken ct = default)
    {
        // Whatever starts it now, a stop asked for earlier is over (#106).
        ClearStopped();
        if (await IsRunningAsync(ct)) { return; }
        if (!HostInstalled) { throw new InvalidOperationException($"the host is not beside the app ({layout.HostExe})"); }
        if (tasks.Exists(TaskName)) { await RunTaskAsync(ct); return; }
        if (Elevated()) { throw new InvalidOperationException("this app is running as administrator, so it will not start the host; turn on starting the host at sign-in, or open the app normally"); }
        // A launcher this app started is still there, between crash restarts: the host it brings
        // up next is waited for, as the task path waits for the task's run, rather than a second
        // launcher started beside it and the first one's pid lost (#106 review).
        if (launcher is not { } pid || !processes.IsRunning(pid)) { launcher = processes.Start(layout.LauncherExe, layout.ServeArguments); }
        await WaitAsync(running: true, StartTimeout, ct);
    }

    // The launcher this app started directly (no logon task), which the task cannot end.
    int? launcher;

    // The launcher's stop marker (HostLauncherContract.StopMarker): while it is there, a launcher
    // that is between crash restarts, or whose host has just ended, starts no host (#106).
    string StopMarker => Path.Combine(layout.ProfileFolder, HostLauncherContract.StopMarker);

    void MarkStopped()
    {
        marked = true;
        try
        {
            Directory.CreateDirectory(layout.ProfileFolder);
            File.WriteAllText(StopMarker, "");
        }
        // The launcher is still ended below; the marker only spares it being ended by force.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void ClearStopped()
    {
        try { File.Delete(StopMarker); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // The task runs agent-bridge-hostw, which lasts as long as the host it waits for, and is
    // registered IgnoreNew, so sign-in never runs a second host. A Run while the instance of a
    // host just stopped is still on its way out is therefore dropped without a word: on
    // 2026-11-02 a Restart stopped the host, ran the task into that instance, and reported "the
    // host did not start" (#280). So the task is run only once its last instance has ended, waited
    // for up to the start timeout; and while one runs, a host it is still bringing up (a Start
    // pressed during sign-in) is as good as one this started. The host a run starts then gets the
    // whole start timeout of its own, so one run just as the wait ran out is not judged at once.
    // Run once only: a run that brings no host up is a failed start, reported as one, not retried.
    async Task RunTaskAsync(CancellationToken ct)
    {
        var deadline = Time.GetUtcNow() + StartTimeout;
        var ran = false;
        while (true)
        {
            if (!ran && !tasks.IsRunning(TaskName))
            {
                tasks.Run(TaskName);
                ran = true;
                deadline = Time.GetUtcNow() + StartTimeout;
            }
            if (await IsRunningAsync(ct)) { return; }
            if (Time.GetUtcNow() > deadline)
            {
                throw new TimeoutException(ran ? "the host did not start; see its log" : $"the host did not start: its logon task's last run had not ended after {StartTimeout.TotalSeconds:0} s");
            }
            await Task.Delay(Poll, ct);
        }
    }

    // An update between the host stopping for it and being applied (Settings' Update and restart,
    // AutoUpdater), which the app quitting must not strand: the stop, and the start after a failed
    // apply, are kept here for Quit to finish. Once quitting, no update stops the host.
    readonly object updating = new();
    Task? updateStop;
    Task? updateRestart;
    bool installing;
    bool quitting;
    // Quitting gives up an update's waits for usage probes (never the probes), so Quit can start
    // a host already stopped for the update within its own short wait.
    readonly CancellationTokenSource quit = new();

    public bool UpdateUnderWay { get { lock (updating) { return updateStop is not null; } } }

    // Stops the host, then applies an update, which ends this process into the new version, whose
    // start brings the host back. Should applying throw, or the app begin quitting meanwhile, the
    // host is started again (by Quit, if quitting) and the error rethrown. One at a time: Settings
    // and the automatic updates may both try, and the second is refused (UpdateUnderWayException)
    // before its begin (what the new version announces) is recorded over the first's.
    public async Task StopAndApplyAsync(Action begin, Action apply)
    {
        Task stop;
        lock (updating)
        {
            if (quitting) { throw new OperationCanceledException("the app is quitting"); }
            if (installing) { throw new UpdateUnderWayException(); }
            begin();
            installing = true;
            // On the thread pool, so Quit can wait for it from the UI thread.
            // The other profiles' hosts first, which the update ends without a stop of their own.
            var giveUp = quit.Token;
            stop = updateStop = Task.Run(async () =>
            {
                var own = await OwnPidAsync();
                var ownStarted = own is { } o ? processes.StartedAt(o) : null;
                await OthersDoneAsync(own, giveUp);
                await StopAsync(ProbeWait, CancellationToken.None, giveUp, own is { } h ? (h, ownStarted) : null);
                // Again just before the hand-over: every other host's hold is fresh, and one that
                // let a probe start while this host was waited for is waited for too.
                await OthersDoneAsync(own, giveUp);
            });
        }
        try
        {
            await stop;
            // Held while applying, so a Quit on another thread waits for it rather than finding the
            // host stopped and starting it under the update.
            lock (updating)
            {
                if (quitting) { throw new OperationCanceledException("the app is quitting"); }
                apply();
            }
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            Task? restart;
            lock (updating)
            {
                updateStop = null;
                restart = quitting ? null : updateRestart = Task.Run(() => StartAsync());
            }
            if (restart is null) { throw; }
            try { await restart; }
            catch (Exception again) when (again is not (OutOfMemoryException or StackOverflowException))
            {
                throw new InvalidOperationException($"{e.Message}; and the host did not start again: {again.Message}", e);
            }
            finally { lock (updating) { updateRestart = null; } }
            throw;
        }
        finally { lock (updating) { installing = false; } }
    }

    // The app quitting: an update that had stopped the host, or was stopping it, and not applied,
    // leaves it running again rather than down until the next sign-in or app start. Waits for the
    // stop to finish first: until then the old host still answers, and starting it would do
    // nothing. Bounded, since it runs on the way out.
    public void Quit(TimeSpan wait, Action<string> log)
    {
        Task? stop, restart;
        lock (updating)
        {
            quitting = true;
            (stop, restart) = (updateStop, updateRestart);
            updateStop = null;
        }
        // Outside the lock: what it ends may take the lock on its way out.
        quit.Cancel();
        if (stop is null && restart is null) { return; }
        try
        {
            var back = Task.Run(async () =>
            {
                if (restart is not null) { await restart; return; }
                // A stop that failed or was refused still leaves the host to be started, or running.
                try { await stop!; }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { }
                await StartAsync();
            }).Wait(wait);
            if (!back) { log($"the host stopped for an update was not back within {wait.TotalSeconds:0} s"); }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { log($"the host stopped for an update did not start again: {e.GetBaseException().Message}"); }
    }

    // Asks the host to stop and waits for its pipe to go; a host that will not go is ended. One
    // that answers no is not: it is running, and refused this process (one inside an agent
    // session, design §6.3 invariant 13), so ending it anyway would be the very thing it refused.
    // The 2026-10-29 review found an agent could run this through the app's uninstall hook.
    // The app quitting gives up the wait for probes, as it does an update's (#238 review).
    public Task StopAsync(CancellationToken ct = default) => StopAsync(ProbeWait, ct, quit.Token);

    // One stop at a time: Settings' Stop, Restart and an update's stop may all wait on the same
    // usage probe. Two at once had the second, finding the host gone under the first's stop, report
    // it ended unexpectedly and clear the marker the first had just written (#238 review), so a
    // host crashing on its way out was started again (#346). The second now begins after the first,
    // and finds the host stopped.
    readonly SemaphoreSlim stopping = new(1, 1);
    // Whether the stop under way wrote the marker: only that one is its to clear.
    bool marked;

    // hold: gives up the wait for the probes only, before anything is stopped. host: the host this
    // profile ran when an update began (its pid, and when it started), which must not outlive a stop
    // that never reached it.
    async Task StopAsync(TimeSpan probeWait, CancellationToken ct, CancellationToken hold = default, (int Pid, DateTimeOffset? Started)? host = null)
    {
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, hold))
        {
            await stopping.WaitAsync(waiting.Token);
        }
        try
        {
            marked = false;
            // A host that answered when the update began but not now (its pipe hung, say) was never
            // held, nor asked to stop: still running, the update would end it, and a standalone Codex
            // probe with it, part-way through refreshing its sign-in (#197 E4). Refused, as a host
            // of the package that cannot be reached is, before the launcher is touched (#238 review).
            if (!await StopHostAsync(probeWait, ct, hold) && host is { } h && processes.IsRunning(h.Pid)
                && (h.Started is not { } began || processes.StartedAt(h.Pid) is not { } now || now == began))
            {
                throw new HostBusyException($"this profile's host (pid {h.Pid}) could not be reached to stop it and is still running, so its usage probes cannot be held and nothing was stopped; try again later");
            }
            await EndLauncherAsync(probeWait, ct, hold);
        }
        // Not stopped: refused, still finishing a usage probe (#238), or the wait for its probes
        // given up. It runs on, and is still to be started again should it crash.
        catch (Exception e) when (e is HostRefusedException or HostBusyException
            || (e is OperationCanceledException && hold.IsCancellationRequested && !ct.IsCancellationRequested))
        {
            if (marked) { ClearStopped(); }
            throw;
        }
        finally { stopping.Release(); }
    }

    // Through the host's pipe, as above, once its usage probes are done (#238). Whether it reached a
    // host and told it to stop: false when nothing answered, which is taken as no host running.
    async Task<bool> StopHostAsync(TimeSpan probeWait, CancellationToken ct, CancellationToken hold = default)
    {
        int? pid = null;
        var reached = false;
        try
        {
            await using var ch = await connect(ct);
            await ch.HelloAsync(ct);
            pid = (await ch.CallAsync<HostStatus>(HostOps.Status, ct: ct)).ProcessId;
            using (var holding = CancellationTokenSource.CreateLinkedTokenSource(ct, hold))
            {
                try { await ProbesDoneAsync(ch, probeWait, "the host", holding.Token); }
                // Its pipe went mid-wait because the host ended: "nothing was stopped" would read as
                // though it still ran (#238 review). Said only when its process has gone and nothing
                // answers on its pipe: a hung host does not answer either, but its process is there.
                catch (HostBusyException e) when (e.InnerException is IOException or TimeoutException && pid is { } p && !processes.IsRunning(p))
                {
                    if (await IsRunningAsync(ct)) { throw; }
                    throw new HostBusyException("the host ended unexpectedly while the stop waited for its usage probe to finish; it was not stopped from here, and its launcher, if it has one, starts it again as after any crash", e);
                }
            }
            // A stop on purpose: the launcher must not start the host again after it (#106). Only
            // once its probes are done, though: the wait for them may last minutes with the host
            // running, and a marker left then (the app ending, or the host crashing meanwhile) kept
            // a crashed host down (#238 review). Still before the stop, so a host that crashes while
            // stopping is not started again.
            MarkStopped();
            try { await ch.CallAsync(HostOps.Stop, ct: ct); }
            catch (HostErrorException e) { throw new HostRefusedException(e.Message); }
            reached = true;
        }
        // Refused at the connection (one of the host and this app elevated, the other not): the
        // host is there and said no, as a refused stop does (#106 review).
        catch (LocalRefusedException e) { throw new HostRefusedException(e.Message); }
        // No host answering (none running, a launcher between crash restarts, or one not responding):
        // stopped on purpose all the same, so the launcher starts none (#106).
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException) { MarkStopped(); }
        try { await WaitAsync(running: false, StopTimeout, ct); }
        catch (TimeoutException) when (pid is { } p)
        {
            processes.Kill(p);
            await WaitAsync(running: false, StopTimeout, ct);
        }
        // Its pipe is gone; wait for the process too, so its files can be replaced (an update).
        var deadline = Time.GetUtcNow() + StopTimeout;
        while (pid is { } p && processes.IsRunning(p) && Time.GetUtcNow() < deadline) { await Task.Delay(Poll, ct); }
        return reached;
    }

    // The launcher (agent-bridge-hostw) outlives a host it is about to start again: between crash
    // restarts (3, 6, 12 s), or with the host it restarted still starting, before its pipe opens.
    // Neither answers on the pipe, so a stop through it alone "succeeded" and the launcher then
    // brought the host back (#106's independent review). With the marker written it stops at its
    // next look; a host it was still starting is stopped through its pipe once that opens, or, if
    // this has stopped waiting by then (a launcher from an earlier run of the app, which this cannot
    // see; one ended below), stops itself, since a host the launcher runs looks for the marker once
    // its pipe is open (HostHandover.StoppedFromTheApp). One still there after the start timeout is
    // ended.
    async Task EndLauncherAsync(TimeSpan probeWait, CancellationToken ct, CancellationToken hold = default)
    {
        var deadline = Time.GetUtcNow() + StartTimeout;
        while (LauncherRunning())
        {
            if (Time.GetUtcNow() > deadline)
            {
                if (tasks.Exists(TaskName)) { tasks.Stop(TaskName); }
                if (launcher is { } pid) { processes.Kill(pid); }
                break;
            }
            if (await AnswersAsync(ct))
            {
                await StopHostAsync(probeWait, ct, hold);
                continue;
            }
            await Task.Delay(Poll, ct);
        }
        launcher = null;
    }

    bool LauncherRunning() =>
        tasks.Exists(TaskName) && tasks.IsRunning(TaskName) || launcher is { } pid && processes.IsRunning(pid);

    // For Velopack's update hook, which has 15 s (docs.velopack.io/integrating/hooks): a host the app already
    // stopped (every update the app starts) is left alone, so no stop is begun only to be abandoned.
    // One still running is stopped only once its usage probes are done within probeWait; otherwise
    // it is not asked to stop at all, and the reason is returned: Velopack ends it with the package.
    // A host its pipe cannot reach is taken as stopped here: Velopack ends what runs from the package
    // whatever this says, so the hooks are best effort (docs/app-windows.md).
    public async Task<string?> StopWithinAsync(TimeSpan probeWait, CancellationToken ct = default)
    {
        if (!await IsRunningAsync(ct)) { return null; }
        try { await StopAsync(probeWait, ct); }
        catch (HostBusyException e) { return e.Message; }
        return null;
    }

    // Restarting is stopping first, so a refused stop starts nothing either.
    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    // How long a stop waits for a usage probe tied to the host to be done (#238): Codex's
    // standalone app-server, whose input goes with the host, given up on as hung only at 10
    // minutes, then its grace.
    public TimeSpan ProbeWait { get; init; } = TimeSpan.FromMinutes(11);
    public TimeSpan ProbePoll { get; init; } = TimeSpan.FromSeconds(1);
    // One host.probes.hold call: a host that does not answer within it cannot say, and is not stopped.
    public TimeSpan ProbeCallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // The other profiles' folders beside this one, whose hosts an update ends too; the pipe of one;
    // and the hosts running from this installation, by process id (by default, agent-bridge-host
    // processes whose executable is under the app's folder; one that cannot be looked at counts, as
    // for PackageProcesses: not knowing is not permission).
    public Func<IEnumerable<string>> OtherProfiles { get; init; }
    public Func<string, CancellationToken, Task<LocalChannel>> ConnectTo { get; init; } =
        (folder, ct) => LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromSeconds(1), ct);
    public Func<IReadOnlyCollection<int>> PackageHosts { get; init; }

    IEnumerable<string> ProfilesBeside()
    {
        if (!Directory.Exists(layout.DataRoot)) { return []; }
        return Directory.EnumerateDirectories(layout.DataRoot)
            .Where(d => !string.Equals(Path.GetFileName(d), layout.Profile, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFullPath(d), Path.GetFullPath(layout.AppDataFolder), StringComparison.OrdinalIgnoreCase));
    }

    HashSet<int> HostsFromApp()
    {
        var root = Path.GetFullPath(layout.AppFolder);
        var hosts = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(layout.HostExe)))
        {
            using (p)
            {
                string? path;
                try { path = p.MainModule?.FileName; }
                catch (InvalidOperationException) { continue; }
                catch (System.ComponentModel.Win32Exception) { path = null; }
                if (path is null || path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { hosts.Add(p.Id); }
            }
        }
        return hosts;
    }

    async Task<int?> OwnPidAsync()
    {
        try
        {
            await using var ch = await connect(CancellationToken.None);
            await ch.HelloAsync();
            return (await ch.CallAsync<HostStatus>(HostOps.Status, timeout: ProbeCallTimeout)).ProcessId;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException) { return null; }
    }

    // Before an update (#238): Velopack ends every process in the package, the other profiles'
    // hosts too, so their usage probes are held and waited for as this profile's are. One that
    // cannot confirm stops the update. They are left running: the update ends them once this host
    // has stopped. Every one is held again each round, so no hold lapses while another is waited
    // for, and the package is looked at again once they are done: a host started during the wait
    // was never held. own: this profile's host, which its own stop holds.
    async Task OthersDoneAsync(int? own, CancellationToken ct)
    {
        var deadline = Time.GetUtcNow() + ProbeWait;
        while (true)
        {
            var seen = await OthersHeldAsync(own, deadline, ct);
            if (PackageHosts().All(p => p == own || seen.Contains(p))) { return; }
            if (Time.GetUtcNow() >= deadline) { throw new HostBusyException($"nothing was stopped: hosts kept starting from this installation for {FormatWait(ProbeWait)}; try again later"); }
        }
    }

    // One look at the package: every other host of it held until none is busy. The hosts held.
    async Task<HashSet<int>> OthersHeldAsync(int? own, DateTimeOffset deadline, CancellationToken ct)
    {
        var package = PackageHosts();
        var seen = new HashSet<int>();
        var reached = new List<(string Who, LocalChannel Channel)>();
        try
        {
            foreach (var folder in OtherProfiles())
            {
                var who = $"profile {Path.GetFileName(folder)}'s host";
                LocalChannel ch;
                // Nothing answering there; whether a host of the package went unreached is asked below.
                try { ch = await ConnectTo(folder, ct); }
                catch (Exception e) when (e is IOException or TimeoutException or HostErrorException or OperationCanceledException && !ct.IsCancellationRequested) { continue; }
                int pid;
                try
                {
                    await ch.HelloAsync(ct);
                    pid = (await ch.CallAsync<HostStatus>(HostOps.Status, timeout: ProbeCallTimeout, ct: ct)).ProcessId;
                }
                catch (Exception e) when (e is IOException or TimeoutException or HostErrorException)
                {
                    await ch.DisposeAsync();
                    throw new HostBusyException($"{who} could not say whether a usage probe it started is still finishing, so nothing was stopped: {e.Message}");
                }
                if (!package.Contains(pid)) { await ch.DisposeAsync(); continue; }
                seen.Add(pid);
                reached.Add((who, ch));
            }
            // A host of the package no pipe reached (one at another elevation refuses this app's
            // connection, say) cannot be held, and the update would end it all the same.
            if (package.Where(p => p != own && !seen.Contains(p)).Select(p => (int?)p).FirstOrDefault() is { } missed)
            {
                throw new HostBusyException($"a host running from this installation (pid {missed}) could not be reached to hold its usage probes, so nothing was stopped");
            }
            while (true)
            {
                string? busy = null;
                foreach (var (who, ch) in reached)
                {
                    if (await HoldAsync(ch, who, ct) is { } why) { busy ??= $"{who}: {why}"; }
                }
                if (busy is null) { return seen; }
                if (Time.GetUtcNow() >= deadline) { throw new HostBusyException($"nothing was stopped: {busy}, after {FormatWait(ProbeWait)}; try again later"); }
                await Task.Delay(ProbePoll, ct);
            }
        }
        finally
        {
            foreach (var (_, ch) in reached) { await ch.DisposeAsync(); }
        }
    }

    // One host.probes.hold: null when nothing of the host's is finishing (or it is an older host,
    // whose "unknown op" means it runs no such probe), else what is. A host that cannot say throws.
    async Task<string?> HoldAsync(LocalChannel ch, string who, CancellationToken ct)
    {
        ProbeHoldResult held;
        try { held = await ch.CallAsync<ProbeHoldResult>(HostOps.ProbeHold, timeout: ProbeCallTimeout, ct: ct); }
        catch (HostErrorException e) when (e.Message.StartsWith($"unknown op {HostOps.ProbeHold}", StringComparison.Ordinal)) { return null; }
        catch (HostErrorException e) { throw new HostRefusedException(e.Message); }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            throw new HostBusyException($"{who} could not say whether a usage probe it started is still finishing, so nothing was stopped: {e.Message}", e);
        }
        return held.Busy ? held.Why ?? "a usage probe it started is still finishing" : null;
    }

    // Before the host is stopped, by a person, an update or anything else here: it starts no probe
    // whose process goes with it, and one running is let finish its answers and the grace after
    // them. Velopack ends whatever still runs from the package once the 10 s after Stop are up, so
    // waiting is done here, while the host runs, never after. Only an older host's "unknown op"
    // lets the stop go on unconfirmed (it runs no such probe); a host that cannot say, or that is
    // still busy at ProbeWait, is not stopped at all, and never reaches the kill below.
    async Task ProbesDoneAsync(LocalChannel ch, TimeSpan wait, string who, CancellationToken ct)
    {
        var deadline = Time.GetUtcNow() + wait;
        while (await HoldAsync(ch, who, ct) is { } why)
        {
            if (Time.GetUtcNow() >= deadline) { throw new HostBusyException($"nothing was stopped: {who}: {why}, after {FormatWait(wait)}; try again later"); }
            await Task.Delay(ProbePoll, ct);
        }
    }

    static string FormatWait(TimeSpan wait) => wait >= TimeSpan.FromMinutes(1) ? $"{wait.TotalMinutes:0} minutes" : $"{wait.TotalSeconds:0} s";

    async Task WaitAsync(bool running, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Time.GetUtcNow() + timeout;
        while (await IsRunningAsync(ct) != running)
        {
            if (Time.GetUtcNow() > deadline) { throw new TimeoutException(running ? "the host did not start; see its log" : "the host did not stop"); }
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
    const int TaskStateRunning = 4;

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

    // IRegisteredTask.Stop: ends every running instance of it. A task that is gone has none.
    public void Stop(string name) => Use(folder =>
    {
        object? task;
        try { task = Call(folder, "GetTask", name); }
        catch (Exception e) when (e is COMException or FileNotFoundException) { return false; }
        try { Call(task!, "Stop", 0); }
        finally { Release(task); }
        return true;
    });

    // IRegisteredTask.State: TASK_STATE_RUNNING while any instance of it runs.
    public bool IsRunning(string name) => Use(folder =>
    {
        object? task;
        try { task = Call(folder, "GetTask", name); }
        catch (Exception e) when (e is COMException or FileNotFoundException) { return false; }
        try { return Convert.ToInt32(Invoke(task!, "State", System.Reflection.BindingFlags.GetProperty, []), System.Globalization.CultureInfo.InvariantCulture) == TaskStateRunning; }
        finally { Release(task); }
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
    internal static object? Call(object target, string name, params object?[] args) =>
        Invoke(target, name, System.Reflection.BindingFlags.InvokeMethod, args);

    static object? Invoke(object target, string name, System.Reflection.BindingFlags flags, object?[] args)
    {
        try { return target.GetType().InvokeMember(name, flags, null, target, args); }
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

// The host was not stopped: a usage probe tied to it is still finishing, or it could not say (#238).
public sealed class HostBusyException(string reason, Exception? inner = null) : InvalidOperationException(reason, inner);

// Another update (Settings', or the automatic one) is stopping the host or being applied.
public sealed class UpdateUnderWayException() : InvalidOperationException("an update is already being installed");
