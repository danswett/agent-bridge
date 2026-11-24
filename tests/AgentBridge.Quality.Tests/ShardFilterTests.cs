using AgentBridge.Quality;
using Xunit;

// S4 (#4104), piece 2: quality/test-shards.json and what a shard job runs. Shards 1..N-1 list the classes they run; shard N is the REMAINDER and runs everything
// not listed, by a negated filter, so a new or renamed class lands there and is never dropped, with no discovery step (the source scan dropped 73 of Host.Tests'
// 4860 tests, and --list-tests takes 222 s). What must hold: the lists never overlap, the remainder filter is built from ALL of them, and every test runs once.
public sealed class ShardFilterTests
{
    const string Plan = """
        {
          "shards": 4,
          "projects": {
            "Host.Tests": {
              "1": ["Ns.AlphaTests", "Ns.BetaTests"],
              "2": ["GammaTests"],
              "3": ["Ns.DeltaTests", "Ns.EpsilonTests"]
            }
          },
          "whole": { "1": ["Roster.Tests"], "2": ["Quality.Tests"], "3": [] }
        }
        """;

    // VSTest's rule for the filters used here: "FullyQualifiedName~X" is containment, terms joined by | are any-of, terms joined by & are all-of.
    static bool Runs(string filter, string test) => filter.StartsWith("FullyQualifiedName!~", StringComparison.Ordinal)
        ? filter.Split('&').All(t => !test.Contains(t["FullyQualifiedName!~".Length..], StringComparison.Ordinal))
        : filter.Split('|').Any(t => test.Contains(t["FullyQualifiedName~".Length..], StringComparison.Ordinal));

    static string[] Terms(string filter) => [.. filter.Split('&', '|').Select(t => t[(t.IndexOf('~') + 1)..])];

    [Fact]
    public void The_lists_never_overlap_a_class_in_two_shards_is_refused_by_name()
    {
        var e = Assert.Throws<ArgumentException>(() => ShardPlanFile.Parse(Plan.Replace("\"GammaTests\"", "\"Ns.AlphaTests\"", StringComparison.Ordinal)));

        Assert.Contains("Ns.AlphaTests", e.Message, StringComparison.Ordinal);
    }

