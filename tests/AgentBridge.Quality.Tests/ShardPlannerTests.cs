using AgentBridge.Quality;
using Xunit;

// S4 (#4104): the product job's test shards. A shard runs the classes it is given with a VSTest filter of "FullyQualifiedName~Class." terms. The planner's
// whole job is that no test is dropped and none runs twice: every discovered class lands in exactly one shard, and a term matches only its own shard's tests
// (a term "X." also matches "FakeX.", so classes like that travel together). The spike found 73 of Host.Tests' 4860 tests run nowhere when classes came from a
// source scan; these tests are why discovery is the built assembly's and the plan is a pure function of it.
public sealed class ShardPlannerTests
{
    static readonly IReadOnlyDictionary<string, double> NoWeights = new Dictionary<string, double>();

    static string[] Classes(int count, int seed)
    {
        var random = new Random(seed);
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var ns = random.Next(4) switch { 0 => "", 1 => "AgentBridge.Host.Tests.", 2 => "AgentBridge.App.Tests.", _ => "AgentBridge.Transport.Tests." };
            names.Add($"{ns}{(random.Next(5) == 0 ? "Fake" : "")}Class{random.Next(count * 2)}Tests");
        }
        return [.. names.Distinct()];
    }

    static string[] Flatten(ShardPlan plan) => [.. plan.Shards.SelectMany(s => s)];

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 12)]
    [InlineData(3, 139)]
    [InlineData(4, 345)]
    [InlineData(8, 40)]
    public void Every_class_lands_in_exactly_one_shard(int shards, int count)
    {
        var classes = Classes(count, seed: count * 31 + shards);

        var plan = ShardPlanner.Plan(classes, NoWeights, shards);

        Assert.Equal(shards, plan.Shards.Count);
        Assert.Equal(classes.Order(StringComparer.Ordinal), Flatten(plan).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_plan_does_not_depend_on_the_order_classes_are_discovered_in()
    {
        var classes = Classes(120, seed: 7);
        var weights = classes.ToDictionary(c => c, c => (double)(c.Length * 3 % 41 + 1));

        var plan = ShardPlanner.Plan(classes, weights, 4);
        var shuffled = ShardPlanner.Plan([.. classes.OrderBy(c => c.GetHashCode() ^ 0x5bd1e995)], weights, 4);

        Assert.Equal(plan.Shards.Select(s => string.Join('|', s)), shuffled.Shards.Select(s => string.Join('|', s)));
    }

    // "AnswerFlowTests." is also inside "AgentAnswerFlowTests.": a filter for one runs the other too, so a plan that split them would run it twice.
    [Fact]
    public void Classes_whose_terms_would_match_each_other_travel_together()
    {
        string[] classes = ["AnswerFlowTests", "AgentAnswerFlowTests", "Ns.AnswerFlowTests", "ReplyFlowTests", "Other1", "Other2", "Other3"];
        var weights = new Dictionary<string, double> { ["AnswerFlowTests"] = 100, ["AgentAnswerFlowTests"] = 100, ["Ns.AnswerFlowTests"] = 100 };

        var plan = ShardPlanner.Plan(classes, weights, 4);

        var holders = plan.Shards.Select((s, i) => (s, i)).Where(x => x.s.Contains("AnswerFlowTests") || x.s.Contains("AgentAnswerFlowTests") || x.s.Contains("Ns.AnswerFlowTests"))
            .Select(x => x.i).Distinct().ToArray();
        Assert.Single(holders);
    }

    [Fact]
    public void A_term_matches_only_the_tests_of_its_own_shards_classes_even_with_collisions()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var classes = Classes(60 + seed, seed);
            var plan = ShardPlanner.Plan(classes, NoWeights, 3 + seed % 3);
            foreach (var (shard, index) in plan.Shards.Select((s, i) => (s, i)))
                foreach (var className in shard)
                {
                    var matching = plan.ShardsMatching($"{className}.A_test_name").ToArray();
                    Assert.Equal([index], matching);
                }
        }
    }

    // Longest-first onto the lightest shard leaves the shards within one unit of each other. Names with no look-alikes, so every class is its own unit; weights from a seeded generator
    // (string.GetHashCode is randomized per process, so a test built on it changes between runs).
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    [InlineData(3, 8)]
    public void Shard_weights_differ_by_no_more_than_the_heaviest_class(int seed, int shards)
    {
        var random = new Random(seed);
        var classes = Enumerable.Range(0, 150).Select(i => $"Ns{i % 7}.Case{i:D3}Tests").ToArray();
        var weights = classes.ToDictionary(c => c, _ => Math.Round(random.NextDouble() * 90 + 1, 2));

        var plan = ShardPlanner.Plan(classes, weights, shards);

        var totals = plan.Shards.Select(s => s.Sum(c => weights[c])).ToArray();
        Assert.True(totals.Max() - totals.Min() <= weights.Values.Max() + 1e-9, $"shards {string.Join(", ", totals)}");
    }

    [Fact]
    public void A_heavy_class_gets_a_shard_nearly_to_itself()
    {
        string[] classes = ["Host.Big", "Host.A", "Host.B", "Host.C", "Host.D", "Host.E", "Host.F", "Host.G"];
        var weights = classes.ToDictionary(c => c, c => c == "Host.Big" ? 400.0 : 20.0);

        var plan = ShardPlanner.Plan(classes, weights, 4);

        var big = plan.Shards.Single(s => s.Contains("Host.Big"));
        Assert.Single(big);
    }

    [Fact]
    public void A_class_with_no_recorded_weight_still_gets_a_shard_and_is_spread_by_the_median()
    {
        string[] classes = ["Known1", "Known2", "Known3", "Known4", "New1", "New2", "New3", "New4"];
        var weights = new Dictionary<string, double> { ["Known1"] = 10, ["Known2"] = 10, ["Known3"] = 10, ["Known4"] = 10 };

        var plan = ShardPlanner.Plan(classes, weights, 4);

        Assert.Equal(8, Flatten(plan).Length);
        Assert.All(plan.Shards, s => Assert.Equal(2, s.Count));
    }

    [Fact]
    public void More_shards_than_classes_leaves_shards_empty_with_empty_filter_text_which_the_caller_must_skip_not_pass_to_dotnet_test()
    {
        var plan = ShardPlanner.Plan(["OnlyTests"], NoWeights, 3);

        Assert.Equal(3, plan.Shards.Count);
        Assert.Equal(1, plan.Shards.Count(s => s.Count > 0));
        var empty = Enumerable.Range(0, 3).Where(i => plan.Shards[i].Count == 0).ToArray();
        Assert.Equal(2, empty.Length);
        Assert.All(empty, i => Assert.Equal("", plan.Filter(i)));
        Assert.Single(plan.ShardsMatching("OnlyTests.Method"));
    }

    [Fact]
    public void The_filter_is_one_sorted_term_per_class_and_plain_text()
    {
        var plan = ShardPlanner.Plan(["Zed.BTests", "Zed.ATests"], NoWeights, 1);

        Assert.Equal("FullyQualifiedName~Zed.ATests.|FullyQualifiedName~Zed.BTests.", plan.Filter(0));
    }

    // P1 (review of #4317): VSTest's "FullyQualifiedName~X" is a case-INSENSITIVE contains, so "Foo.ATests." is inside "Foo.aTests.". The check is made on the real filter TEXT,
    // not the planner's own matcher: each test must run under exactly one shard's filter, as VSTest reads it.
    static int[] RunsUnder(ShardPlan plan, string test) =>
        [.. Enumerable.Range(0, plan.Shards.Count).Where(i => plan.Filter(i).Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Any(t => test.Contains(t["FullyQualifiedName~".Length..], StringComparison.OrdinalIgnoreCase)))];

    [Theory]
    [InlineData("Foo.ATests", "Foo.aTests")]
    [InlineData("AnswerFlowTests", "AGENTanswerflowTests")]
    [InlineData("Ns.XTests", "ns.xtests")]
    public void Classes_that_differ_only_by_case_are_one_unit_because_VSTest_ignores_case(string first, string second)
    {
        string[] classes = [first, second, "Bar.One", "Bar.Two", "Bar.Three", "Bar.Four"];
        var weights = classes.ToDictionary(c => c, _ => 10.0);

        var plan = ShardPlanner.Plan(classes, weights, 2);

        Assert.Equal(1, plan.Shards.Count(s => s.Contains(first)));
        Assert.Equal(plan.Shards.Single(s => s.Contains(first)), plan.Shards.Single(s => s.Contains(second)));
        foreach (var c in classes) { Assert.Single(RunsUnder(plan, $"{c}.A_test")); }
        foreach (var c in classes) { Assert.Single(plan.ShardsMatching($"{c}.A_test")); }
    }

    [Fact]
    public void A_class_name_inside_another_with_different_case_travels_with_it()
    {
        var plan = ShardPlanner.Plan(["AnswerFlowTests", "AGENTanswerflowTests", "Other1", "Other2", "Other3"], NoWeights, 3);

        Assert.Equal(1, plan.Shards.Count(s => s.Contains("AnswerFlowTests") || s.Contains("AGENTanswerflowTests")));
        Assert.Single(RunsUnder(plan, "AGENTanswerflowTests.M"));
    }

    // P2: what makes every shard job compute the same plan. A fixed input with its exact plan, so a change to the tie-break or the order fails here.
    [Fact]
    public void The_plan_for_a_fixed_input_is_exactly_this()
    {
        string[] classes = ["H", "G", "F", "E", "D", "C", "B", "A"];
        var weights = new Dictionary<string, double> { ["A"] = 10, ["B"] = 9, ["C"] = 8, ["D"] = 7, ["E"] = 6, ["F"] = 5, ["G"] = 5, ["H"] = 1 };

        var plan = ShardPlanner.Plan(classes, weights, 3);

        Assert.Equal(["A|F|G", "B|E|H", "C|D"], plan.Shards.Select(s => string.Join('|', s)));
    }

    [Fact]
    public void Units_of_equal_weight_go_in_name_order_ascending_to_the_lowest_shard()
    {
        var plan = ShardPlanner.Plan(["B5", "A5"], new Dictionary<string, double> { ["A5"] = 5, ["B5"] = 5 }, 2);

        Assert.Equal(["A5", "B5"], plan.Shards.Select(s => string.Join('|', s)));
    }

    [Fact]
    public void A_class_with_no_time_weighs_the_median_of_those_that_have_one()
    {
        // Known 30, 20, 10: the median is 20, so the unknown U weighs 20 and sorts after B (equal weight, later name).
        var plan = ShardPlanner.Plan(["A", "B", "C", "U"], new Dictionary<string, double> { ["A"] = 30, ["B"] = 20, ["C"] = 10 }, 2);

        Assert.Equal(["A|C", "B|U"], plan.Shards.Select(s => string.Join('|', s)));
    }

    [Theory]
    [InlineData(-50.0)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_weight_that_is_not_a_positive_finite_number_counts_as_unknown(double bad)
    {
        string[] classes = ["A", "B", "C", "U"];
        var known = new Dictionary<string, double> { ["A"] = 30, ["B"] = 20, ["C"] = 10 };

        var withBad = ShardPlanner.Plan(classes, new Dictionary<string, double>(known) { ["U"] = bad }, 2);
        var without = ShardPlanner.Plan(classes, known, 2);

        Assert.Equal(without.Shards.Select(s => string.Join('|', s)), withBad.Shards.Select(s => string.Join('|', s)));
    }

    [Fact]
    public void The_simulation_of_a_filter_ignores_case_as_VSTest_does()
    {
        var plan = new ShardPlan([["Foo.aTests"], ["Other"]]);

        Assert.Equal([0], plan.ShardsMatching("Foo.ATests.M"));
        Assert.Equal([0], plan.ShardsMatching("FOO.ATESTS.M"));
        Assert.Empty(plan.ShardsMatching("Foo.aTestsX.M"));
    }

    [Fact]
    public void A_class_named_twice_is_planned_once()
    {
        var plan = ShardPlanner.Plan(["A", "A", "B", "A"], NoWeights, 2);

        Assert.Equal(["A", "B"], Flatten(plan).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_filter_sorts_its_terms_whatever_order_the_shard_holds_them_in()
    {
        var plan = new ShardPlan([["Zed.B", "Zed.A", "Alpha"]]);

        Assert.Equal("FullyQualifiedName~Alpha.|FullyQualifiedName~Zed.A.|FullyQualifiedName~Zed.B.", plan.Filter(0));
    }

    [Theory]
    [InlineData("Bad(Name)")]
    [InlineData("A&B")]
    [InlineData("A|B")]
    [InlineData("A=B")]
    [InlineData("A!B")]
    [InlineData("A~B")]
    [InlineData("")]
    public void A_class_name_the_filter_language_would_misread_is_refused_loudly_not_dropped(string name)
    {
        Assert.Throws<ArgumentException>(() => ShardPlanner.Plan(["Fine", name], NoWeights, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void At_least_one_shard_is_required(int shards)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShardPlanner.Plan(["A"], NoWeights, shards));
    }
}
