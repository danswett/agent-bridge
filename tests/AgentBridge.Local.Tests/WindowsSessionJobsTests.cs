using System.Diagnostics;
using Xunit;

namespace AgentBridge.Local.Tests;

// design/agent-principals.md §3.4, "Session job": the real Windows job, with real processes. A job has no name, so a test can never meet a
// running session's job.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsSessionJobsTests : IDisposable
{
    readonly List<Process> started = [];
    static readonly string Key = new string('A', 32) + new string('B', 32);

    public void Dispose()
    {
        foreach (var p in started) { try { if (!p.HasExited) { p.Kill(true); } } catch (Exception) { } p.Dispose(); }
    }

    static bool Windows => OperatingSystem.IsWindows();

    WindowsSessionJobs Jobs() => new();

    static DateTimeOffset StartOf(Process p) => new(p.StartTime.ToUniversalTime());

    Process Sleeper()
    {
        var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 120") { CreateNoWindow = true, UseShellExecute = false })!;
        started.Add(p);
        return p;
    }

    [Fact]
    public void A_job_is_only_ever_reached_through_the_handle_that_made_it_and_the_recorded_root_must_be_in_it()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var enrolled = Sleeper();
        var other = Sleeper();
        jobs.Enroll(Key, enrolled.Id, StartOf(enrolled));
        Assert.False(jobs.Contains(Key, other.Id, StartOf(other), enrolled.Id, StartOf(enrolled)));
        Assert.True(jobs.Contains(Key, enrolled.Id, StartOf(enrolled), enrolled.Id, StartOf(enrolled)));
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
    public void A_release_that_races_a_question_never_pulls_the_handle_out_from_under_it()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var root = Sleeper();
        jobs.Enroll(Key, root.Id, StartOf(root));
        // The credential goes between finding the job and using it: that question is answered with the handle it started with.
        jobs.AfterLookup = key => jobs.Release(key);
        Assert.True(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
        jobs.AfterLookup = null;
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
        Assert.Null(jobs.LimitFlags(Key));
    }

    [Fact]
    public void The_jobs_handle_is_not_inheritable_so_no_child_the_host_starts_can_carry_it()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var root = Sleeper();
        jobs.Enroll(Key, root.Id, StartOf(root));
        Assert.False(jobs.IsInheritable(Key));
    }

    [Fact]
    public void The_job_only_permits_break_away_and_letting_it_go_ends_nothing()
    {
        if (!Windows) { return; }
        var jobs = Jobs();
        var root = Sleeper();
        jobs.Enroll(Key, root.Id, StartOf(root));
        Assert.Equal(0x0800u, jobs.LimitFlags(Key));
        jobs.Release(Key);
        Assert.Null(jobs.LimitFlags(Key));
        Assert.False(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
        Thread.Sleep(300);
        Assert.False(root.HasExited);
        jobs.Enroll(Key, root.Id, StartOf(root));
        Assert.True(jobs.Contains(Key, root.Id, StartOf(root), root.Id, StartOf(root)));
    }
}
