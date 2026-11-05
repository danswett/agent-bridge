using System.Diagnostics;
using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using AgentBridge.Local;
using Xunit;

// The launcher's hold on the host, and the app's on the launcher, with real processes (#420): the
// host ends with its launcher but the agents it started do not, and a launcher is known by its
// start time as well as its pid. Windows only, where the launcher runs.
[Trait("Category", "Process")]
public sealed class HostLauncherProcessTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("abl-").FullName;
    readonly List<Process> started = [];

    public void Dispose()
    {
        foreach (var p in started)
        {
            try { if (!p.HasExited) { p.Kill(); } }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    Process Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) { start.ArgumentList.Add(a); }
        var p = Process.Start(start)!;
        started.Add(p);
        return p;
    }

    [Fact]
    public async Task A_host_in_the_launchers_job_ends_with_the_launcher_and_an_agent_it_started_runs_on()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var agentPid = Path.Combine(folder, "agent pid.txt");
        var told = Path.Combine(folder, "told.txt");
        // The "host", started in the job with the launcher's word in its environment and an
        // argument that needs quoting: it starts an "agent" of its own, and waits.
        var job = HostJob.Create();
        try
        {
            using var host = job.Start(System32(@"WindowsPowerShell\v1.0\powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command",
                $"Set-Content -Path \"{told}\" -Value $env:{HostLauncherContract.Variable}; " +
                $"$a = Start-Process -FilePath '{System32("PING.EXE")}' -ArgumentList '-n','300','127.0.0.1' -WindowStyle Hidden -PassThru; " +
                $"Set-Content -Path '{agentPid}.part' -Value $a.Id; Move-Item -LiteralPath '{agentPid}.part' -Destination '{agentPid}'; Start-Sleep -Seconds 300"],
                new Dictionary<string, string> { [HostLauncherContract.Variable] = "1" }, folder);
            // Written aside and moved into place, so it is never read half written or still held.
            await Wait.UntilAsync(() => File.Exists(agentPid), "the host to start its agent", TimeSpan.FromSeconds(60));
            var agentId = int.Parse(File.ReadAllText(agentPid).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            // Held by its handle from now, before the job is closed: if the agent ends with the
            // host (what this catches), its pid can be given to another process, which a look-up
            // by pid would then judge alive, or kill (#478 review, Copilot 4486726235).
            using var agent = Process.GetProcessById(agentId);
            _ = agent.SafeHandle;
            try
            {
                Assert.Equal("1", File.ReadAllText(told).Trim());
                Assert.False(host.HasExited);
                Assert.False(agent.HasExited);

                // The launcher ends: its handle to the job closes, as it does when it is killed.
                job.Dispose();

                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(agent.HasExited, "the agent ended with the host");
            }
            finally
            {
                try { if (!agent.HasExited) { agent.Kill(); } }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        finally { job.Dispose(); }
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData(@"a\\b", @"a\\b")]
    public void An_argument_is_quoted_as_the_host_reads_its_command_line_back(string argument, string quoted)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.Equal(quoted, HostJob.Quote(argument));
    }

    [Fact]
    public async Task A_process_this_started_is_known_until_it_exits_and_ending_it_afterwards_is_no_error()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var processes = new SystemProcesses();

        var pid = processes.Start(System32("cmd.exe"), ["/c", "exit 0"]);

        await Wait.UntilAsync(() => !processes.IsRunning(pid), "the process to exit", TimeSpan.FromSeconds(30));
        processes.Kill(pid);
        Assert.False(processes.IsRunning(pid));
    }

    [Fact]
    public async Task A_launcher_an_earlier_run_of_the_app_started_is_taken_on_only_with_its_own_start_time_and_then_ended()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var launcher = Run(System32("PING.EXE"), "-n", "300", "127.0.0.1");
        var startedTicks = launcher.StartTime.ToUniversalTime().Ticks;
        var processes = new SystemProcesses();

        Assert.False(processes.Adopt(launcher.Id, startedTicks + 1));
        Assert.True(processes.Adopt(launcher.Id, startedTicks));
        Assert.True(processes.IsRunning(launcher.Id));
        processes.Kill(launcher.Id);

        Assert.True(launcher.WaitForExit(TimeSpan.FromSeconds(30)));
        await Wait.UntilAsync(() => !processes.IsRunning(launcher.Id), "the launcher to be seen gone", TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void The_launcher_reads_the_apps_stop_marker_and_forgets_only_a_stop_asked_for_before_it_started()
    {
        var marker = Path.Combine(folder, HostLauncherContract.StopMarker);
        var started = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
        using var files = new LauncherFiles(folder, _ => { });
        Assert.False(files.Stopped());

        // A stop asked for since this launcher started, by an app that could not find it yet.
        File.WriteAllText(marker, "");
        File.SetLastWriteTimeUtc(marker, started.AddSeconds(1));
        Assert.True(files.Stopped());
        files.ClearStoppedBefore(started);
        Assert.True(files.Stopped());

        // One asked for before it started.
        File.SetLastWriteTimeUtc(marker, started.AddSeconds(-1));
        files.ClearStoppedBefore(started);
        Assert.False(files.Stopped());
        Assert.False(File.Exists(marker));
    }

    // The marker's lock (#478 review): a launcher clears an earlier stop only while it holds it,
    // and leaves the stop in place when it cannot have it; the lock's file, left behind by a
    // holder that has gone, blocks nothing.
    [Fact]
    public void A_launcher_clears_an_earlier_stop_only_under_the_markers_lock_and_a_lock_left_behind_blocks_nothing()
    {
        var marker = Path.Combine(folder, HostLauncherContract.StopMarker);
        var started = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
        var logged = new List<string>();
        using var files = new LauncherFiles(folder, logged.Add);
        File.WriteAllText(marker, "");
        File.SetLastWriteTimeUtc(marker, started.AddSeconds(-1));

        using (var held = HostLauncherContract.LockStopMarker(folder, TimeSpan.Zero))
        {
            Assert.NotNull(held);
            files.ClearStoppedBefore(started);
            Assert.True(files.Stopped());
            Assert.Contains(logged, l => l.Contains("left in place", StringComparison.Ordinal));
        }

        Assert.True(File.Exists(Path.Combine(folder, HostLauncherContract.StopMarkerLock)));
        files.ClearStoppedBefore(started);
        Assert.False(files.Stopped());
    }

    [Fact]
    public void Only_one_launcher_at_a_time_holds_the_record_which_the_app_can_read_and_which_goes_with_it()
    {
        // Windows's share modes, which the launcher (Windows only) relies on; elsewhere they are
        // advisory.
        if (!OperatingSystem.IsWindows()) { return; }
        var record = Path.Combine(folder, HostLauncherContract.LauncherRecord);
        var first = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
        var logged = new List<string>();
        var serving = new LauncherFiles(folder, _ => { });
        using var second = new LauncherFiles(folder, logged.Add);

        Assert.True(serving.HoldRecord(77, first));
        Assert.False(second.HoldRecord(78, first.AddMinutes(1)));
        Assert.False(second.HoldRecord(78, first.AddMinutes(1)));
        // Read as the app reads it, while it is held.
        using (var read = new StreamReader(new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            Assert.True(HostLauncherContract.TryParseLauncher(read.ReadToEnd(), out var pid, out var ticks));
            Assert.Equal((77, first.Ticks), (pid, ticks));
        }
        Assert.Single(logged);

        serving.Dispose();
        Assert.False(File.Exists(record));
        Assert.True(second.HoldRecord(78, first.AddMinutes(1)));
    }

    [Theory]
    [InlineData(new[] { "serve", "--profile", "shadow" }, true)]
    [InlineData(new[] { "shadow", "run", "--profile", "shadow" }, false)]
    public void Only_a_serving_launcher_answers_to_the_apps_stop_and_records_itself(string[] args, bool serving)
    {
        using var files = LauncherFiles.ForServe(args, folder, _ => { });

        Assert.Equal(serving, files is not null);
    }
}