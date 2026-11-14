using System.Diagnostics;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class EfficiencyBaselineTests
{
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
