using System.Xml.Linq;

namespace AgentBridge.Quality.Tests;

public class DiffCoverageTests
{
    // A path only: nothing here is read from or written to it.
    static readonly string Root = Path.Combine(AppContext.BaseDirectory, "repo");

    static string Diff(params string[] lines) => string.Join('\n', lines) + "\n";

    [Fact]
    public void Added_lines_come_from_every_hunk_at_the_new_path_never_from_deletions_or_lines_that_look_like_headers()
    {
        var added = UnifiedDiff.AddedLines(Diff(
            "diff --git a/src/Edited.cs b/src/Edited.cs",
            "index 1111111..2222222 100644",
            "--- a/src/Edited.cs",
            "+++ b/src/Edited.cs",
            "@@ -3 +3 @@ class A",
            "-old",
            "+new",
            "@@ -10,0 +11,3 @@",
            "+a",
            "+++ an added line, not a header",
            "+-- nor this",
            "@@ -20,2 +23,0 @@",
            "-gone",
            "-gone too",
            "diff --git a/src/Old.cs b/src/Moved.cs",
            "similarity index 90%",
            "rename from src/Old.cs",
            "rename to src/Moved.cs",
            "--- a/src/Old.cs",
            "+++ b/src/Moved.cs",
            "@@ -5 +5,2 @@",
            "-x",
            "+y",
            "+z",
            "\\ No newline at end of file",
            "diff --git a/src/Pure.cs b/src/PureMoved.cs",
            "similarity index 100%",
            "rename from src/Pure.cs",
            "rename to src/PureMoved.cs",
            "diff --git a/docs/gone.md b/docs/gone.md",
            "deleted file mode 100644",
            "--- a/docs/gone.md",
            "+++ /dev/null",
            "@@ -1,2 +0,0 @@",
            "-a",
            "-b",
            "diff --git a/new file.cs b/new file.cs",
            "new file mode 100644",
            "--- /dev/null",
            "+++ b/new file.cs\t",
            "@@ -0,0 +1,2 @@",
            "+one",
            "+two",
            "diff --git \"a/tab\\there.cs\" \"b/tab\\there.cs\"",
            "--- \"a/tab\\there.cs\"",
            "+++ \"b/tab\\there.cs\"",
            "@@ -1 +1 @@",
            "-a",
            "+b",
            "diff --git \"a/caf\\303\\251 \\\"q\\\".cs\" \"b/caf\\303\\251 \\\"q\\\".cs\"",
            "--- \"a/caf\\303\\251 \\\"q\\\".cs\"",
            "+++ \"b/caf\\303\\251 \\\"q\\\".cs\"",
            "@@ -2,0 +3 @@",
            "+c",
            "diff --git a/img.png b/img.png",
            "Binary files a/img.png and b/img.png differ"));

        Assert.Equal(["café \"q\".cs", "new file.cs", "src/Edited.cs", "src/Moved.cs", "tab\there.cs"], added.Keys.ToArray());
        Assert.Equal([3, 11, 12, 13], added["src/Edited.cs"].ToArray());
        Assert.Equal([5, 6], added["src/Moved.cs"].ToArray());
        Assert.Equal([1, 2], added["new file.cs"].ToArray());
        Assert.Equal([1], added["tab\there.cs"].ToArray());
        Assert.Equal([3], added["café \"q\".cs"].ToArray());
    }

    [Fact]
    public void A_diff_that_breaks_its_own_hunk_counts_is_an_error_not_a_guess()
    {
        Assert.Throws<FormatException>(() => UnifiedDiff.AddedLines(Diff("+++ b/a.cs", "@@ -1 +1,3 @@", "-a", "+b")));
        Assert.Throws<FormatException>(() => UnifiedDiff.AddedLines(Diff("+++ b/a.cs", "@@ -1 +1,2 @@", "-a", "+b", "stray")));
        Assert.Throws<FormatException>(() => UnifiedDiff.AddedLines(Diff("@@ -0,0 +1 @@", "+a")));
        Assert.Throws<FormatException>(() => UnifiedDiff.AddedLines(Diff("+++ b/a.cs", "@@ -1 +99999999999 @@", "-a", "+b")));
    }

    static XElement Class(string filename, params (int Number, int Hits)[] lines) =>
        new("class", new XAttribute("name", "C"), new XAttribute("filename", filename),
            // A method's lines repeat its class's; this one is in no class's own lines, so it must not count.
            new XElement("methods", new XElement("method", new XElement("lines", new XElement("line", new XAttribute("number", 999), new XAttribute("hits", 1))))),
            new XElement("lines", lines.Select(l => new XElement("line", new XAttribute("number", l.Number), new XAttribute("hits", l.Hits)))));

