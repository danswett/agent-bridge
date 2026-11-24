using System.Text.Json;
using AgentBridge.Quality;
using Xunit;

// S4 (#4104), piece 3: the plan is generated OFFLINE from CI's TRX files (the coverage-<os> artifact): per-class seconds -> the planner -> quality/test-shards.json.
// The generated plan lists shards 1..N-1; bin N is left UNLISTED, because it is the remainder (so it has tests today, and takes whatever is new tomorrow).
public sealed class ShardWeightsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"weights-{Guid.NewGuid():N}");

    public ShardWeightsTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    // One TRX per project, as dotnet test writes them: UnitTest definitions (className, codeBase) and results (testId, duration).
    void Trx(string project, params (string Class, string Test, double Seconds)[] tests)
    {
        var defs = string.Join("", tests.Select((t, i) => $"<UnitTest name=\"{t.Test}\" id=\"{i}\"><TestMethod codeBase=\"D:/a/{project}.dll\" className=\"{t.Class}\" name=\"{t.Test}\" /></UnitTest>"));
        var results = string.Join("", tests.Select((t, i) => $"<UnitTestResult testId=\"{i}\" testName=\"{t.Test}\" duration=\"{TimeSpan.FromSeconds(t.Seconds):c}\" outcome=\"Passed\" />"));
        File.WriteAllText(Path.Combine(dir, $"{project}-{Guid.NewGuid():N}.trx"),
            $"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>{results}</Results><TestDefinitions>{defs}</TestDefinitions></TestRun>");
    }

    static (string, string, double)[] Many(string project, int classes, double each) =>
        [.. Enumerable.Range(0, classes).SelectMany(c => Enumerable.Range(0, 3).Select(t => ($"{project}.Class{c:D3}Tests", $"T{t}", each * (c % 5 + 1) / 3)))];

    [Fact]
    public void A_classs_seconds_are_the_sum_of_its_tests_per_project()
    {
        Trx("A.Tests", ("A.OneTests", "x", 1.5), ("A.OneTests", "y", 2.0), ("A.TwoTests", "z", 4.0));

        var weights = ShardWeights.Read(dir);

        Assert.Equal(3.5, weights["A.Tests"]["A.OneTests"], 3);
        Assert.Equal(4.0, weights["A.Tests"]["A.TwoTests"], 3);
    }

    [Fact]
    public void Two_trx_files_of_one_project_add_up_and_a_directory_with_none_is_an_error()
    {
        Trx("A.Tests", ("A.OneTests", "x", 1.0));
        Trx("A.Tests", ("A.OneTests", "y", 2.0));

        Assert.Equal(3.0, ShardWeights.Read(dir)["A.Tests"]["A.OneTests"], 3);
        Assert.Throws<ArgumentException>(() => ShardWeights.Read(Path.Combine(dir, "nothing-here")));
    }

    [Fact]
    public void A_big_project_is_listed_by_class_with_the_last_bin_left_as_the_remainder()
    {
        Trx("Big.Tests", Many("Big", 60, 30));

        var text = ShardWeights.Generate(ShardWeights.Read(dir), shards: 4, wholeUnderSeconds: 60);
        var plan = ShardPlanFile.Parse(text);

        var listed = new[] { 1, 2, 3 }.Select(i => plan.For("Big.Tests", i)).ToArray();
        Assert.All(listed, c => Assert.Equal(ShardRun.Filtered, c.Run));
        Assert.StartsWith("FullyQualifiedName!~", plan.For("Big.Tests", 4).Filter, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        Assert.False(json.RootElement.GetProperty("projects").GetProperty("Big.Tests").TryGetProperty("4", out _));
    }

    // Every class is in exactly one bin, and bin 4 (the remainder) is not listed: its classes are exactly the ones no list names.
    [Fact]
    public void Every_class_runs_in_exactly_one_shard_and_every_shard_has_tests()
    {
        var tests = Many("Big", 80, 20);
        Trx("Big.Tests", tests);
        var plan = ShardPlanFile.Parse(ShardWeights.Generate(ShardWeights.Read(dir), 4, 60));

        foreach (var (cls, _, _) in tests.DistinctBy(t => t.Item1))
        {
            var homes = Enumerable.Range(1, 4).Where(i => plan.For("Big.Tests", i) is { Run: ShardRun.Filtered } c &&
                (i < 4 ? c.Filter!.Split('|').Contains($"FullyQualifiedName~{cls}.") : !c.Filter!.Split('&').Contains($"FullyQualifiedName!~{cls}.")));
            Assert.True(homes.Count() == 1, $"{cls} runs in [{string.Join(", ", homes)}]");
        }
        Assert.All(Enumerable.Range(1, 4), i => Assert.NotEqual(ShardRun.None, plan.For("Big.Tests", i).Run));
    }

    [Fact]
    public void Shards_are_balanced_by_seconds()
    {
        var tests = Many("Big", 100, 12);
        Trx("Big.Tests", tests);
        var weights = ShardWeights.Read(dir)["Big.Tests"];
        var plan = ShardPlanFile.Parse(ShardWeights.Generate(ShardWeights.Read(dir), 4, 60));

        double Seconds(int shard)
        {
            var filter = plan.For("Big.Tests", shard).Filter!;
            return weights.Where(w => shard < 4 ? filter.Split('|').Contains($"FullyQualifiedName~{w.Key}.") : !filter.Split('&').Contains($"FullyQualifiedName!~{w.Key}.")).Sum(w => w.Value);
        }
        var loads = Enumerable.Range(1, 4).Select(Seconds).ToArray();

        Assert.Equal(weights.Values.Sum(), loads.Sum(), 3);
        Assert.True(loads.Max() - loads.Min() < loads.Average() * 0.1, string.Join(", ", loads));
    }

    // Each project alone balances across the shards, but its heaviest bin must not always land on shard 1: the shards' TOTALS across projects must even out.
    [Fact]
    public void Totals_across_projects_are_balanced_not_only_each_project()
    {
        foreach (var name in new[] { "P1", "P2", "P3", "P4", "P5" })
            Trx($"{name}.Tests", ($"{name}.HeavyTests", "a", 100.0), ($"{name}.BTests", "b", 20.0), ($"{name}.CTests", "c", 20.0), ($"{name}.DTests", "d", 20.0), ($"{name}.ETests", "e", 20.0));
        var weights = ShardWeights.Read(dir);
        var plan = ShardPlanFile.Parse(ShardWeights.Generate(weights, 4, 60));

        double Seconds(int shard) => weights.Sum(p =>
        {
            var filter = plan.For(p.Key, shard).Filter!;
            return p.Value.Where(w => shard < 4 ? filter.Split('|').Contains($"FullyQualifiedName~{w.Key}.") : !filter.Split('&').Contains($"FullyQualifiedName!~{w.Key}.")).Sum(w => w.Value);
        });
        var loads = Enumerable.Range(1, 4).Select(Seconds).ToArray();

        Assert.True(loads.Max() - loads.Min() <= 100, string.Join(", ", loads));
    }

    [Fact]
    public void Small_projects_are_listed_whole_in_the_lightest_shards_and_the_last_bin_leaves_them_unlisted()
    {
        Trx("Big.Tests", Many("Big", 60, 30));
        foreach (var name in new[] { "S1.Tests", "S2.Tests", "S3.Tests", "S4.Tests", "S5.Tests", "S6.Tests" })
            Trx(name, ($"{name}.OneTests", "x", 10.0));
        var plan = ShardPlanFile.Parse(ShardWeights.Generate(ShardWeights.Read(dir), 4, 60));

        foreach (var name in new[] { "S1.Tests", "S2.Tests", "S3.Tests", "S4.Tests", "S5.Tests", "S6.Tests" })
            Assert.Single(Enumerable.Range(1, 4), i => plan.For(name, i).Run == ShardRun.All);
    }

    [Fact]
    public void The_generated_text_is_deterministic_one_list_per_line_and_inside_the_filter_budget()
    {
        Trx("Big.Tests", Many("Big", 90, 25));
        Trx("S.Tests", ("S.OneTests", "x", 2.0));
        var weights = ShardWeights.Read(dir);

        var a = ShardWeights.Generate(weights, 4, 60);

        Assert.Equal(a, ShardWeights.Generate(ShardWeights.Read(dir), 4, 60));
        Assert.All(a.Split('\n'), l => Assert.True(l.Length < 20000));
        Assert.DoesNotContain("\r", a, StringComparison.Ordinal);
        ShardPlanFile.Parse(a);
    }

    // The real collisions of this repo: most test classes are in the GLOBAL namespace, so their full name is the bare name and "AnswerFlowTests." is inside
    // "AgentAnswerFlowTests." (a containment filter cannot anchor a term at the start). Each pair must run in exactly one shard, and together.
    [Theory]
    [InlineData("AnswerFlowTests", "AgentAnswerFlowTests")]
    [InlineData("ReplyFlowTests", "AgentReplyFlowTests")]
    [InlineData("GrowthTests", "TurnBuilderGrowthTests")]
    public void A_class_whose_name_ends_with_another_runs_once_and_in_the_same_shard(string inner, string outer)
    {
        var filler = Enumerable.Range(0, 40).Select(i => ($"Filler{i:D2}Tests", "t", 5.0 + i % 7));
        Trx("Big.Tests", [(inner, "a", 50.0), (outer, "b", 50.0), .. filler]);
        var weights = ShardWeights.Read(dir);

        var plan = ShardPlanFile.Parse(ShardWeights.Generate(weights, 4, 60));

        var a = plan.RunningShards("Big.Tests", $"{inner}.a").ToArray();
        var b = plan.RunningShards("Big.Tests", $"{outer}.b").ToArray();
        Assert.Equal(a, b);
        Assert.Single(a);
        // The new class that nobody listed: it contains the inner term, so it runs where the inner class runs, once, and is not dropped.
        Assert.Equal(a, plan.RunningShards("Big.Tests", $"X{inner}.NewTest").ToArray());
    }

    [Theory]
    [InlineData("AnswerFlowTests", "AgentAnswerFlowTests")]
    [InlineData("ReplyFlowTests", "AgentReplyFlowTests")]
    [InlineData("GrowthTests", "TurnBuilderGrowthTests")]
    public void A_hand_written_plan_that_splits_such_a_pair_is_refused(string inner, string outer)
    {
        var json = $$"""{ "shards": 3, "projects": { "P": { "1": ["{{inner}}"], "2": ["{{outer}}"] } } }""";

        Assert.Throws<ArgumentException>(() => ShardPlanFile.Parse(json));
    }

    // The committed plan (quality/test-shards.json) is the one CI will use: it must stay readable, inside the filter budget and with no class in two shards.
    [Fact]
    public void The_committed_plan_is_valid_and_gives_every_shard_something_to_run()
    {
        var root = ChangedFiles.RepoRoot(Directory.GetCurrentDirectory());
        var plan = ShardPlanFile.Parse(File.ReadAllText(Path.Combine(root, "quality", "test-shards.json")));

        Assert.Equal(4, plan.Shards);
        foreach (var shard in Enumerable.Range(1, plan.Shards))
            Assert.Contains(new[] { "AgentBridge.Host.Tests", "AgentBridge.App.Tests" }, p => plan.For(p, shard).Run == ShardRun.Filtered);
    }

    [Fact]
    public void The_shard_plan_command_writes_the_plan_file()
    {
        Trx("Big.Tests", Many("Big", 60, 30));
        var file = Path.Combine(dir, "plan.json");

        Assert.Equal(0, Program.Run(["shard-plan", "--results", dir, "--shards", "4", "--out", file], new StringWriter()));

        ShardPlanFile.Parse(File.ReadAllText(file));
    }

    // Found by the mutation order (#4324): an existing directory with no TRX, and fewer than two shards, are errors; small projects go heaviest first onto the lightest shard.
    [Fact]
    public void No_trx_in_an_existing_directory_and_fewer_than_two_shards_are_errors()
    {
        Assert.Throws<ArgumentException>(() => ShardWeights.Read(dir));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShardWeights.Generate(new() { ["A.Tests"] = new() { ["A.OneTests"] = 1.0 } }, 1, 60));
    }

    [Fact]
    public void Small_projects_go_heaviest_first_onto_the_lightest_shard()
    {
        foreach (var (name, seconds) in new[] { ("P1.Tests", 30.0), ("P2.Tests", 20.0), ("P3.Tests", 10.0), ("P4.Tests", 10.0) })
            Trx(name, ($"{name}.OneTests", "x", seconds));
        var plan = ShardPlanFile.Parse(ShardWeights.Generate(ShardWeights.Read(dir), 3, 60));

        Assert.Equal(ShardRun.All, plan.For("P1.Tests", 1).Run);
        Assert.Equal(ShardRun.All, plan.For("P2.Tests", 2).Run);
    }
}
