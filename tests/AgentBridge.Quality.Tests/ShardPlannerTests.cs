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

    [Fact]
    public void Shard_weights_differ_by_no_more_than_the_heaviest_unit()
    {
        var classes = Classes(200, seed: 11);
        var weights = classes.ToDictionary(c => c, c => (double)(Math.Abs(c.GetHashCode()) % 90 + 1));

        var plan = ShardPlanner.Plan(classes, weights, 4);

        var totals = plan.Shards.Select(s => s.Sum(c => weights[c])).ToArray();
        Assert.True(totals.Max() - totals.Min() <= weights.Values.Max() * 3, $"shards {string.Join(", ", totals)}");
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
    public void More_shards_than_classes_leaves_shards_empty_whose_filter_matches_nothing()
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
