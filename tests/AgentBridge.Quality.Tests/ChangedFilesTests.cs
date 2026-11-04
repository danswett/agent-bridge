namespace AgentBridge.Quality.Tests;

// Real git processes in a throwaway repository, so ring 0 leaves it to ring 1.
[Trait("Category", "Process")]
public class ChangedFilesTests
{
    [Fact]
    public void Changes_are_the_branch_commits_plus_staged_unstaged_and_untracked_files_with_moves_at_both_paths()
    {
        var root = Fixture.ScratchFolder();
        try
        {
            Git(root, "init", "-q", "-b", "main");
            Write(root, "kept.txt");
            Write(root, "edited.txt");
            Write(root, "moved.txt");
            Commit(root, "base");
            Git(root, "switch", "-q", "-c", "topic");
            Write(root, "src/committed.cs");
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            Git(root, "mv", "moved.txt", "docs/moved.txt");
            Commit(root, "topic");
            Write(root, "edited.txt", "changed");
            Write(root, "pwa/untracked.css");
            // Staged, then the working tree put back as it was: still a change to commit.
            Write(root, "kept.txt", "staged");
            Git(root, "add", "kept.txt");
            Write(root, "kept.txt", "x");

            var (files, mergeBase) = ChangedFiles.Since(root, "main");
            var (committedOnly, _) = ChangedFiles.Since(root, "main", head: "HEAD");

            Assert.Equal(["docs/moved.txt", "edited.txt", "kept.txt", "moved.txt", "pwa/untracked.css", "src/committed.cs"], files);
            Assert.Equal(["docs/moved.txt", "moved.txt", "src/committed.cs"], committedOnly);
            Assert.Equal(Git(root, "rev-parse", "main").Trim(), mergeBase);
        }
        finally
        {
            Fixture.Delete(root);
        }
    }

    [Fact]
    public void Paths_git_would_quote_come_back_verbatim()
    {
        var root = Fixture.ScratchFolder();
        try
        {
            Git(root, "init", "-q", "-b", "main");
            Write(root, "seed.txt");
            Commit(root, "base");
            // Straight into the index, since Windows can't hold these names on disk. One per line,
            // git prints each quoted ("tools/q/a\"b.cs"), which no glob anchored at tools/ matches.
            var blob = Git(root, "hash-object", "-w", "seed.txt").Trim();
            string[] odd = ["docs/new\nline.md", "src/tab\there.cs", "tools/AgentBridge.Quality/a\"b.cs", "tools/AgentBridge.Quality/back\\slash.cs"];
            foreach (var path in odd)
                Git(root, "-c", "core.protectNTFS=false", "update-index", "--add", "--cacheinfo", $"100644,{blob},{path}");
            Git(root, "-c", "user.name=quality", "-c", "user.email=quality@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "odd");

            var (files, _) = ChangedFiles.Since(root, "HEAD~1", head: "HEAD");

            // A backslash reads as a separator, as it does in a catalog glob: still under tools/.
            Assert.Equal(["docs/new\nline.md", "src/tab\there.cs", "tools/AgentBridge.Quality/a\"b.cs", "tools/AgentBridge.Quality/back/slash.cs"], files);
            Assert.Contains("tools/AgentBridge.Quality/a\"b.cs", ChangedFiles.Tracked(root));
        }
        finally
        {
            Fixture.Delete(root);
        }
    }

    static void Write(string root, string path, string text = "x")
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    static void Commit(string root, string message)
    {
        Git(root, "add", ".");
        Git(root, "-c", "user.name=quality", "-c", "user.email=quality@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
    }

    static string Git(string root, params string[] args) => ChangedFiles.Git(root, args);
}
