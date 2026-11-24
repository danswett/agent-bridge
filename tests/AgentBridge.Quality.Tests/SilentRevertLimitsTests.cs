using AgentBridge.Quality;
using Xunit;

// #4490 P2-3 (review of #4479). The guard runs on the plan job's critical path of every pull request, so it is bounded: it checks only what the history it was given can answer (a line last changed
// before a shallow boundary is "not checked", never blamed on the boundary commit), and it stops at a hard time box and line budget and says what it did not check.
public sealed class SilentRevertLimitsTests : GitRepoTestBase
{
    // A shallow clone: the only commit it has is the boundary, so every line "last changed" there is only known to be older. Blaming it on the boundary commit would name the wrong pull request.
    [Fact]
    public void Lines_last_changed_before_the_fetched_history_are_noted_not_blamed_on_the_boundary_commit()
    {
        Commit("Add the lite delivery (#1)", ("src/Lite.cs", Lines("old", 4)));
        Commit("Touch something else (#2)", ("src/Other.cs", "x\n"));
        var shallow = Path.Combine(Path.GetTempPath(), $"revert-shallow-{Guid.NewGuid():N}");
        try
        {
            ChangedFiles.Git(Path.GetTempPath(), "clone", "-q", "--depth", "1", "--no-local", new Uri(Repo).AbsoluteUri, shallow);
            ChangedFiles.Git(shallow, "switch", "-q", "-c", "feature");
            File.WriteAllText(Path.Combine(shallow, "src", "Lite.cs"), Lines("old", 1));
            ChangedFiles.Git(shallow, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-am", "Remove lines");

            var report = SilentRevert.Check(shallow, "origin/main", "HEAD", ownPr: null);

            Assert.Empty(report.Removals);                                                      // not "#2": the boundary commit is not where those lines were last changed
            Assert.Contains(report.Notes, n => n.Contains("3 deleted lines", StringComparison.Ordinal) && n.Contains("before the fetched history", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(shallow, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(shallow, true);
        }
    }

    [Fact]
    public void The_line_budget_and_the_time_box_stop_the_check_and_say_how_much_was_not_checked()
    {
        Commit("Add three files (#7)", ("a.cs", Lines("a", 10)), ("b.cs", Lines("b", 10)), ("c.cs", Lines("c", 10)));
        Git("switch", "-q", "-c", "feature");
        Commit("Delete them", ("a.cs", ""), ("b.cs", ""), ("c.cs", ""));

        var output = new StringWriter();
        var exit = Program.Run(["silent-revert", "--repo", Repo, "--base", "main", "--max-lines", "15", "--max-seconds", "90"], output);       // the command takes both limits
        var spent = SilentRevert.Check(Repo, "main", "HEAD", ownPr: null, new SilentRevert.Limits(MaxSeconds: 0, MaxLines: 20000));

        Assert.Equal(0, exit);
        Assert.Contains("this PR removes lines from #7: 15 lines", output.ToString(), StringComparison.Ordinal);   // checked up to the budget, no further
        Assert.Contains("15 of 30 deleted lines; the rest are not checked", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("line budget", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(spent.Removals);                                                           // the time box was already spent before the first file
        Assert.Contains(spent.Notes, n => n.Contains("time box", StringComparison.Ordinal) && n.Contains("0 of 30", StringComparison.Ordinal));
    }
}