    // Each class in a package of its own, as a file linked into two assemblies is.
    static XDocument Report(IEnumerable<string> sources, params XElement[] classes) =>
        new(new XElement("coverage",
            new XElement("sources", sources.Select(s => new XElement("source", s))),
            new XElement("packages", classes.Select(c => new XElement("package", new XElement("classes", c))))));

    static LineCoverage Coverage()
    {
        var coverage = new LineCoverage();
        coverage.Add(Root, Report([],
            Class(Path.Combine(Root, "src", "A.cs"), (1, 0), (2, 0), (3, 5)),
            Class(Path.Combine(Root, "src", "A.cs"), (2, 3), (4, 0)),
            Class(Path.Combine(Path.GetDirectoryName(Root)!, "elsewhere", "B.cs"), (1, 1)),
            Class(Path.Combine(Root, "..", "escaped.cs"), (1, 1))), "first.xml");
        coverage.Add(Root, Report([Path.Combine(Root, "src")],
            Class(Path.Combine(Root, "src", "A.cs"), (4, 1)),
            Class("Rel.cs", (7, 0))), "second.xml");
        return coverage;
    }

    [Fact]
    public void A_line_is_executable_if_any_entry_lists_it_and_covered_if_any_ran_it_and_files_outside_the_checkout_are_left_out()
    {
        var coverage = Coverage();

        Assert.Equal(2, coverage.FileCount);
        Assert.Equal(2, coverage.Outside);
        Assert.False(coverage.Covered("src/A.cs", 1));
        Assert.True(coverage.Covered("src/A.cs", 2));
        Assert.True(coverage.Covered("src/A.cs", 3));
        Assert.True(coverage.Covered("src/A.cs", 4));
        Assert.Null(coverage.Covered("src/A.cs", 5));
        Assert.Null(coverage.Covered("src/A.cs", 999));
        Assert.False(coverage.Covered("src/Rel.cs", 7));
        Assert.False(coverage.Measures("elsewhere/B.cs"));
        // Another system's absolute path never lands inside this checkout.
        Assert.Null(LineCoverage.RepoRelative(Root, @"Z:\elsewhere\src\A.cs", []));
        Assert.Null(LineCoverage.RepoRelative(Root, "../escaped.cs", []));
        Assert.Equal("src/A.cs", LineCoverage.RepoRelative(Root, "src/A.cs", []));
    }

