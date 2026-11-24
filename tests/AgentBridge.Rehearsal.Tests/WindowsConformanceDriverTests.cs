using System.Text.Json;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

internal sealed class WindowsConformanceStub(string? omittedCapability = null, string? failingCapability = null)
{
    static readonly Uri Link = new("https://example.test/runs/windows-stub");
    readonly List<string> calls = [];

    public IReadOnlyList<string> Calls => calls;

    public ConformanceResult? Run(ConformanceRun run, ConformanceCapability capability)
    {
        calls.Add(capability.Name);
        if (capability.Name == omittedCapability)
            return null;
        var failed = capability.Name == failingCapability;
        return new(run, capability.Name, failed ? ConformanceVerdict.Fail : ConformanceVerdict.Pass,
            failed ? "stub failure" : "stub passed", Link);
    }
}

[CollectionDefinition("Windows conformance entry point", DisableParallelization = true)]
public sealed class WindowsConformanceEntryPointCollection { }

[Collection("Windows conformance entry point")]
public class WindowsConformanceDriverTests
{
    static readonly ConformanceRun Windows = new("Windows", "1.0", 1, new string('b', 40), "windows-run");

    [Fact]
    public void The_driver_records_each_required_inventory_row_from_the_windows_stub()
    {
        var required = ConformanceInventory.ForClient("Windows");
        var stub = new WindowsConformanceStub();

        var results = WindowsConformanceDriver.Run(Windows, required, stub.Run);

        Assert.Equal(required.Select(capability => capability.Name), stub.Calls);
        Assert.Equal(required.Select(capability => capability.Name), results.Select(result => result.Capability));
        Assert.All(results, result =>
        {
            Assert.Equal(Windows, result.Run);
            Assert.Equal(ConformanceVerdict.Pass, result.Verdict);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_driver_refuses_callback_results_outside_the_required_run_or_inventory(bool wrongRun)
    {
        var required = ConformanceInventory.ForClient("Windows");
        ConformanceResult? Invalid(ConformanceRun run, ConformanceCapability capability)
            => new(wrongRun ? run with { RunId = "another-run" } : run,
                wrongRun ? capability.Name : "unknown-capability", ConformanceVerdict.Fail, "invalid stub result");

        Assert.Throws<ArgumentException>(() => WindowsConformanceDriver.Run(Windows, required, Invalid));
    }

    [Theory]
    [InlineData("pass", 0, (int)ConformanceVerdict.Pass)]
    [InlineData("fail", 1, (int)ConformanceVerdict.Fail)]
    [InlineData("omitted", 1, (int)ConformanceVerdict.Missing)]
    [InlineData("unlinked", 1, (int)ConformanceVerdict.Missing)]
    public async Task The_entry_point_maps_recorded_windows_evidence_onto_every_required_inventory_row(
        string scenario, int expectedExit, int expectedReply)
    {
        using var s = new Scratch();
        var required = ConformanceInventory.ForClient("Windows");
        var results = PassingRows().Where(row => scenario != "omitted" || row.Capability != "reply")
            .Select(row => row.Capability != "reply" ? row : row with
            {
                Verdict = scenario == "fail" ? ConformanceVerdict.Fail : ConformanceVerdict.Pass,
                Evidence = scenario == "unlinked" ? null : row.Evidence,
            });

        var (exit, rows, error) = await EntryPoint(s, results);

        Assert.Equal(expectedExit, exit);
        Assert.Equal("", error);
        Assert.Equal(required.Select(capability => capability.Name), rows.Select(row => row.Capability));
        Assert.All(rows, row => Assert.Equal(Windows, row.Run));
        Assert.Equal((ConformanceVerdict)expectedReply, rows.Single(row => row.Capability == "reply").Verdict);
        Assert.All(rows.Where(row => row.Capability != "reply"), row =>
        {
            Assert.Equal(ConformanceVerdict.Pass, row.Verdict);
            Assert.Equal("recorded self-test evidence", row.Reason);
            Assert.Equal(new Uri("https://example.test/runs/windows"), row.Evidence);
        });
    }

    [Fact]
    public async Task The_entry_point_cannot_certify_windows_from_a_passing_caller_selected_subset()
    {
        using var s = new Scratch();
        var (exit, rows, error) = await EntryPoint(s, PassingRows().Where(row => row.Capability == "reply"));

        Assert.Equal(1, exit);
        Assert.Equal("", error);
        Assert.Equal(ConformanceInventory.ForClient("Windows").Count, rows.Length);
        Assert.All(rows.Where(row => row.Capability != "reply"),
            row => Assert.Equal(ConformanceVerdict.Missing, row.Verdict));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_entry_point_refuses_evidence_for_another_run_or_unknown_capability(bool wrongRun)
    {
        using var s = new Scratch();
        var invalid = PassingRows().Select(row => row.Capability != "reply" ? row : row with
        {
            Run = wrongRun ? Windows with { RunId = "another-run" } : Windows,
            Capability = wrongRun ? "reply" : "unknown-capability",
        });
        var (exit, rows, error) = await EntryPoint(s, invalid);

        Assert.Equal(2, exit);
        Assert.Empty(rows);
        Assert.Contains("outside the required client/capability matrix", error, StringComparison.Ordinal);
    }

    static IEnumerable<ConformanceResult> PassingRows()
        => ConformanceInventory.ForClient("Windows").Select(capability => new ConformanceResult(
            Windows, capability.Name, ConformanceVerdict.Pass, "recorded self-test evidence",
            new Uri("https://example.test/runs/windows")));

    static async Task<(int Exit, ConformanceResult[] Rows, string Error)> EntryPoint(
        Scratch s, IEnumerable<ConformanceResult> results)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = await Program.Main(["conformance", "--client", "Windows",
                "--runs", s.File("runs.json", JsonSerializer.Serialize(new[] { Windows }, Evidence.Json)),
                "--capabilities", s.File("capabilities.json", """[{"name":"reply","live":true}]"""),
                "--results", s.File("results.jsonl", string.Join("\n", ClientConformance.JsonLines(results))),
                "--home", s.Root]);
            var rows = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<ConformanceResult>(line, Evidence.Json)!).ToArray();
            return (exit, rows, error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
