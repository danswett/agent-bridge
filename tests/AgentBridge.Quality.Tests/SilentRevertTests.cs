using AgentBridge.Quality;
using Xunit;

// #4465: PR #3968's merge of main silently reverted #4126 (its code and its four HoldOf tests), and CI stayed green because the tests went with the code. The guard asks, for every line a pull
// request deletes, which merged PR last changed it, and says so. It only warns: the point is that a reader looks.
public sealed class SilentRevertTests : GitRepoTestBase
{
    const string Source = "class Lite {\n  void Deliver() {\n    held = Hold(node);\n  }\n  void Other() { }\n}\n";
    const string Tests = "public class LiteTests {\n  [Fact]\n  public void HoldOf_resolves_a_node() { }\n\n  [Fact]\n  public void HoldOf_refuses_an_unknown_node() { }\n\n  [Fact]\n  public void Unrelated() { }\n}\n";

    [Fact]
    public void A_diff_gives_each_deleted_line_with_its_line_number_in_the_old_file()
    {
        const string diff = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -2,2 +1,0 @@ class A\n-two\n-three\n@@ -9 +7,2 @@\n-nine\n+nine!\n+ten\n";

        var lines = SilentRevert.DeletedLines(diff);

        Assert.Equal([("src/A.cs", 2, "two"), ("src/A.cs", 3, "three"), ("src/A.cs", 9, "nine")], lines.Select(l => (l.File, l.OldLine, l.Text)));
    }

    [Theory]
    [InlineData("Fix the hold lookup (#4126)", 4126)]
    [InlineData("Merge main into #3968 (#3968)", 3968)]
    [InlineData("A change that mentions #99 in the middle", null)]
    [InlineData("Plain commit", null)]
    public void A_squash_merge_subject_names_its_pull_request_only_by_the_trailing_number(string subject, int? pr)
    {
        Assert.Equal(pr, SilentRevert.PrOf(subject));
    }

    [Fact]
    public void Deleted_test_methods_are_named_but_deleted_helpers_and_source_methods_are_not()
    {
        var deleted = new[]
        {
            new DeletedLine("tests/X/LiteTests.cs", 2, "  [Fact]"), new DeletedLine("tests/X/LiteTests.cs", 3, "  public void HoldOf_resolves_a_node() { }"),
            new DeletedLine("tests/X/LiteTests.cs", 4, "  // a helper, not a test"), new DeletedLine("tests/X/LiteTests.cs", 5, "  public void SetUpHelper() { }"),   // looks like a test, follows a deleted line, but no [Fact] before it
            new DeletedLine("tests/X/LiteTests.cs", 6, "  [Theory]"), new DeletedLine("tests/X/LiteTests.cs", 7, "  public async Task Another_case(int n) { }"),
            new DeletedLine("src/Lite.cs", 3, "  public void Deliver() { }"),
        };

        Assert.Equal(["HoldOf_resolves_a_node", "Another_case"], SilentRevert.DeletedTests(deleted).Select(t => t.Name));
    }

    [Fact]
    public void A_deleted_line_is_blamed_on_the_pull_request_that_last_changed_it_and_the_pull_requests_own_number_is_left_out()
    {
        Commit("Add the lite delivery (#1)", ("src/Lite.cs", Source), ("tests/X/LiteTests.cs", Tests));
        Commit("Change Deliver's hold (#2)", ("src/Lite.cs", Source.Replace("Hold(node)", "HoldOf(node)", StringComparison.Ordinal)));
        Git("switch", "-q", "-c", "feature");
        Commit("Remove the hold tests and the hold call", ("src/Lite.cs", Source.Replace("    held = Hold(node);\n", "", StringComparison.Ordinal)),
            ("tests/X/LiteTests.cs", Tests.Replace("  [Fact]\n  public void HoldOf_resolves_a_node() { }\n\n", "", StringComparison.Ordinal)));

        var found = SilentRevert.Find(Repo, "main", "HEAD", ownPr: null);

        Assert.Equal([1, 2], found.Select(r => r.Pr).Order());
        var first = found.Single(r => r.Pr == 1);
        Assert.Equal(["HoldOf_resolves_a_node"], first.Tests.Select(t => t.Name));
        Assert.Equal(["src/Lite.cs"], found.Single(r => r.Pr == 2).Files);                                // the line #2 changed
        Assert.Equal([1], SilentRevert.Find(Repo, "main", "HEAD", ownPr: 2).Select(r => r.Pr));        // the PR itself is not blamed on itself
    }

    [Fact]
    public void A_pull_request_that_only_adds_lines_is_flagged_for_nothing()
    {
        Commit("Add the lite delivery (#1)", ("src/Lite.cs", Source));
        Git("switch", "-q", "-c", "feature");
        Commit("Add more", ("src/Lite.cs", Source + "// new\n"));

        Assert.Empty(SilentRevert.Find(Repo, "main", "HEAD", ownPr: null));
        Assert.Contains("No lines", SilentRevert.Render([]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_says_which_pull_request_loses_lines_names_the_deleted_tests_and_is_a_warning_not_a_failure()
    {
        Commit("Add the lite delivery (#4126)", ("src/Lite.cs", Source), ("tests/X/LiteTests.cs", Tests));
        Git("switch", "-q", "-c", "feature");
        Commit("Revert quietly", ("src/Lite.cs", Source.Replace("    held = Hold(node);\n", "", StringComparison.Ordinal)),
            ("tests/X/LiteTests.cs", Tests.Replace("  [Fact]\n  public void HoldOf_resolves_a_node() { }\n\n", "", StringComparison.Ordinal)));
        var (output, summary) = (new StringWriter(), Path.Combine(Repo, "summary.md"));

        var exit = Program.Run(["silent-revert", "--repo", Repo, "--base", "main", "--summary", summary], output);

        Assert.Equal(0, exit);                                                                             // flags, never fails on its own
        var text = output.ToString();
        Assert.Contains("this PR removes lines from #4126", text, StringComparison.Ordinal);
        Assert.Contains("HoldOf_resolves_a_node", text, StringComparison.Ordinal);
        Assert.Contains("::warning", text, StringComparison.Ordinal);
        // the ::warning lines are workflow commands for the log; in the job summary they would show as literal text
        Assert.Contains("this PR removes lines from #4126", File.ReadAllText(summary), StringComparison.Ordinal);
        Assert.DoesNotContain("::warning", File.ReadAllText(summary), StringComparison.Ordinal);
    }
}
