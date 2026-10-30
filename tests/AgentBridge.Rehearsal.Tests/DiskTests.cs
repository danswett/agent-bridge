using AgentBridge.Host.Delivery.Staging;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class DiskTests
{
    [Fact]
    public void A_staging_folder_only_this_user_can_open_passes_and_a_file_the_host_did_not_name_fails()
    {
        using var s = new Scratch();
        var profile = Path.Combine(s.Root, "profile");
        var folder = PrivateFolder.Ensure(Path.Combine(profile, "attachments"));
        PrivateFolder.WriteNew(Path.Combine(folder, "0123456789abcdef0123456789abcdef-photo.png"), [1, 2, 3]);
        var elsewhere = Path.Combine(s.Root, "not-temp");
        Assert.Equal(Verdict.Pass, Disk.Staging(profile, 1, elsewhere).Verdict);
        Assert.Equal(Verdict.Fail, Disk.Staging(profile, 2, elsewhere).Verdict);
        // The scratch folder is under TEMP, which staging must never be.
        Assert.Contains("TEMP", Disk.Staging(profile, null).Detail, StringComparison.Ordinal);
        PrivateFolder.WriteNew(Path.Combine(folder, "photo.png"), [1]);
        Assert.Equal(Verdict.Fail, Disk.Staging(profile, null, elsewhere).Verdict);
    }

    [Fact]
    public void Worktrees_are_counted_by_the_marker_each_bridge_writes_in_their_git_folder()
    {
        using var s = new Scratch();
        var repo = Path.Combine(s.Root, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "first");
        foreach (var name in new[] { "host-tree", "old-tree", "own-tree" })
        {
            Git(repo, "worktree", "add", "-q", "--detach", Path.Combine(s.Root, name));
        }
        File.WriteAllText(Path.Combine(AdminFolder(Path.Combine(s.Root, "host-tree")), "agent-bridge-host-created"), "2026-10-28T10:00:00-07:00");
        File.WriteAllText(Path.Combine(AdminFolder(Path.Combine(s.Root, "old-tree")), "agent-bridge-created"), "x");

        Assert.Equal(Verdict.Pass, Disk.Worktrees(repo, 1, 1, 1).Verdict);
        Assert.Equal(Verdict.Fail, Disk.Worktrees(repo, 2, null, null).Verdict);
        Assert.Equal(Verdict.Fail, Disk.Worktrees(repo, null, null, 0).Verdict);
    }

    static string AdminFolder(string worktree) => Git(worktree, "rev-parse", "--absolute-git-dir").Trim();

    static string Git(string dir, params string[] args)
    {
        var r = AgentBridge.Host.Delivery.Launch.Git.Run(dir, args);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Error}");
        return r.Output;
    }
}
