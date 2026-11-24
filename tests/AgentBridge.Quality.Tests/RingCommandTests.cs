namespace AgentBridge.Quality.Tests;

public class RingCommandTests
{
    const string Exclusions = "Category!=Process&Category!=Network&Category!=HomeAssistantIntegration";

    [Theory]
    [InlineData("dotnet test tests/A.Tests -c Release", "dotnet test tests/A.Tests -c Release --filter \"" + Exclusions + "\"")]
    [InlineData("dotnet test tests/HA.Tests -c Release --filter \"Category!=HomeAssistantIntegration\"",
        "dotnet test tests/HA.Tests -c Release --filter \"Category!=HomeAssistantIntegration&Category!=Process&Category!=Network\"")]
    [InlineData("dotnet test x --filter \"A|B\" --nologo", "dotnet test x --filter \"(A|B)&" + Exclusions + "\" --nologo")]
    [InlineData("cd pwa && node --test \"test/unit/*.test.mjs\"", "cd pwa && node --test \"test/unit/*.test.mjs\"")]
    public void Ring_zero_adds_the_in_process_filter_to_dotnet_test_merged_with_the_commands_own(string command, string expected) =>
        Assert.Equal(expected, RingCommand.For(command, 0));

    [Fact]
    public void Later_rings_run_the_catalogs_command_as_written()
    {
        const string command = "dotnet test tests/A.Tests -c Release";

        Assert.Equal(command, RingCommand.For(command, 1));
    }

    // quality/run.ps1 runs each suite's command through RingCommand at ring 0. A --filter that is not quoted is not seen by it, so a
    // second one is appended and dotnet test refuses two ("expects a single argument"): ha-callsite-guard failed for everyone.
    [Fact]
    public void Every_dotnet_test_command_in_the_catalog_has_one_filter_at_every_ring_and_a_Quality_Tests_filter_names_a_real_test()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) root = Path.GetDirectoryName(root)!;
        var catalog = Catalog.Load(Path.Combine(root, "quality", "catalog.yml"));
        var types = typeof(RingCommandTests).Assembly.GetTypes();
        var commands = catalog.Suites.Where(s => s.Command is { } c && c.TrimStart().StartsWith("dotnet test", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(commands);
        foreach (var suite in commands)
        {
            foreach (var ring in new[] { 0, 1 })
            {
                var run = RingCommand.For(suite.Command, ring)!;
                var filters = System.Text.RegularExpressions.Regex.Matches(run, @"--filter(\s|$)").Count;
                Assert.True(filters <= 1, $"{suite.Id} at ring {ring} runs {filters} --filter arguments: {run}");
            }
            // A filter that names no test runs nothing and still passes, so one aimed at this project must name a class it has.
            if (suite.Project == "tests/AgentBridge.Quality.Tests" && System.Text.RegularExpressions.Regex.Match(suite.Command!, @"FullyQualifiedName~(\w+)") is { Success: true } name)
                Assert.Contains(types, t => t.FullName!.Contains(name.Groups[1].Value, StringComparison.Ordinal));
        }
    }
}
