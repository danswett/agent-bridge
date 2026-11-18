using System.Diagnostics;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class EfficiencyBaselineTests
{
    [Fact]
    public Task Memory_verdict_fails_when_growth_stays_over_its_ceiling_through_the_rerun() =>
        The_baseline_contract_holds_for_each_fixture("memory-growth");

    [Fact]
    public Task Memory_verdict_passes_when_a_one_time_cache_stops_growing_in_the_rerun() =>
        The_baseline_contract_holds_for_each_fixture("memory-cache");

    [Fact]
    public Task Memory_verdict_reports_not_measured_when_the_control_moves() =>
        The_baseline_contract_holds_for_each_fixture("memory-control");

    [Theory]
    [InlineData("memory-spin")]
    [InlineData("memory-steady")]
    [InlineData("memory-boundary")]
    [InlineData("memory-missing")]
    [InlineData("memory-replacement")]
    [InlineData("memory-retry")]
    [InlineData("memory-invalid")]
    public Task Memory_verdict_honours_recorded_sample_boundaries(string scenario) =>
        The_baseline_contract_holds_for_each_fixture(scenario);

    [Theory]
    [InlineData("elapsed")]
    [InlineData("exit")]
    [InlineData("replacement")]
    [InlineData("readonly")]
    [InlineData("no-survivors")]
    [InlineData("partial")]
    [InlineData("ambiguous")]
    [InlineData("metric-values")]
    [InlineData("entrypoint")]
    public async Task The_baseline_contract_holds_for_each_fixture(string scenario)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(root, "quality", "efficiency-baseline.Tests.ps1"), "-Scenario", scenario })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, (await error) + (await stdout));
    }
}
