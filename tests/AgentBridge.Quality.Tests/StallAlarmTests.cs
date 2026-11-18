using System.Diagnostics;

namespace AgentBridge.Quality.Tests;

public class StallAlarmTests
{
    [Theory]
    [Trait("Category", "Process")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Stall_alarm_uses_complete_evidence_and_one_issue(int scenario)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(root, "quality", "test-stall-alarm.ps1"), "-Case", scenario.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }
}