    [Fact]
    public void A_report_that_is_not_well_formed_Cobertura_is_an_error()
    {
        Assert.Throws<FormatException>(() => new LineCoverage().Add(Root, new XDocument(new XElement("report")), "r.xml"));
        Assert.Throws<FormatException>(() => new LineCoverage().Add(Root, Report([], new XElement("class", new XElement("lines"))), "r.xml"));
        var badHits = Class(Path.Combine(Root, "a.cs"), (1, 0));
        badHits.Element("lines")!.Element("line")!.SetAttributeValue("hits", "many");
        Assert.Throws<FormatException>(() => new LineCoverage().Add(Root, Report([], badHits), "r.xml"));
        Assert.Throws<FormatException>(() => new LineCoverage().Add(Root, Report([], Class(Path.Combine(Root, "a.cs"), (0, 1))), "r.xml"));

        var folder = Fixture.ScratchFolder();
        try
        {
            var truncated = Path.Combine(folder, "Cobertura.xml");
            File.WriteAllText(truncated, "<?xml version=\"1.0\"?><coverage><packages><package>");
            Assert.Throws<FormatException>(() => new LineCoverage().Read(Root, truncated));
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    static readonly DiffCoverageIdentity Identity = new("owner/repo", "0123456789abcdef0123456789abcdef01234567", "HEAD^1",
        "fedcba9876543210fedcba9876543210fedcba98", "fedcba9876543210fedcba9876543210fedcba98",
        "coverage-ubuntu-latest", "https://github.com/owner/repo/actions/runs/1/attempts/2", "success", "every test", ["Cobertura.xml"]);

    static SortedDictionary<string, SortedSet<int>> Added(params (string Path, int[] Lines)[] files) =>
        new(files.ToDictionary(f => f.Path, f => new SortedSet<int>(f.Lines)), StringComparer.Ordinal);

    [Fact]
    public void Only_executable_added_lines_count_and_a_low_number_warns_bound_to_the_commit_and_run_it_was_measured_on()
    {
        var result = DiffCoverage.Evaluate(Added(("src/A.cs", [1, 2, 3, 5]), ("build/x.ps1", [1, 2])), Coverage());

        Assert.Equal(DiffCoverageStatus.Measured, result.Status);
        Assert.Equal((3, 2), (result.Executable, result.Covered));
        Assert.True(result.BelowTarget);
        var markdown = DiffCoverage.Render(result, Identity);
        Assert.Contains("**66.7%** of the 3 executable added line(s) ran (2 covered), below the 80.0% target.", markdown);
        Assert.Contains("| `src/A.cs` | 2 | 3 | 1 |", markdown);
        Assert.Contains("Not counted: 2 added line(s) in 1 file(s) that aren't C# outside tests/ (scripts, docs, tests) and that the report doesn't measure: `build/x.ps1`.", markdown);
        Assert.Contains("`0123456789abcdef0123456789abcdef01234567`", markdown);
        Assert.Contains("`coverage-ubuntu-latest` of [this run](https://github.com/owner/repo/actions/runs/1/attempts/2)", markdown);
        Assert.StartsWith("::warning title=Diff coverage (advisory)::66.7%25 of 3", DiffCoverage.Annotation(result));
        Assert.Equal("1-3, 7, 9-10", DiffCoverage.Ranges([1, 2, 3, 7, 9, 10]));
    }

    [Fact]
    public void No_executable_added_line_is_not_applicable_rather_than_full_coverage()
    {
        // Comments in a measured file, a doc and a test's own source: none of it is code to cover.
        var result = DiffCoverage.Evaluate(Added(("src/A.cs", [5]), ("docs/x.md", [1, 2]), ("tests/X.Tests/XTests.cs", [1])), Coverage());

        Assert.Equal(DiffCoverageStatus.NotApplicable, result.Status);
        Assert.Null(DiffCoverage.Annotation(result));
        var markdown = DiffCoverage.Render(result, Identity);
        Assert.Contains("**NOT APPLICABLE.**", markdown);
        Assert.DoesNotContain("%", markdown);
    }

    [Fact]
    public void Production_CSharp_the_report_does_not_measure_is_unknown_not_free_of_executable_lines()
    {
        var result = DiffCoverage.Evaluate(Added(("src/App/NewPage.cs", [1, 2, 3]), ("docs/x.md", [1])), Coverage());

        Assert.Equal(DiffCoverageStatus.Unavailable, result.Status);
        Assert.Equal(["src/App/NewPage.cs"], result.Unknown.ToArray());
        var markdown = DiffCoverage.Render(result, Identity);
        Assert.Contains("**UNAVAILABLE.**", markdown);
        Assert.Contains("Not measured: `src/App/NewPage.cs`.", markdown);
        Assert.DoesNotContain("NOT APPLICABLE", markdown);
        Assert.DoesNotContain("%", markdown);
        Assert.StartsWith("::warning title=Diff coverage (advisory)::UNAVAILABLE: ", DiffCoverage.Annotation(result));
        Assert.Contains("Not measured: src/App/NewPage.cs.", DiffCoverage.Annotation(result));
    }

    [Fact]
    public void Full_coverage_of_the_measured_files_is_only_a_subset_when_other_production_CSharp_is_unmeasured()
    {
        var result = DiffCoverage.Evaluate(Added(("src/A.cs", [2, 3]), ("tools/New/Tool.cs", [4])), Coverage());

        Assert.Equal(DiffCoverageStatus.Unavailable, result.Status);
        Assert.Equal((2, 2), (result.Executable, result.Covered));
        Assert.False(result.BelowTarget);
        var markdown = DiffCoverage.Render(result, Identity);
        Assert.Contains("In the files it does measure only, a subset and not this change's coverage, 100.0% of the 2 executable added line(s) ran (2 covered).", markdown);
        Assert.Contains("| `src/A.cs` | 2 | 2 |", markdown);
        Assert.DoesNotContain("**100.0%**", markdown);
        Assert.DoesNotContain("target", markdown);
        Assert.StartsWith("::warning title=Diff coverage (advisory)::UNAVAILABLE: ", DiffCoverage.Annotation(result));
    }

    [Fact]
    public void A_missing_or_partial_report_or_one_measuring_nothing_here_is_unavailable_never_a_pass()
    {
        var added = Added(("src/A.cs", [1, 2]));
        var nothingHere = new LineCoverage();
        nothingHere.Add(Root, Report([], Class(Path.Combine(Path.GetDirectoryName(Root)!, "other-checkout", "src", "A.cs"), (1, 1), (2, 1))), "r.xml");

        var outside = DiffCoverage.Evaluate(added, nothingHere);
        var missing = DiffCoverage.Evaluate(Root, added, [Path.Combine(Root, "no-such", "Cobertura.xml")], "success");
        var failedTests = DiffCoverage.Evaluate(Root, added, [Path.Combine(Root, "no-such", "Cobertura.xml")], "failure");

        Assert.All(new[] { outside, missing, failedTests }, r =>
        {
            Assert.Equal(DiffCoverageStatus.Unavailable, r.Status);
            Assert.Equal((0, 0), (r.Executable, r.Covered));
            Assert.StartsWith("::notice title=Diff coverage (advisory)::UNAVAILABLE: ", DiffCoverage.Annotation(r));
            Assert.Contains("**UNAVAILABLE.**", DiffCoverage.Render(r, Identity));
        });
        Assert.Contains("no-such", missing.Reason);
        Assert.Contains("failure", failedTests.Reason);
    }
}

// Real git processes in a throwaway repository, so ring 0 leaves it to ring 1.
[Trait("Category", "Process")]
public class DiffCoverageCommandTests
{
    [Fact]
    public void The_diff_is_from_the_merge_base_with_renames_followed_and_the_checkout_must_be_the_head()
    {
        var root = Fixture.ScratchFolder();
        try
        {
            Git(root, "init", "-q", "-b", "main");
            Write(root, "src/A.cs", "a1\na2\na3\n");
            Write(root, "src/Old.cs", "o1\no2\no3\no4\no5\no6\n");
            Write(root, "src/Main.cs", "m1\nm2\n");
            Commit(root, "base");
            Git(root, "switch", "-q", "-c", "topic");
            Write(root, "src/A.cs", "a1\nnew2\na2\na3\nnew5\n");
            Git(root, "mv", "src/Old.cs", "src/Moved.cs");
            Write(root, "src/Moved.cs", "o1\no2\no3\no4 changed\no5\no6\n");
            Commit(root, "topic");
            // After the branch point, main drops a line: a two-dot diff would read it as added.
            Git(root, "switch", "-q", "main");
            Write(root, "src/Main.cs", "m1\n");
            Commit(root, "main moves on");
            Git(root, "switch", "-q", "topic");

            var report = Path.Combine(root, "TestResults", "Cobertura.xml");
            Write(root, "TestResults/Cobertura.xml", new XDocument(new XElement("coverage", new XElement("packages", new XElement("package", new XElement("classes",
                Class(Path.Combine(root, "src", "A.cs"), (1, 1), (2, 1), (3, 0), (4, 1), (5, 0)),
                Class(Path.Combine(root, "src", "Moved.cs"), (4, 3)),
                Class(Path.Combine(root, "src", "Main.cs"), (2, 0))))))).ToString());
            var output = new StringWriter();

            var exit = DiffCoverageCommand.Run(["--coverage", report, "--base", "main", "--head", "HEAD", "--repo", root, "--artifact", "coverage-test"], output);

            Assert.Equal(0, exit);
            var text = output.ToString();
            Assert.Contains("**66.7%** of the 3 executable added line(s) ran (2 covered)", text);
            Assert.Contains("| `src/A.cs` | 1 | 2 | 5 |", text);
            Assert.Contains("| `src/Moved.cs` | 1 | 1 |", text);
            Assert.DoesNotContain("src/Main.cs", text);
            Assert.DoesNotContain("src/Old.cs", text);
            Assert.Throws<InvalidOperationException>(() => DiffCoverageCommand.Run(["--coverage", report, "--base", "main", "--head", "main", "--repo", root], new StringWriter()));
        }
        finally
        {
            Fixture.Delete(root);
        }
    }

    static XElement Class(string filename, params (int Number, int Hits)[] lines) =>
        new("class", new XAttribute("filename", filename),
            new XElement("lines", lines.Select(l => new XElement("line", new XAttribute("number", l.Number), new XAttribute("hits", l.Hits)))));

    static void Write(string root, string path, string text)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    static void Commit(string root, string message)
    {
        Git(root, "add", "src");
        Git(root, "-c", "user.name=quality", "-c", "user.email=quality@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
    }

    static string Git(string root, params string[] args) => ChangedFiles.Git(root, args);
}