    // "GammaTests." is inside "Ns.FakeGammaTests.": listed in different shards, the tests of one would run in both.
    [Fact]
    public void Lists_whose_terms_match_each_others_tests_are_refused()
    {
        var e = Assert.Throws<ArgumentException>(() => ShardPlanFile.Parse(Plan.Replace("\"Ns.BetaTests\"", "\"Ns.FakeGammaTests\"", StringComparison.Ordinal)));

        Assert.Contains("Ns.FakeGammaTests", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_remainder_filter_is_built_from_every_listed_class_exactly_once()
    {
        var plan = ShardPlanFile.Parse(Plan);

        var remainder = plan.For("Host.Tests", 4);

        Assert.Equal(ShardRun.Filtered, remainder.Run);
        Assert.Equal(new[] { "Ns.AlphaTests.", "Ns.BetaTests.", "Ns.DeltaTests.", "Ns.EpsilonTests.", "GammaTests." }.Order(StringComparer.Ordinal), Terms(remainder.Filter!).Order(StringComparer.Ordinal));
        Assert.All(remainder.Filter!.Split('&'), t => Assert.StartsWith("FullyQualifiedName!~", t, StringComparison.Ordinal));
    }

    [Fact]
    public void A_shard_before_the_last_runs_only_its_own_classes()
    {
        var plan = ShardPlanFile.Parse(Plan);

        Assert.Equal("FullyQualifiedName~Ns.AlphaTests.|FullyQualifiedName~Ns.BetaTests.", plan.For("Host.Tests", 1).Filter);
        Assert.Equal("FullyQualifiedName~GammaTests.", plan.For("Host.Tests", 2).Filter);
    }

    // The point of the remainder: tests of a class nobody listed (new, renamed, or from a base class) run in exactly one shard: the last.
    [Fact]
    public void Every_test_runs_in_exactly_one_shard_listed_new_renamed_and_look_alike_classes_alike()
    {
        var plan = ShardPlanFile.Parse(Plan);
        string[] tests =
        [
            "Ns.AlphaTests.Does_a_thing", "Ns.BetaTests.Does_other(x: 1)", "GammaTests.A_test", "Ns.DeltaTests.T", "Ns.EpsilonTests.T",
            "Ns.BrandNewTests.T",                // a new class: nobody listed it
            "Ns.RenamedAlphaClassTests.T",       // a rename of a listed class
            "Ns.HostEnrollmentTests.Custody(failure: \"io\")",  // the class the source scan dropped
            "Other.Ns.GammaTests.T",             // namespaced twin of a listed global class: its term matches, so it runs with GammaTests
        ];

        foreach (var test in tests)
        {
            var running = Enumerable.Range(1, 4).Where(i => plan.For("Host.Tests", i) is { Run: ShardRun.Filtered } c && Runs(c.Filter!, test)).ToArray();
            Assert.True(running.Length == 1, $"{test} runs in shards [{string.Join(", ", running)}]");
        }
        Assert.Equal(new[] { 4 }, Enumerable.Range(1, 4).Where(i => Runs(plan.For("Host.Tests", i).Filter!, "Ns.BrandNewTests.T")));
    }

    [Fact]
    public void A_filter_over_the_size_budget_is_refused_when_the_plan_is_read_not_when_the_job_runs()
    {
        var many = string.Join(',', Enumerable.Range(0, 700).Select(i => $"\"Some.Long.Namespace.Of.Tests.ClassNumber{i:D4}Tests\""));
        var json = $$"""{ "shards": 2, "projects": { "Big.Tests": { "1": [{{many}}] } } }""";

        var e = Assert.Throws<ArgumentException>(() => ShardPlanFile.Parse(json));

        Assert.Contains("24000", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_realistic_plan_stays_well_inside_the_budget()
    {
        var names = Enumerable.Range(0, 345).Select(i => $"AgentBridge.Host.Tests.SomeLongerClassName{i:D3}Tests").ToArray();
        // Three quarters of the classes listed, the rest (a new or unlisted quarter) left to the remainder, which excludes every listed one.
        string List(int skip, int take) => string.Join(',', names.Skip(skip).Take(take).Select(n => $"\"{n}\""));
        var json = $$"""{ "shards": 4, "projects": { "Host.Tests": { "1": [{{List(0, 87)}}], "2": [{{List(87, 87)}}], "3": [{{List(174, 86)}}] } } }""";

        var plan = ShardPlanFile.Parse(json);

        Assert.InRange(plan.For("Host.Tests", 4).Filter!.Length, 1, 24000);
    }

    [Fact]
    public void Whole_projects_run_in_their_shard_and_a_project_in_no_list_runs_whole_in_the_remainder()
    {
        var plan = ShardPlanFile.Parse(Plan);

        Assert.Equal(ShardRun.All, plan.For("Roster.Tests", 1).Run);
        Assert.Equal(ShardRun.None, plan.For("Roster.Tests", 2).Run);
        Assert.Equal(ShardRun.None, plan.For("Roster.Tests", 4).Run);
        Assert.Equal(new[] { ShardRun.None, ShardRun.None, ShardRun.None, ShardRun.All }, Enumerable.Range(1, 4).Select(i => plan.For("Brand.New.Tests", i).Run));
    }

    [Fact]
    public void A_shard_with_no_classes_listed_for_a_project_runs_nothing_of_it_and_a_project_with_none_listed_runs_whole_in_the_remainder()
    {
        var plan = ShardPlanFile.Parse("""{ "shards": 3, "projects": { "Host.Tests": { "1": ["AlphaTests"] } } }""");

        Assert.Equal(ShardRun.None, plan.For("Host.Tests", 2).Run);
        Assert.Equal(ShardRun.Filtered, plan.For("Host.Tests", 3).Run);
        Assert.Equal(ShardRun.All, ShardPlanFile.Parse("""{ "shards": 3, "projects": { "Host.Tests": {} } }""").For("Host.Tests", 3).Run);
    }

    [Theory]
    [InlineData("""{ "shards": 0 }""")]
    [InlineData("""{ "shards": 4, "extra": 1 }""")]
    [InlineData("""{ "shards": 4, "shards": 4 }""")]
    [InlineData("""{ "shards": 4, "projects": { "P": { "4": ["A"] } } }""")]               // shard N is the remainder: nothing is listed for it
    [InlineData("""{ "shards": 4, "projects": { "P": { "0": ["A"] } } }""")]
    [InlineData("""{ "shards": 4, "projects": { "P": { "1": ["Bad(Name)"] } } }""")]
    [InlineData("""{ "shards": 4, "projects": { "P": { "1": ["A"] } }, "whole": { "1": ["P"] } }""")]   // a project is whole or by class, not both
    [InlineData("""{ "shards": 4, "whole": { "1": ["P"], "2": ["P"] } }""")]
    [InlineData("not json")]
    public void A_plan_that_is_malformed_or_ambiguous_is_refused(string json)
    {
        Assert.Throws<ArgumentException>(() => ShardPlanFile.Parse(json));
    }

    [Fact]
    public void The_shard_filter_command_prints_all_none_or_the_filter()
    {
        var file = Path.Combine(Path.GetTempPath(), $"shards-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, Plan);
        try
        {
            string Ask(string project, string shard)
            {
                var output = new StringWriter();
                Assert.Equal(0, Program.Run(["shard-filter", "--plan", file, "--project", project, "--shard", shard], output));
                return output.ToString().Trim();
            }

            Assert.Equal("FullyQualifiedName~GammaTests.", Ask("Host.Tests", "2"));
            Assert.StartsWith("FullyQualifiedName!~", Ask("Host.Tests", "4"), StringComparison.Ordinal);
            Assert.Equal("all", Ask("Roster.Tests", "1"));
            Assert.Equal("none", Ask("Roster.Tests", "3"));
            Assert.Throws<ArgumentException>(() => Ask("Host.Tests", "5"));
            Assert.Throws<ArgumentException>(() => Ask("Host.Tests", "0"));
        }
        finally { File.Delete(file); }
    }
}
