using System.Diagnostics;
using AgentBridge.Quality;
using Xunit;

// S4 (#4104): the verify-only CI gate. A new class that is a look-alike of a listed one (AnswerFlowTests inside AgentAnswerFlowTests) must FAIL the pull request that adds it, not
// be found later. The unit is the TEST METHOD, as VSTest names it ("Ns.Outer+Inner.Method"), read from the built assembly's metadata only (no type loading, no test code run).
public sealed class ShardVerifyTests
{
    static readonly string Dll = typeof(ShardVerifyTests).Assembly.Location;

    static string[] Scan() => [.. TestMethodScanner.Scan(Dll)];

    [Fact]
    public void The_scan_finds_facts_theories_custom_facts_inherited_nested_and_generic_methods_by_their_VSTest_names()
    {
        var found = Scan();

        string[] expected =
        [
            "AnswerFlowTests.Plain", "AnswerFlowTests.Themed", "AgentAnswerFlowTests.Plain",
            "OuterLookTests.Outer", "OuterLookTests+InnerLookTests.Nested",
            "DerivedLookTests.Own", "DerivedLookTests.FromBase", "DerivedLookTests.Generic",   // inherited methods run under the concrete class
            "CustomFactLookTests.Custom",                                                      // a subclass of FactAttribute, by its base chain
        ];
        Assert.All(expected, name => Assert.Contains(name, found));
        Assert.DoesNotContain(found, n => n.Contains("AbstractNeverRunLookTests", StringComparison.Ordinal));   // abstract, never derived
        Assert.DoesNotContain(found, n => n.Contains("BaseLookTests", StringComparison.Ordinal));              // open generic base: it runs only through DerivedLookTests
    }

