using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentBridge.Quality.Tests;

// The sweep marker (a status on main's head: what the last full judgement saw) and the two things that read it: a scoped run
// while main isn't green, and the backstop's --if-changed. Also: what the judge does with a main run that is still in flight.
public partial class MergeRuleTests
{
    static string FailingMainRuns => MainRuns((1, "completed", "failure", 1));

    // A red main that is a different state: another finished run.
    static string FailingMainRunsAgain => MainRuns((2, "completed", "failure", 1), (1, "completed", "failure", 1));

    static string HeadOfPull(int index) => $"{index + 1:x8}".PadRight(40, '0');

    sealed record OpenPulls(string One, string[] Listed, Dictionary<int, string> ByNumber, string Pulls);

    // `count` open pull requests (#42 and up), each on its own head, as GraphQL lists them and as REST lists them.
    static OpenPulls Open(int count, int movedHead = -1)
    {
        var listed = Enumerable.Range(0, count).Select(i => GraphQLPullRequest(HeadOfPull(i), number: 42 + i)).ToArray();
        return new(listed[0], listed, Enumerable.Range(0, count).ToDictionary(i => 42 + i, i => listed[i]),
            JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new
            {
                number = 42 + i,
                head = new { sha = i == movedHead ? new string('9', 40) : HeadOfPull(i) },
                draft = false,
                labels = Array.Empty<object>(),
                updated_at = "2026-11-23T00:00:00Z",
            })));
    }

    static FakeGitHub FakeOpen(OpenPulls open, string? mainRuns = null, string? marker = null, bool failMarker = false) =>
        new(open.One, listed: open.Listed, byNumber: open.ByNumber, mainRuns: mainRuns, pulls: open.Pulls, marker: marker, failMarker: failMarker);

    static string JudgeWith(FakeGitHub github, params string[] arguments)
    {
        var config = WriteConfig();
        try
        {
            var output = new StringWriter();
            MergeRuleCommand.Run([.. arguments, "--config", config, "--post"], output, github, OnMain);
            return output.ToString();
        }
        finally
        {
            File.Delete(config);
        }
    }

    static int Points(string output) => int.Parse(Regex.Match(output, @"API: GraphQL (\d+) point").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    static bool Widened(string output) => output.Contains("judging every open pull request, not only the one asked for", StringComparison.Ordinal);

    // The judge reads main from the newest finished push run of CI (CompletedMain): a run that is queued or in progress has no
    // verdict yet and is passed over, so the START of a main run changes nothing; its completion does.
    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    [InlineData("waiting")]
    [InlineData("pending")]
    public void A_main_run_still_in_flight_is_passed_over_for_the_newest_finished_one(string status)
    {
        var green = FakeOpen(Open(1), MainRuns((3, status, null, 1), (2, "completed", "success", 1), (1, "completed", "failure", 1)));
        var red = FakeOpen(Open(1), MainRuns((3, status, null, 1), (2, "completed", "failure", 1), (1, "completed", "success", 1)));

        var seenGreen = JudgeWith(green, "--all");
        var seenRed = JudgeWith(red, "--all");

        Assert.Contains(" run 2 (attempt 1) on ", seenGreen, StringComparison.Ordinal);
        Assert.DoesNotContain(" run 3 (", seenGreen, StringComparison.Ordinal);
        Assert.Contains("named jobs passed", seenGreen, StringComparison.Ordinal);
        Assert.Contains(" run 2 (attempt 1) on ", seenRed, StringComparison.Ordinal);
        Assert.DoesNotContain(" run 3 (", seenRed, StringComparison.Ordinal);
        Assert.Contains("named jobs not green", seenRed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_judgement_posts_the_sweep_marker_and_a_dry_run_or_a_cut_open_list_does_not()
    {
        var open = Open(3);
        var full = FakeOpen(open, FailingMainRuns);
        var cut = new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, mainRuns: FailingMainRuns, pulls: open.Pulls, listFailsAfterFirstPage: true);
        var dry = FakeOpen(open, FailingMainRuns);

        JudgeWith(full, "--all");
        JudgeWith(cut, "--all");
        var config = WriteConfig();
        try { MergeRuleCommand.Run(["--all", "--config", config], new StringWriter(), dry, OnMain); }
        finally { File.Delete(config); }

        var marker = Assert.Single(full.Markers);
        Assert.Matches(@"^main=[0-9a-f]{16} heads=[0-9a-f]{16}$", marker);
        Assert.Contains(full.Posted, p => p.Contains(" merge-rule-sweep main=", StringComparison.Ordinal));
        Assert.Empty(cut.Markers);
        Assert.Empty(dry.Markers);
    }

    // Quality's condition: a red main costs ONE full judgement per main state change, not one per event.
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
        Assert.True(Points(first) > Points(second), $"the first, widened run cost {Points(first)} points, the next {Points(second)}");
        Assert.All(later, run => { Assert.False(Widened(run)); Assert.Equal(Points(second), Points(run)); });
        Assert.Single(github.Markers);
    }

    // And what a scoped run costs under a red main with the marker no longer grows with the open pull requests.
    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 12)]
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

        var firstOfTheNewState = JudgeWith(after, "--pr", "42");
        var nextOfTheNewState = JudgeWith(after, "--pr", "42");

        Assert.True(Widened(firstOfTheNewState));
        Assert.False(Widened(nextOfTheNewState));
        Assert.Equal(2, after.Markers.Count);
        Assert.NotEqual(before.Markers[0], after.Markers[0]);
    }

    // The marker is never trusted when it can't be read: an unreadable one means judge in full, never skip or narrow.
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

        var output = JudgeWith(github, "--pr", "42");

        Assert.True(Widened(output));
    }

    [Fact]
    public void A_scoped_run_under_a_green_main_never_widens_and_never_posts_a_marker()
    {
        var github = FakeOpen(Open(4));

        var output = JudgeWith(github, "--pr", "42");

        Assert.False(Widened(output));
        Assert.Empty(github.Markers);
    }

    // The backstop: --all --if-changed judges nothing while main's state and the open pull requests are as the last full
    // judgement left them, and everything else judges as it always did.
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
        Assert.Single(github.Markers);
    }

    [Theory]
    [InlineData("an open pull request moved to a new head")]
    [InlineData("main's state changed")]
    [InlineData("the marker is unreadable")]
    [InlineData("the marker is garbled")]
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
            _ => FakeOpen(open, marker: marker),
        };

        string[] arguments = reason == "--if-changed is not given" ? ["--all"] : ["--all", "--if-changed"];
        var output = JudgeWith(github, arguments);

        Assert.DoesNotContain("Nothing has changed", output, StringComparison.Ordinal);
        Assert.True(Points(output) > 0);
    }

    [Fact]
    public void The_backstop_judges_when_the_open_pull_requests_cannot_be_listed_cheaply_or_main_cannot_be_read()
    {
        var open = Open(3);
        var settled = FakeOpen(open);
        JudgeWith(settled, "--all", "--if-changed");
        var marker = Assert.Single(settled.Markers);
        var noList = new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, marker: marker);
        var noMain = new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, pulls: open.Pulls, marker: marker, failMain: true);

        var withoutList = JudgeWith(noList, "--all", "--if-changed");
        var withoutMain = JudgeWith(noMain, "--all", "--if-changed");

        Assert.DoesNotContain("Nothing has changed", withoutList, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing has changed", withoutMain, StringComparison.Ordinal);
        Assert.Contains("Couldn't read main's CI", string.Join("\n", noMain.Posted), StringComparison.Ordinal);
    }

    [Fact]
    public void A_receipt_status_on_an_open_head_counts_as_a_change_but_the_merge_rule_verdict_itself_does_not()
    {
        var open = Open(2);
        const string receipt = """[{"context":"independent-review","state":"success","description":"CLEAN"},{"context":"merge-rule","state":"failure","description":"old"}]""";
        var withoutReceipt = new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, pulls: open.Pulls, statuses: """[{"context":"merge-rule","state":"failure","description":"old"}]""");
        JudgeWith(withoutReceipt, "--all", "--if-changed");
        var marker = Assert.Single(withoutReceipt.Markers);

        var sameButForOurOwnVerdict = new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, pulls: open.Pulls, marker: marker,
            statuses: """[{"context":"merge-rule","state":"success","description":"new"},{"context":"merge-rule-sweep","state":"success","description":"x"}]""");
        var receiptArrived = new FakeGitHub(open.One, listed: open.Listed, byNumber: open.ByNumber, pulls: open.Pulls, marker: marker, statuses: receipt);

        Assert.Contains("Nothing has changed", JudgeWith(sameButForOurOwnVerdict, "--all", "--if-changed"), StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing has changed", JudgeWith(receiptArrived, "--all", "--if-changed"), StringComparison.Ordinal);
    }
}
