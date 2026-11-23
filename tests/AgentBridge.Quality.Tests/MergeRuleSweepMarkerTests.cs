using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentBridge.Quality.Tests;

// The sweep marker (a status on main's head), what reads it (a scoped run under a red main, --if-changed), a main run in flight.
public partial class MergeRuleTests
{
    static string FailingMainRuns => MainRuns((1, "completed", "failure", 1));
    static string HeadOfPull(int index) => $"{index + 1:x8}".PadRight(40, '0');

    sealed record OpenPulls(string One, string[] Listed, Dictionary<int, string> ByNumber, string Pulls);
    static OpenPulls Open(int count, int movedHead = -1)
    {
        var listed = Enumerable.Range(0, count).Select(i => GraphQLPullRequest(HeadOfPull(i), number: 42 + i)).ToArray();
        return new(listed[0], listed, Enumerable.Range(0, count).ToDictionary(i => 42 + i, i => listed[i]),
            JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new
            {
                number = 42 + i, head = new { sha = i == movedHead ? new string('9', 40) : HeadOfPull(i) },
                draft = false, labels = Array.Empty<object>(), updated_at = "2026-11-23T00:00:00Z",
            })));
    }
    static FakeGitHub FakeOpen(OpenPulls open, string? mainRuns = null, string? marker = null, bool failMarker = false, string statuses = "[]") =>
        new(open.One, listed: open.Listed, byNumber: open.ByNumber, mainRuns: mainRuns, pulls: open.Pulls, marker: marker, failMarker: failMarker, statuses: statuses);
    static string JudgeWith(FakeGitHub github, params string[] arguments)
    {
        var config = WriteConfig();
        try { var output = new StringWriter(); MergeRuleCommand.Run([.. arguments, "--config", config, "--post"], output, github, OnMain); return output.ToString(); }
        finally { File.Delete(config); }
    }
    static int Points(string output) => int.Parse(Regex.Match(output, @"API: GraphQL (\d+) point").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    static FakeGitHub By(FakeGitHub github, string login) { github.MarkerCreator = login; return github; }
    static FakeGitHub Aged(FakeGitHub github, TimeSpan? age) { github.MarkerAt = age is null ? null : DateTimeOffset.UtcNow - age; return github; }
    static bool Widened(string output) => output.Contains("judging every open pull request, not only the one asked for", StringComparison.Ordinal);

    // The judge reads main from the newest FINISHED push run (CompletedMain): a queued or in-progress run is passed over.
    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    public void A_main_run_still_in_flight_is_passed_over_for_the_newest_finished_one(string status)
    {
        var green = FakeOpen(Open(1), MainRuns((3, status, null, 1), (2, "completed", "success", 1), (1, "completed", "failure", 1)));
        var red = FakeOpen(Open(1), MainRuns((3, status, null, 1), (2, "completed", "failure", 1), (1, "completed", "success", 1)));
        foreach (var (github, verdict) in new[] { (green, "named jobs passed"), (red, "named jobs not green") })
        {
            var seen = JudgeWith(github, "--all");
            Assert.Contains(" run 2 (attempt 1) on ", seen, StringComparison.Ordinal);
            Assert.DoesNotContain(" run 3 (", seen, StringComparison.Ordinal);
            Assert.Contains(verdict, seen, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_full_judgement_posts_the_sweep_marker_and_a_dry_run_or_a_cut_open_list_does_not()
    {
        var open = Open(3);
        var (full, dry) = (FakeOpen(open, FailingMainRuns), FakeOpen(open, FailingMainRuns));
        var cut = new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, mainRuns: FailingMainRuns, pulls: open.Pulls, listFailsAfterFirstPage: true);
        JudgeWith(full, "--all");
        JudgeWith(cut, "--all");
        var config = WriteConfig();
        try { MergeRuleCommand.Run(["--all", "--config", config], new StringWriter(), dry, OnMain); } finally { File.Delete(config); }

        Assert.Equal("merge-rule-sweep", MergeRuleCommand.SweepContext);
        Assert.Matches(@"^main=[0-9a-f]{16} heads=[0-9a-f]{16}$", Assert.Single(full.Markers));
        Assert.Empty(cut.Markers);
        Assert.Empty(dry.Markers);
    }

    [Fact]
    public void A_red_main_costs_one_full_judgement_per_main_state_and_then_scoped_runs_stay_scoped()
    {
        var open = Open(6);
        var github = FakeOpen(open, FailingMainRuns);
        var first = JudgeWith(github, "--pr", "42");
        var second = JudgeWith(github, "--pr", "42");
        var later = Enumerable.Range(0, 8).Select(_ => JudgeWith(github, "--pr", "42")).ToArray();
        Assert.True(Widened(first));
        Assert.Single(github.Markers);
        Assert.False(Widened(second));
        Assert.True(Points(first) > Points(second));
        Assert.All(later, run => { Assert.False(Widened(run)); Assert.Equal(Points(second), Points(run)); });
        var next = FakeOpen(open, MainRuns((2, "completed", "failure", 1), (1, "completed", "failure", 1)), marker: github.Markers[0]); // a new main state widens once more
        Assert.True(Widened(JudgeWith(next, "--pr", "42")));
        Assert.False(Widened(JudgeWith(next, "--pr", "42")));
        Assert.Equal(2, next.Markers.Count);
    }

    [Fact]
    public void A_scoped_run_under_a_red_main_with_the_marker_costs_the_same_however_many_pull_requests_are_open()
    {
        const int few = 1, many = 6;
        int Scoped(int count, bool withMarker)
        {
            var github = FakeOpen(Open(count), FailingMainRuns);
            if (withMarker) { JudgeWith(github, "--all"); }
            return Points(JudgeWith(github, "--pr", "42"));
        }

        Assert.Equal(Scoped(few, withMarker: true), Scoped(many, withMarker: true));
        Assert.True(Scoped(many, withMarker: false) > Scoped(many, withMarker: true));
    }

    [Fact]
    public void An_unreadable_marker_makes_a_scoped_run_under_a_red_main_widen_as_before()
    {
        var open = Open(3);
        foreach (var github in new[] { FakeOpen(open, FailingMainRuns, failMarker: true), FakeOpen(open, FailingMainRuns, marker: "not a marker"), FakeOpen(open, FailingMainRuns, marker: "main=XYZ heads=123") })
            Assert.True(Widened(JudgeWith(github, "--pr", "42")));
    }

    [Fact]
    public void The_backstop_does_nothing_when_nothing_changed_since_the_last_full_judgement()
    {
        var open = Open(3);
        var github = FakeOpen(open);
        JudgeWith(github, "--all", "--if-changed");
        var posted = github.Posted.Count;
        var again = JudgeWith(github, "--all", "--if-changed");
        Assert.Contains("Nothing has changed since the last full judgement", again, StringComparison.Ordinal);
        Assert.Contains("Nothing has changed", JudgeWith(Aged(FakeOpen(open, marker: github.Markers[0]), TimeSpan.FromMinutes(59)), "--all", "--if-changed"), StringComparison.Ordinal);
        Assert.Equal(0, Points(again));
        Assert.Equal(posted, github.Posted.Count);
        // merge-rule's own verdict and the marker on a head are not changes either
        var ours = FakeOpen(open, marker: github.Markers[0], statuses: """[{"context":"merge-rule","state":"failure","description":"x"},{"context":"merge-rule-sweep","state":"success","description":"y"}]""");
        Assert.Contains("Nothing has changed", JudgeWith(ours, "--all", "--if-changed"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("an open pull request moved to a new head")]
    [InlineData("main's state changed")]
    [InlineData("the marker is unreadable")]
    [InlineData("the marker is garbled")]
    [InlineData("a receipt status arrived")]
    [InlineData("the marker is over an hour old")]
    [InlineData("the marker has no time")]
    [InlineData("the marker was posted by someone else")]
    [InlineData("the open list is unreadable")]
    [InlineData("main is unreadable")]
    [InlineData("--if-changed is not given")]
    public void The_backstop_judges_in_full_when_anything_changed_or_cannot_be_checked(string reason)
    {
        var open = Open(3);
        var settled = FakeOpen(open);
        JudgeWith(settled, "--all", "--if-changed");
        var marker = Assert.Single(settled.Markers);
        var github = reason switch
        {
            "an open pull request moved to a new head" => FakeOpen(Open(3, movedHead: 1), marker: marker),
            "main's state changed" => FakeOpen(open, MainRuns((2, "completed", "success", 1), (1, "completed", "success", 1)), marker: marker),
            "the marker is unreadable" => FakeOpen(open, failMarker: true),
            "the marker is garbled" => FakeOpen(open, marker: "garbled"),
            "the marker is over an hour old" => Aged(FakeOpen(open, marker: marker), TimeSpan.FromMinutes(61)),
            "the marker has no time" => Aged(FakeOpen(open, marker: marker), null),
            "the marker was posted by someone else" => By(FakeOpen(open, marker: marker), "mallory"),
            "a receipt status arrived" => FakeOpen(open, marker: marker, statuses: """[{"context":"independent-review","state":"success","description":"CLEAN"}]"""),
            "the open list is unreadable" => new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, marker: marker),
            "main is unreadable" => new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, pulls: open.Pulls, marker: marker, failMain: true),
            _ => FakeOpen(open, marker: marker),
        };

        var output = JudgeWith(github, reason == "--if-changed is not given" ? ["--all"] : ["--all", "--if-changed"]);

        Assert.DoesNotContain("Nothing has changed", output, StringComparison.Ordinal);
        Assert.True(Points(output) > 0 || reason == "main is unreadable");
    }

    // The marker is bookkeeping: whatever state it is in, on a head it is no check and no receipt.
    [Fact]
    public void The_marker_status_on_a_head_is_never_an_input_to_the_verdict()
    {
        object Marker() => new { context = "merge-rule-sweep", state = "FAILURE", description = "independent-review CLEAN", createdAt = "2026-11-03T20:02:00Z", creator = new { login = "danswett" } };
        var plain = new FakeGitHub(GraphQLPullRequest(Head));
        var marked = new FakeGitHub(GraphQLPullRequest(Head, extraStatuses: [Marker()]));

        JudgeWith(plain, "--pr", "42");
        JudgeWith(marked, "--pr", "42");

        Assert.Equal(Assert.Single(plain.Posted), Assert.Single(marked.Posted));
    }

    // A waiver flip fires no event, so it is in the digest: the backstop judges again, then is quiet again.
    [Fact]
    public void Flipping_the_codex_waiver_makes_the_backstop_judge_again()
    {
        var github = FakeOpen(Open(2));
        string Backstop(string? waiver)
        {
            var (config, output) = (WriteConfig(), new StringWriter());
            File.AppendAllText(config, Environment.NewLine + "codex_waiver: { switch: CODEX_WAIVER, description: waiver }" + Environment.NewLine);
            try { MergeRuleCommand.Run(["--all", "--if-changed", "--config", config, "--post"], output, github, OnMain, variable: name => name == "CODEX_WAIVER" ? waiver : null); return output.ToString(); }
            finally { File.Delete(config); }
        }
        Backstop(null);
        Assert.Contains("Nothing has changed", Backstop(null), StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing has changed", Backstop("on"), StringComparison.Ordinal);
        Assert.Contains("Nothing has changed", Backstop("on"), StringComparison.Ordinal);
    }

    // The marker is read and written on the checkout's own head, and only when that is origin/main's untouched commit.
    [Fact]
    public void A_run_that_is_not_origin_mains_evaluator_neither_reads_nor_writes_the_marker()
    {
        var github = FakeOpen(Open(2));
        var config = WriteConfig();
        try { MergeRuleCommand.Run(["--all", "--allow-non-main", "--config", config, "--post"], new StringWriter(), github, _ => new(Head, MainCommit, Dirty: false)); }
        finally { File.Delete(config); }

        Assert.Empty(github.Markers);
        Assert.DoesNotContain(github.RestPaths, path => path.Contains($"/commits/{Head}/statuses", StringComparison.Ordinal));
    }
}
