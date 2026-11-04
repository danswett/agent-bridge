using System.Text.Json;
using System.Xml.Linq;

namespace AgentBridge.Quality.Tests;

public sealed class FlakesTests : IDisposable
{
    static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    static readonly FlakeContext Context = new("https://example.test/run/1", "abc1234", new DateTimeOffset(2026, 11, 4, 10, 30, 0, TimeSpan.Zero), "danswett");

    readonly string root = Directory.CreateTempSubdirectory("flakes-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    // One project's TRX in one run, as dotnet test writes it into the run's folder (named for the
    // time, not the project), and the run's exit code: each test name with Passed, Failed or
    // NotExecuted. Returns the TRX's path.
    string Run(string os, string project, int run, int exit, params (string Test, string Outcome)[] results)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, os, $"run-{run}")).FullName;
        var trx = Path.Combine(dir, $"runneradmin_runnervm_{Guid.NewGuid():N}_net10.0.trx");
        var ids = results.Select(_ => Guid.NewGuid().ToString()).ToList();
        new XDocument(new XElement(Ns + "TestRun",
            new XElement(Ns + "TestDefinitions", ids.Select(id => new XElement(Ns + "UnitTest", new XAttribute("id", id), new XAttribute("storage", $"/x/bin/{project.ToLowerInvariant()}.dll"),
                new XElement(Ns + "TestMethod", new XAttribute("codeBase", $"/x/bin/{project}.dll"))))),
            new XElement(Ns + "Results", results.Select((r, i) =>
                new XElement(Ns + "UnitTestResult", new XAttribute("testId", ids[i]), new XAttribute("testName", r.Test), new XAttribute("outcome", r.Outcome),
                    r.Outcome == "Failed"
                        ? new XElement(Ns + "Output", new XElement(Ns + "ErrorInfo", new XElement(Ns + "Message", $"run {run} failed ```here```"), new XElement(Ns + "StackTrace", "at T.M()")))
                        : null)))))
            .Save(trx);
        File.WriteAllText(Path.Combine(dir, "exit"), exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return trx;
    }

    [Fact]
    public void A_test_that_passed_some_runs_and_failed_others_is_flaky_one_that_failed_every_run_is_failing_and_skips_count_for_neither()
    {
        for (var i = 1; i <= 3; i++)
            Run("windows-latest", "AgentBridge.Host.Tests", i, 1,
                ("AnswerFlowTests.Racy", i == 2 ? "Failed" : "Passed"),
                ("AnswerFlowTests.Broken", "Failed"),
                ("AnswerFlowTests.Skipped", "NotExecuted"),
                ("AnswerFlowTests.Fine", "Passed"));
        Run("ubuntu-latest", "AgentBridge.Host.Tests", 1, 0, ("AnswerFlowTests.Racy", "Passed"), ("AnswerFlowTests.Broken", "Passed"));

        var verdicts = FlakeHunt.Load(root).Classify().ToDictionary(v => v.Test, v => v);

        Assert.Equal(new[] { Verdict.Flaky }, verdicts["AnswerFlowTests.Racy"].Kinds);
        var windows = verdicts["AnswerFlowTests.Racy"].ByOs.Single(o => o.Os == "windows-latest");
        Assert.Equal((1, 3), (windows.Failed, windows.Runs));
        Assert.Equal(2, verdicts["AnswerFlowTests.Racy"].FirstError(Verdict.Flaky)!.Run);
        // Failing every run on Windows and passing on Linux is still a real failure on Windows.
        Assert.Equal(new[] { Verdict.Failing }, verdicts["AnswerFlowTests.Broken"].Kinds);
        Assert.True(verdicts["AnswerFlowTests.Fine"].Passing);
        Assert.DoesNotContain("AnswerFlowTests.Skipped", verdicts.Keys);
    }

    [Fact]
    public void A_flake_already_open_by_marker_or_filed_by_hand_gets_the_new_counts_and_a_new_one_gets_an_issue_labelled_flaky()
    {
        for (var i = 1; i <= 2; i++)
            Run("windows-11-arm", "AgentBridge.App.Tests", i, i,
                ("ConnectionTests.With_no_host_running_the_app_says_so", i == 1 ? "Passed" : "Failed"),
                ("Ns.TrustTests.Known(x: 1)", i == 1 ? "Failed" : "Passed"),
                // Another case of a test whose first case has an issue of its own.
                ("Ns.TrustTests.Known(x: 2)", i == 1 ? "Passed" : "Failed"),
                ("FleetTests.New", i == 1 ? "Failed" : "Passed"));
        var verdicts = FlakeHunt.Load(root).Classify();
        var known = verdicts.Single(v => v.Test == "Ns.TrustTests.Known(x: 1)");
        var open = new List<OpenIssue>
        {
            new(465, "Flaky: App ConnectionTests.With_no_host_running_the_app_says_so times out", "", [], "danswett"),
            // Names the test but isn't about a flake: never commented on as one.
            new(300, "Connection rework", "ConnectionTests.With_no_host_running_the_app_says_so", [], "danswett"),
            new(600, "Flaky on windows-11-arm: App TrustTests.Known", FlakeReport.Marker(known, Verdict.Flaky), ["flaky", "area:testing"], "app/github-actions"),
        };

        var plan = FlakeReport.Plan(verdicts, open, Context);

        var byHand = plan.Single(a => a.Issue == 465);
        Assert.Equal("comment", byHand.Action);
        Assert.Equal(new[] { "flaky", "area:testing" }, byHand.Labels);
        Assert.Contains("| windows-11-arm | 1 | 2 |", byHand.Body);
        Assert.Contains("2026-11-04 02:30 PST", byHand.Body);
        var marked = plan.Single(a => a.Issue == 600);
        Assert.Empty(marked.Labels);
        var created = plan.Single(a => a.Action == "create" && a.Key.Contains("FleetTests.New", StringComparison.Ordinal));
        Assert.Equal("Flaky on windows-11-arm: App.Tests FleetTests.New", created.Title);
        // The hunt's own issue is for its own case only, so the second case gets one of its own.
        Assert.Contains(plan, a => a.Action == "create" && a.Key.Contains("Known(x: 2)", StringComparison.Ordinal));
        Assert.Equal(new[] { "flaky", "area:testing" }, created.Labels);
        Assert.StartsWith(FlakeReport.Marker(verdicts.Single(v => v.Test == "FleetTests.New"), Verdict.Flaky), created.Body);
        // The error's own backticks can't close the fence around it.
        Assert.Contains("````\nrun 1 failed ```here```", created.Body);
    }

    [Fact]
    public void A_hand_filed_issue_is_taken_only_for_the_whole_Class_Method_it_names()
    {
        Run("ubuntu-latest", "AgentBridge.App.Tests", 1, 1, ("AgentBridge.App.Tests.TrustTests.Known(x: 1)", "Failed"));
        Run("ubuntu-latest", "AgentBridge.App.Tests", 2, 0, ("AgentBridge.App.Tests.TrustTests.Known(x: 1)", "Passed"));
        var verdicts = FlakeHunt.Load(root).Classify();
        var open = new List<OpenIssue>
        {
            new(801, "Flaky: TrustTests.Known_more", "", [], "danswett"),
            new(802, "Flaky: OtherTrustTests.Known", "", [], "danswett"),
        };

        Assert.Equal("create", Assert.Single(FlakeReport.Plan(verdicts, open, Context)).Action);
        Assert.Equal(803, Assert.Single(FlakeReport.Plan(verdicts, [.. open, new(803, "Flaky: AgentBridge.App.Tests.TrustTests.Known(x: 1)", "", [], "danswett")], Context)).Issue);
    }

    [Fact]
    public void An_issue_by_anyone_but_the_owner_or_the_hunt_never_takes_a_tests_flake_record_even_with_its_name_or_marker()
    {
        Run("ubuntu-latest", "AgentBridge.Host.Tests", 1, 1, ("AnswerFlowTests.Racy", "Failed"));
        Run("ubuntu-latest", "AgentBridge.Host.Tests", 2, 0, ("AnswerFlowTests.Racy", "Passed"));
        var verdicts = FlakeHunt.Load(root).Classify();
        var marker = FlakeReport.Marker(verdicts.Single(), Verdict.Flaky);
        var open = new List<OpenIssue>
        {
            new(700, "Flaky: AnswerFlowTests.Racy", "", ["flaky"], "someone-else"),
            new(701, "Flaky on ubuntu-latest: Host.Tests AnswerFlowTests.Racy", marker, ["flaky"], "someone-else"),
        };

        var plan = FlakeReport.Plan(verdicts, open, Context);

        Assert.Equal("create", Assert.Single(plan).Action);
        // The owner's own, filed by hand, is taken; the hunt's, by gh's name for the Actions bot.
        Assert.Equal(702, Assert.Single(FlakeReport.Plan(verdicts, [.. open, new(702, "Flaky: AnswerFlowTests.Racy", "", [], "DanSwett")], Context)).Issue);
        Assert.Equal(703, Assert.Single(FlakeReport.Plan(verdicts, [.. open, new(703, "x", marker, [], "app/github-actions")], Context)).Issue);
        Assert.Equal("someone-else", OpenIssue.Parse("""[{"number":1,"title":"t","body":"b","labels":[],"author":{"is_bot":false,"login":"someone-else"}}]""").Single().Author);
    }

    [Fact]
    public void A_test_flaky_on_one_OS_and_failing_every_run_on_another_is_reported_as_both_and_fails_the_report()
    {
        for (var i = 1; i <= 2; i++)
        {
            Run("ubuntu-latest", "AgentBridge.Host.Tests", i, i == 1 ? 1 : 0, ("T.Both", i == 1 ? "Failed" : "Passed"));
            Run("windows-11-arm", "AgentBridge.Host.Tests", i, 1, ("T.Both", "Failed"));
        }
        var verdicts = FlakeHunt.Load(root).Classify();

        var plan = FlakeReport.Plan(verdicts, [], Context);

        Assert.Equal(new[] { "Flaky on ubuntu-latest: Host.Tests T.Both", "Failing every run on windows-11-arm: Host.Tests T.Both" }, plan.Select(a => a.Title));
        Assert.Equal(new[] { "bug", "area:testing" }, plan[1].Labels);
        Assert.Contains("**First error** (windows-11-arm, run 1)", plan[1].Body);
        Assert.Equal(1, Program.Run(["flakes", "--results", root], new StringWriter()));
    }

    [Fact]
    public void A_night_with_more_new_failures_than_the_cap_files_only_the_cap_and_says_so()
    {
        Run("ubuntu-latest", "AgentBridge.Host.Tests", 1, 1, [.. Enumerable.Range(0, FlakeReport.MaxNewIssues + 3).Select(i => ($"T.Fails{i:00}", "Failed"))]);
        var hunt = FlakeHunt.Load(root);
        var verdicts = hunt.Classify();

        var plan = FlakeReport.Plan(verdicts, [], Context);

        Assert.Equal(FlakeReport.MaxNewIssues, plan.Count);
        Assert.All(plan, a => Assert.Equal(new[] { "bug", "area:testing" }, a.Labels));
        Assert.Contains("3 more not filed", FlakeReport.Summary(hunt, verdicts, plan, dryRun: false));
    }

    [Fact]
    public void A_damaged_TRX_an_aborted_test_run_or_a_run_cut_short_is_broken_and_the_rest_is_still_classified()
    {
        for (var i = 1; i <= 2; i++)
            Run("windows-latest", "AgentBridge.Host.Tests", i, 1, ("T.Racy", i == 1 ? "Passed" : "Failed"));
        // Run 2's App.Tests host aborted after one pass and one fail.
        Abort(Run("windows-latest", "AgentBridge.App.Tests", 2, 1, ("A.Pass", "Passed"), ("A.Fail", "Failed")));
        // And Hooks.Tests' host before its first test, so its TRX names no test, nor its project.
        var hooks = Run("windows-latest", "AgentBridge.Hooks.Tests", 2, 1);
        Abort(hooks);
        File.WriteAllText(Path.Combine(root, "windows-latest", "run-1", "broken.trx"), "<TestRun><Results>");
        Run("windows-latest", "AgentBridge.Host.Tests", 3, 0, ("T.Racy", "Passed"));
        File.Delete(Path.Combine(root, "windows-latest", "run-3", "exit"));

        var hunt = FlakeHunt.Load(root);

        Assert.Equal(new[] { Verdict.Flaky }, hunt.Classify().Single(v => v.Test == "T.Racy").Kinds);
        Assert.Contains(hunt.Broken, b => b.Run == 1 && b.Why.StartsWith("broken.trx could not be read", StringComparison.Ordinal));
        Assert.Contains(hunt.Broken, b => b.Run == 2 && b.Project == "AgentBridge.App.Tests" && b.Why.StartsWith("the test run was aborted", StringComparison.Ordinal));
        Assert.Contains(hunt.Broken, b => b.Run == 3 && b.Why == "no exit code recorded: the run was cut short");
        Assert.Contains(hunt.Broken, b => b.Run == 2 && b.Project == FlakeHunt.WholeRun && b.Why.StartsWith($"{Path.GetFileName(hooks)}, before any test: the test run was aborted", StringComparison.Ordinal));
    }

    static void Abort(string trx)
    {
        var doc = XDocument.Load(trx);
        doc.Root!.Add(new XElement(Ns + "ResultSummary", new XAttribute("outcome", "Failed"),
            new XElement(Ns + "RunInfos", new XElement(Ns + "RunInfo", new XAttribute("outcome", "Error"), new XElement(Ns + "Text", "The active test run was aborted. Reason: Test host process crashed")))));
        doc.Save(trx);
    }

    [Fact]
    public void A_run_that_crashed_with_no_test_failed_or_left_no_results_fails_the_report_and_is_named_in_it()
    {
        Run("windows-latest", "AgentBridge.Host.Delivery.Tests", 1, 0, ("T.A", "Passed"));
        Run("windows-latest", "AgentBridge.Host.Tests", 1, 0, ("T.B", "Passed"));
        // Host.Delivery's test host died in run 2: nothing from it, and nothing failed.
        Run("windows-latest", "AgentBridge.Host.Tests", 2, 1, ("T.B", "Passed"));
        Directory.CreateDirectory(Path.Combine(root, "windows-latest", "run-3"));
        File.WriteAllText(Path.Combine(root, "windows-latest", "run-3", "exit"), "1");
        var plan = Path.Combine(root, "plan.json");
        var output = new StringWriter();

        var code = Program.Run(["flakes", "--results", root, "--plan", plan, "--dry-run"], output);

        Assert.Equal(1, code);
        Assert.Contains("windows-latest (the run) run 2: dotnet test exit 1 with no test failed", output.ToString());
        Assert.Contains("windows-latest AgentBridge.Host.Delivery.Tests run 2: no results from this project", output.ToString());
        Assert.Contains("windows-latest (the run) run 3: no results at all (dotnet test exit 1)", output.ToString());
        Assert.Contains("dry run: nothing filed", output.ToString());
        Assert.Equal(0, JsonDocument.Parse(File.ReadAllText(plan)).RootElement.GetArrayLength());
    }

    [Fact]
    public void A_clean_night_passes_one_where_every_run_left_nothing_fails_and_an_empty_one_is_an_error()
    {
        Run("ubuntu-latest", "AgentBridge.Protocol.Tests", 1, 0, ("T.A", "Passed"));

        Assert.Equal(0, Program.Run(["flakes", "--results", root], new StringWriter()));
        var crashed = Directory.CreateDirectory(Path.Combine(root, "crashed", "windows-latest", "run-1")).FullName;
        File.WriteAllText(Path.Combine(crashed, "exit"), "1");
        var output = new StringWriter();
        Assert.Equal(1, Program.Run(["flakes", "--results", Path.Combine(root, "crashed")], output));
        Assert.Contains("run 1: no results at all (dotnet test exit 1)", output.ToString());
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        Assert.Throws<InvalidOperationException>(() => Program.Run(["flakes", "--results", empty], new StringWriter()));
    }
}