    // The proof that the matcher is VSTest's: the same terms, through a real `dotnet test --list-tests --filter`, list exactly the methods the matcher selects.
    [Theory]
    [InlineData("LookTests.")]          // nested, generic, inherited, custom Fact; the abstract class is absent
    [InlineData("AnswerFlowTests.")]    // a term inside another class's name
    [InlineData("ANSWERFLOWTESTS.")]    // case
    public void The_matcher_selects_exactly_what_a_real_dotnet_test_filter_lists(string term)
    {
        var expected = Scan().Where(n => n.Contains(term, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();

        var listed = RealList(term);

        Assert.NotEmpty(expected);
        Assert.Equal(expected, listed);
    }

    static string[] RealList(string term)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var arg in new[] { "test", Dll, "--list-tests", "--filter", $"FullyQualifiedName~{term}" }) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(180_000), "dotnet test --list-tests took over 3 minutes");
        var lines = output.Result.Split('\n').Select(l => l.Trim()).SkipWhile(l => !l.StartsWith("The following Tests are available", StringComparison.Ordinal)).Skip(1).ToArray();
        Assert.True(lines.Length > 0, $"no tests listed: {output.Result}{errors.Result}");
        // The display name carries a theory's arguments and a generic method's type arguments ("Themed(value: 1)", "Generic<Int32>"): both come after the method name, and a term ends in
        // a dot after a CLASS name, so they cannot change a match; the comparison drops them.
        return [.. lines.Where(l => l.Length > 0).Select(l => l[..(l.IndexOfAny(['(', '<']) is var cut and >= 0 ? cut : l.Length)]).Distinct().Order(StringComparer.Ordinal)];
    }

    // With the remainder a method cannot run in no shard (the term that keeps it out of the remainder puts it in its own), so the plan cannot produce this; the guard is pinned directly,
    // so a future change to the plan model cannot turn a dropped test into a pass.
    [Theory]
    [InlineData(new int[0], false)]       // runs nowhere: dropped
    [InlineData(new[] { 2 }, true)]       // exactly one
    [InlineData(new[] { 1, 3 }, false)]   // twice
    public void A_method_that_runs_in_no_shard_or_two_is_a_violation_and_exactly_one_is_not(int[] shards, bool fine)
    {
        var violation = ShardVerify.Violation("P.Tests", "Ns.Class.Method", shards);

        Assert.Equal(fine, violation is null);
        if (!fine) { Assert.Contains("Ns.Class.Method would run in shards [" + string.Join(", ", shards) + "], not exactly one", violation, StringComparison.Ordinal); }
    }

    [Fact]
    public void A_new_unlisted_look_alike_of_a_listed_class_runs_in_two_shards_and_is_named()
    {
        // AnswerTests (shard 1) and FlowTests (shard 2) are listed. A NEW class FlowTests in a namespace called AnswerTests holds both terms.
        var plan = ShardPlanFile.Parse("""{ "shards": 3, "projects": { "P": { "1": ["AnswerTests"], "2": ["FlowTests"] } } }""");

        var bad = ShardVerify.Check(plan, "P", ["AnswerTests.A", "FlowTests.A", "AnswerTests.FlowTests.A", "AnythingTests.A"]);

        var single = Assert.Single(bad);
        Assert.Contains("AnswerTests.FlowTests.A", single, StringComparison.Ordinal);
        Assert.Contains("[1, 2]", single, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_unlisted_look_alike_of_one_listed_class_runs_once_with_it_so_it_passes()
    {
        var plan = ShardPlanFile.Parse("""{ "shards": 3, "projects": { "P": { "1": ["AnswerFlowTests"] } } }""");

        Assert.Empty(ShardVerify.Check(plan, "P", ["AnswerFlowTests.A", "XAnswerFlowTests.A", "OtherTests.A"]));
    }

    [Fact]
    public void Case_alone_does_not_hide_a_collision()
    {
        var plan = ShardPlanFile.Parse("""{ "shards": 3, "projects": { "P": { "1": ["TurnTests"], "2": ["GrowthTests"] } } }""");

        // "Ns.TURNTESTS.GrowthTests.A" holds "GrowthTests." as written and "TurnTests." only when case is ignored, as VSTest ignores it.
        Assert.Single(ShardVerify.Check(plan, "P", ["Ns.TURNTESTS.GrowthTests.A"]));
    }

    // Fail closed: a gate that passes because it saw nothing is worse than none.
    [Fact]
    public void A_missing_dll_zero_tests_a_load_error_and_an_unreadable_plan_all_fail_and_say_so()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var plan = Path.Combine(dir, "plan.json");
            File.WriteAllText(plan, """{ "shards": 3, "projects": { "Missing.Tests": { "1": ["ATests"] } }, "whole": { "1": ["Whole.Tests"] } }""");
            var corrupt = Path.Combine(dir, "Corrupt.Tests.dll");
            File.WriteAllText(corrupt, "not an assembly");
            var empty = Path.Combine(dir, "Empty.Tests.dll");
            File.Copy(typeof(object).Assembly.Location, empty);       // a real assembly with no test in it

            string Verify(params string[] extra)
            {
                var output = new StringWriter();
                Assert.Equal(1, ShardVerify.Run(plan, extra, output, TimeSpan.FromMinutes(2)));
                return output.ToString();
            }

            Assert.Contains("no test assemblies", Verify(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Missing.Tests", Verify(Dll), StringComparison.Ordinal);
            Assert.Contains("Whole.Tests", Verify(Dll), StringComparison.Ordinal);       // a project the plan lists whole is named too
            Assert.Contains("Corrupt.Tests", Verify(corrupt), StringComparison.Ordinal);
            Assert.Contains("Empty.Tests", Verify(empty), StringComparison.Ordinal);
            var late = new StringWriter();
            Assert.Equal(1, ShardVerify.Run(plan, [Dll], late, TimeSpan.FromTicks(-1)));   // the time box ran out: not verified is a failure
            Assert.Contains("time box", late.ToString(), StringComparison.Ordinal);
            File.WriteAllText(plan, "not json");
            Assert.Contains("shard plan", Verify(Dll), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_command_passes_for_this_assembly_with_every_method_in_exactly_one_shard()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var plan = Path.Combine(dir, "plan.json");
            var project = Path.GetFileNameWithoutExtension(Dll);
            File.WriteAllText(plan, $$"""{ "shards": 3, "projects": { "{{project}}": { "1": ["AnswerFlowTests", "AgentAnswerFlowTests"], "2": ["OuterLookTests"] } } }""");

            var output = new StringWriter();

            Assert.Equal(0, Program.Run(["shard-verify", "--plan", plan, "--dll", Dll], output));
            Assert.Contains("ok", output.ToString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
