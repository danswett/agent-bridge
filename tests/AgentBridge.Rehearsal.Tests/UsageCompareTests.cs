using AgentBridge.Rehearsal;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class UsageCompareTests
{
    static readonly DateTimeOffset Start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(120);

    // Pair i of an allowance: read two minutes apart, app read 5 s after the governor, same figure.
    static UsagePair Pair(string agent, string key, int i, double gov = 40, double app = 40, int appDelaySeconds = 5, int measuredGapSeconds = 0, bool excluded = false)
    {
        var at = Start.AddMinutes(2 * i);
        return new(agent, key, at, gov, at.AddSeconds(-30), at.AddSeconds(appDelaySeconds), app, at.AddSeconds(-30 + measuredGapSeconds), excluded);
    }

    static List<UsagePair> Ten(string agent, string key, Func<int, UsagePair>? make = null) =>
        Enumerable.Range(0, 10).Select(i => make?.Invoke(i) ?? Pair(agent, key, i)).ToList();

    static List<UsagePair> Both(Func<int, UsagePair>? copilot = null, Func<int, UsagePair>? codex = null) =>
        [.. Ten("copilot", "plan", copilot), .. Ten("codex", "secondary", codex)];

    [Fact]
    public void Ten_matching_pairs_for_Copilot_plan_and_Codex_weekly_pass()
    {
        var outcome = UsageCompare.Evaluate(Both(), Interval);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Fact]
    public void One_pair_more_than_one_point_apart_fails_that_allowance()
    {
        var outcome = UsageCompare.Evaluate(Both(copilot: i => Pair("copilot", "plan", i, gov: 40, app: i == 3 ? 41.5 : 40)), Interval);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("copilot", outcome.Detail);
    }

    [Fact]
    public void Exactly_one_point_apart_still_passes()
    {
        var outcome = UsageCompare.Evaluate(Both(codex: i => Pair("codex", "secondary", i, gov: 10, app: 11)), Interval);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Fact]
    public void Fewer_than_ten_counted_pairs_fail_even_when_all_match()
    {
        var pairs = Both().Where(p => !(p.Agent == "codex" && p.GovernorReadAt == Start.AddMinutes(18))).ToList();
        var outcome = UsageCompare.Evaluate(pairs, Interval);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("codex", outcome.Detail);
    }

    [Fact]
    public void Excluded_pairs_are_not_counted_toward_the_ten()
    {
        // Ten pairs, the last excluded because the vendor figure changed between the two reads.
        var outcome = UsageCompare.Evaluate(Both(copilot: i => Pair("copilot", "plan", i, excluded: i == 9)), Interval);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        // An eleventh, retried pair makes the ten.
        var retried = Both(copilot: i => Pair("copilot", "plan", i, excluded: i == 9));
        retried.Add(Pair("copilot", "plan", 10));
        Assert.Equal(Verdict.Pass, UsageCompare.Evaluate(retried, Interval).Verdict);
    }

    [Fact]
    public void An_excluded_pair_with_a_large_difference_does_not_fail_the_check()
    {
        var pairs = Both(copilot: i => Pair("copilot", "plan", i, app: i == 9 ? 60 : 40, excluded: i == 9));
        pairs.Add(Pair("copilot", "plan", 10));
        Assert.Equal(Verdict.Pass, UsageCompare.Evaluate(pairs, Interval).Verdict);
    }

    [Fact]
    public void Readings_less_than_a_minute_apart_fail()
    {
        var close = Both(copilot: i => Pair("copilot", "plan", i) with
        {
            GovernorReadAt = Start.AddSeconds(30 * i),
            AppReadAt = Start.AddSeconds(30 * i + 5),
            GovernorMeasuredAt = Start.AddSeconds(30 * i),
            AppMeasuredAt = Start.AddSeconds(30 * i),
        });
        var outcome = UsageCompare.Evaluate(close, Interval);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("minute", outcome.Detail);
    }

    [Fact]
    public void An_app_read_more_than_sixty_seconds_after_the_governor_read_fails()
    {
        var outcome = UsageCompare.Evaluate(Both(codex: i => Pair("codex", "secondary", i, appDelaySeconds: i == 4 ? 61 : 5)), Interval);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("60", outcome.Detail);
    }

    [Fact]
    public void Measured_times_further_apart_than_one_read_interval_fail()
    {
        var outcome = UsageCompare.Evaluate(Both(copilot: i => Pair("copilot", "plan", i, measuredGapSeconds: i == 0 ? 121 : 0)), Interval);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
    }

    [Fact]
    public void A_missing_allowance_fails_rather_than_passing_on_the_other()
    {
        var outcome = UsageCompare.Evaluate(Ten("copilot", "plan"), Interval);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("codex", outcome.Detail);
    }

    [Fact]
    public void The_command_reads_a_pairs_file_with_one_JSON_object_per_line()
    {
        var path = Path.Combine(Path.GetTempPath(), $"usage-pairs-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path, Both().Select(p => System.Text.Json.JsonSerializer.Serialize(p,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
            Assert.Equal(Verdict.Pass, UsageCompare.Run(path, Interval).Verdict);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_pairs_file_that_cannot_be_read_is_an_error_not_a_pass()
    {
        var path = Path.Combine(Path.GetTempPath(), $"usage-pairs-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, "not json\n");
            Assert.Equal(Verdict.Error, UsageCompare.Run(path, Interval).Verdict);
            Assert.Equal(Verdict.Error, UsageCompare.Run(path + ".missing", Interval).Verdict);
        }
        finally { File.Delete(path); }
    }
}
