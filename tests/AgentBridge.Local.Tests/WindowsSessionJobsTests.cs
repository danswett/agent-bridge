using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentBridge.Local;
using Xunit;

namespace AgentBridge.Local.Tests;

// design/agent-principals.md §3.4, "Session job": the real Windows job, with real processes. A job has no name, so a test can never meet a
// running session's job.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsSessionJobsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abjobs-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<Process> started = [];
    static readonly string Key = new string('A', 32) + new string('B', 32);

    public WindowsSessionJobsTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        foreach (var p in started) { try { if (!p.HasExited) { p.Kill(true); } } catch (Exception) { } p.Dispose(); }
        foreach (var p in Stray) { try { using var q = Process.GetProcessById(p); q.Kill(); } catch (Exception) { } }
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    readonly List<int> Stray = [];

    static bool Windows => OperatingSystem.IsWindows();

    WindowsSessionJobs Jobs() => new();

    static DateTimeOffset StartOf(Process p) => new(p.StartTime.ToUniversalTime());

    Process Sleeper()
    {
        var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 120") { CreateNoWindow = true, UseShellExecute = false })!;
        started.Add(p);
        return p;
    }

    // Root R sleeps, then starts M; M starts C, writes C's pid and exits. C is then a grandchild whose parent is gone.
    (Process Root, int Child) RootWithOrphanedGrandchild(Func<Process, bool>? enroll = null)
    {
        var pidFile = Path.Combine(folder, "child.pid");
        File.WriteAllText(Path.Combine(folder, "m.ps1"),
            $"$c = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120'\r\n$c.Id | Set-Content -LiteralPath '{pidFile}'\r\n");
        File.WriteAllText(Path.Combine(folder, "r.ps1"),
            $"Start-Sleep -Seconds 4\r\nStart-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-File','{Path.Combine(folder, "m.ps1")}'\r\nStart-Sleep -Seconds 120\r\n");
        var root = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -File \"{Path.Combine(folder, "r.ps1")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        started.Add(root);
        Assert.True(enroll?.Invoke(root) ?? true);
        for (var i = 0; i < 300 && !File.Exists(pidFile); i++) { Thread.Sleep(100); }
        Assert.True(File.Exists(pidFile), "the grandchild never started");
        Thread.Sleep(300);
        var child = int.Parse(File.ReadAllText(pidFile).Trim());
        Stray.Add(child);
        return (root, child);
    }

    [Fact]
    public void A_descendant_whose_parent_has_exited_is_in_the_job_and_a_stranger_is_not()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var (root, child) = RootWithOrphanedGrandchild(r => { jobs.Enroll(Key, r.Id, StartOf(r)); return true; });
        using var grandchild = Process.GetProcessById(child);
        // It really is an orphan: its parent chain stops before the root.
        Assert.DoesNotContain(ProcessChain.Read(child).Processes, p => p.Id == root.Id);
        Assert.True(jobs.Contains(Key, root.Id, StartOf(root), child, StartOf(grandchild)));
        Assert.True(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));

        var stranger = Sleeper();
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root), stranger.Id, StartOf(stranger)));
        // The host's own handle is closed and the root has not been ended: the job needs no one holding it.
        Assert.False(root.HasExited);
    }

    [Fact]
    public void A_job_is_only_ever_reached_through_the_handle_that_made_it_and_the_recorded_root_must_be_in_it()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var enrolled = Sleeper();
        var other = Sleeper();
        jobs.Enroll(Key, enrolled.Id, StartOf(enrolled));
        // The root this credential recorded is not in its job: the job is not believed.
        Assert.False(jobs.Contains(Key, other.Id, StartOf(other), enrolled.Id, StartOf(enrolled)));
        Assert.True(jobs.Contains(Key, enrolled.Id, StartOf(enrolled), enrolled.Id, StartOf(enrolled)));
        // Another holder (another session, another host object) has no way to name or open it, whatever key it tries.
        Assert.False(Jobs().Contains(Key, enrolled.Id, StartOf(enrolled), enrolled.Id, StartOf(enrolled)));
        Assert.False(jobs.Contains(new string('C', 64), enrolled.Id, StartOf(enrolled), enrolled.Id, StartOf(enrolled)));
    }

    [Fact]
    public void A_recycled_id_a_missing_start_time_or_a_gone_process_is_not_in_the_job()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var root = Sleeper();
        jobs.Enroll(Key, root.Id, StartOf(root));
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root).AddHours(1), root.Id, StartOf(root)));
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root).AddHours(-1)));
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root), root.Id, null));
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root), 0, StartOf(root)));
        Assert.Throws<InvalidOperationException>(() => jobs.Enroll(new string('D', 64), root.Id, StartOf(root).AddHours(1)));
        root.Kill();
        root.WaitForExit();
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
    }

    [Fact]
    public void A_launch_has_one_job_and_a_process_that_cannot_be_enrolled_leaves_none_behind()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var first = Sleeper();
        var second = Sleeper();
        jobs.Enroll(Key, first.Id, StartOf(first));
        Assert.Throws<InvalidOperationException>(() => jobs.Enroll(Key, second.Id, StartOf(second)));
        Assert.False(jobs.Contains(Key, second.Id, StartOf(second), second.Id, StartOf(second)));

        var gone = Sleeper();
        gone.Kill();
        gone.WaitForExit();
        var none = new string('E', 64);
        Assert.ThrowsAny<Exception>(() => jobs.Enroll(none, gone.Id, StartOf(gone)));
        Assert.Null(jobs.LimitFlags(none));
    }

    [Fact]
    public void A_root_already_in_another_job_is_still_enrolled_and_found_through_both()
    {
        if (!Windows) { return; }
        var outer = Jobs();
        var jobs = Jobs();
        var root = Sleeper();
        outer.Enroll(Key, root.Id, StartOf(root));
        jobs.Enroll(Key, root.Id, StartOf(root));
        Assert.True(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
        Assert.True(outer.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
    }

    [Fact]
    public void The_job_only_permits_break_away_and_letting_it_go_ends_nothing()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var root = Sleeper();
        jobs.Enroll(Key, root.Id, StartOf(root));
        // Permission to break away only: never kill-on-close (0x2000), so a host restart cannot end a session.
        Assert.Equal(0x0800u, jobs.LimitFlags(Key));
        jobs.Release(Key);
        Assert.Null(jobs.LimitFlags(Key));
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
        Thread.Sleep(300);
        Assert.False(root.HasExited);
        // After a host restart the root is enrolled again, in a new job.
        jobs.Enroll(Key, root.Id, StartOf(root));
        Assert.True(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
    }
}