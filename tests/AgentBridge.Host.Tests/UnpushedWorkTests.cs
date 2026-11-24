using System.Diagnostics;
using AgentBridge.Host;
using Xunit;

// Real git, no fakes: the check decides whether an agent's End could lose work (#2741), so each way of losing work
// has a case, and "git failed" must never read as "clean" (a dropped check or a swallowed failure turns these RED).
public sealed class UnpushedWorkTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "unpushed-" + Guid.NewGuid().ToString("N")[..8]);
    string Work => Path.Combine(root, "clone");

    public UnpushedWorkTests()
    {
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(remote); Directory.CreateDirectory(Work);
        Git(remote, "init", "--bare", "-b", "main");
        Git(Work, "init", "-b", "main");
        File.WriteAllText(Path.Combine(Work, "a.txt"), "a");
        Git(Work, "add", "a.txt"); Git(Work, "commit", "-m", "a");
        Git(Work, "remote", "add", "origin", remote); Git(Work, "push", "-u", "origin", "main");
    }
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        Directory.Delete(root, true);
    }

    static void Git(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false" }.Concat(args)) { info.ArgumentList.Add(a); }
        using var git = Process.Start(info)!;
        git.StandardOutput.ReadToEnd(); git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
    }

    [Fact] public void A_clean_pushed_repository_is_not_pending() => Assert.False(UnpushedWork.Pending(Work));
    [Fact] public void An_uncommitted_change_is_pending() { File.WriteAllText(Path.Combine(Work, "a.txt"), "changed"); Assert.True(UnpushedWork.Pending(Work)); }
    [Fact]
    public void Untracked_work_is_pending_even_when_the_repository_hides_untracked_files()
    {
        Git(Work, "config", "status.showUntrackedFiles", "no");
        File.WriteAllText(Path.Combine(Work, "new.txt"), "work in progress");
        Assert.True(UnpushedWork.Pending(Work));
    }
    [Fact]
    public void A_commit_not_pushed_is_pending()
    {
        File.WriteAllText(Path.Combine(Work, "b.txt"), "b");
        Git(Work, "add", "b.txt"); Git(Work, "commit", "-m", "b");
        Assert.True(UnpushedWork.Pending(Work));
    }
    [Fact]
    public void A_commit_on_another_local_branch_is_pending_and_so_is_a_branch_with_no_upstream()
    {
        Git(Work, "checkout", "-b", "side");
        File.WriteAllText(Path.Combine(Work, "c.txt"), "c");
        Git(Work, "add", "c.txt"); Git(Work, "commit", "-m", "c");
        Git(Work, "checkout", "main");
        Assert.True(UnpushedWork.Pending(Work));
        Git(Work, "push", "-u", "origin", "side");
        Assert.False(UnpushedWork.Pending(Work));
        Git(Work, "checkout", "side");
        File.AppendAllText(Path.Combine(Work, "c.txt"), "more");
        Git(Work, "commit", "-am", "more"); Git(Work, "checkout", "main");
        Assert.True(UnpushedWork.Pending(Work));
    }
    [Fact]
    public void A_stash_is_pending()
    {
        File.WriteAllText(Path.Combine(Work, "a.txt"), "stashed");
        Git(Work, "stash");
        Assert.True(UnpushedWork.Pending(Work));
    }
    [Fact]
    public void A_failing_git_status_alone_is_pending_never_clean()
    {
        // Everything else in this repository is clean and pushed: only the unreadable index can make it pending.
        File.WriteAllText(Path.Combine(Work, ".git", "index"), "not an index");
        Assert.True(UnpushedWork.Pending(Work));
    }
    [Fact]
    public void A_git_descendant_that_outlives_git_holding_its_pipe_is_refused_within_one_overall_bound()
    {
        // A clean filter that backgrounds a process still holding git's stderr: git itself exits at once, but its
        // stderr pipe stays open for 30 s. Reading it must not outlast the one 5 s bound (the fleet.end lock is held meanwhile).
        File.WriteAllText(Path.Combine(Work, ".gitattributes"), "a.txt filter=slow\n");
        Git(Work, "config", "filter.slow.clean", "cat; (sleep 30 >/dev/null &)");
        File.WriteAllText(Path.Combine(Work, "a.txt"), "b"); // same size as the committed "a", so git must hash it and run the filter
        var clock = Stopwatch.StartNew();
        Assert.True(UnpushedWork.Pending(Work));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(9), $"took {clock.Elapsed}");
    }
    [Fact]
    public void A_folder_git_cannot_read_is_pending_never_clean()
    {
        Assert.True(UnpushedWork.Pending(root));
        Assert.True(UnpushedWork.Pending(Path.Combine(root, "missing")));
    }
}
