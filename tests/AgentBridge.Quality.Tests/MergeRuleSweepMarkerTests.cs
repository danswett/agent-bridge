using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentBridge.Quality.Tests;

// The sweep marker (a status on main's head: what the last full judgement saw), what reads it (a scoped run while main isn't
// green; the backstop's --if-changed), and a main run still in flight.
public partial class MergeRuleTests
{
    static string FailingMainRuns => MainRuns((1, "completed", "failure", 1));
    static string FailingMainRunsAgain => MainRuns((2, "completed", "failure", 1), (1, "completed", "failure", 1));
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

    static FakeGitHub FakeOpen(OpenPulls open, string? mainRuns = null, string? marker = null, bool failMarker = false) =>
        new(open.One, listed: open.Listed, byNumber: open.ByNumber, mainRuns: mainRuns, pulls: open.Pulls, marker: marker, failMarker: failMarker);

    static string JudgeWith(FakeGitHub github, params string[] arguments)
    {
        var config = WriteConfig();
        try { var output = new StringWriter(); MergeRuleCommand.Run([.. arguments, "--config", config, "--post"], output, github, OnMain); return output.ToString(); }
        finally { File.Delete(config); }
    }

    static int Points(string output) => int.Parse(Regex.Match(output, @"API: GraphQL (\d+) point").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    static bool Widened(string output) => output.Contains("judging every open pull request, not only the one asked for", StringComparison.Ordinal);

    // The judge reads main from the newest FINISHED push run (CompletedMain): a queued or in-progress run is passed over.
    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    [InlineData("waiting")]
    [InlineData("pending")]
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

        var marker = Assert.Single(full.Markers);
        Assert.Matches(@"^main=[0-9a-f]{16} heads=[0-9a-f]{16}$", marker);
        Assert.Contains(full.Posted, p => p.Contains(" merge-rule-sweep main=", StringComparison.Ordinal));
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
        Assert.Contains("every open pull request was judged for this main state", second, StringComparison.Ordinal);
        Assert.True(Points(first) > Points(second));
        Assert.All(later, run => { Assert.False(Widened(run)); Assert.Equal(Points(second), Points(run)); });
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(3, 12)]
    public void A_scoped_run_under_a_red_main_with_the_marker_costs_the_same_however_many_pull_requests_are_open(int few, int many)
    {
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
    public void A_new_main_state_widens_once_more_and_the_marker_follows_it()
    {
        var open = Open(4);
        var before = FakeOpen(open, FailingMainRuns);
        JudgeWith(before, "--pr", "42");
        var after = FakeOpen(open, FailingMainRunsAgain, marker: Assert.Single(before.Markers));

        Assert.True(Widened(JudgeWith(after, "--pr", "42")));
        Assert.False(Widened(JudgeWith(after, "--pr", "42")));
        Assert.Equal(2, after.Markers.Count);
        Assert.NotEqual(before.Markers[0], after.Markers[0]);
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("garbled")]
    [InlineData("wrong shape")]
    public void An_unreadable_marker_makes_a_scoped_run_under_a_red_main_widen_as_before(string kind)
    {
        var open = Open(3);
        var github = kind switch
        {
            "unreadable" => FakeOpen(open, FailingMainRuns, failMarker: true),
            "garbled" => FakeOpen(open, FailingMainRuns, marker: "not a marker"),
            _ => FakeOpen(open, FailingMainRuns, marker: "main=XYZ heads=123"),
        };

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
        Assert.Equal(0, Points(again));
        Assert.Equal(posted, github.Posted.Count);
    }

    [Theory]
    [InlineData("an open pull request moved to a new head")]
    [InlineData("main's state changed")]
    [InlineData("the marker is unreadable")]
    [InlineData("the marker is garbled")]
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
            "the open list is unreadable" => new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, marker: marker),
            "main is unreadable" => new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, pulls: open.Pulls, marker: marker, failMain: true),
            _ => FakeOpen(open, marker: marker),
        };

        var output = JudgeWith(github, reason == "--if-changed is not given" ? ["--all"] : ["--all", "--if-changed"]);

        Assert.DoesNotContain("Nothing has changed", output, StringComparison.Ordinal);
        Assert.True(Points(output) > 0 || reason == "main is unreadable");
    }

    [Fact]
    public void A_receipt_status_on_an_open_head_counts_as_a_change_but_the_merge_rule_verdict_itself_does_not()
    {
        var open = Open(2);
        FakeGitHub With(string statuses, string? marker = null) => new(open.One, listed: open.Listed, byNumber: open.ByNumber, pulls: open.Pulls, marker: marker, statuses: statuses);
        var settled = With("""[{"context":"merge-rule","state":"failure","description":"old"}]""");
        JudgeWith(settled, "--all", "--if-changed");
        var marker = Assert.Single(settled.Markers);

        var ourOwnVerdictMoved = With("""[{"context":"merge-rule","state":"success","description":"new"},{"context":"merge-rule-sweep","state":"success","description":"x"}]""", marker);
        var receiptArrived = With("""[{"context":"independent-review","state":"success","description":"CLEAN"}]""", marker);

        Assert.Contains("Nothing has changed", JudgeWith(ourOwnVerdictMoved, "--all", "--if-changed"), StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing has changed", JudgeWith(receiptArrived, "--all", "--if-changed"), StringComparison.Ordinal);
    }
}
