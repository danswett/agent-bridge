using System.Diagnostics;
using System.Text.Json;

namespace AgentBridge.Quality.Tests;

// Offline propagation probe only: fixture evidence is not an observed installed-app cause.
[Trait("Category", "Process")]
public class AppSelfTestCauseTests
{
    [Fact]
    public async Task A_failed_installed_self_test_reports_the_recorded_app_exit_cause()
    {
        const string stderrCause = "self-test: failed: fixture validation step";
        const string eventCause = "AgentBridge.exe: fixture exception in fixture validation step";
        var (exit, output) = await Run(new
        {
            processId = 4242, exitCode = 1, failed = true, stderr = stderrCause,
            events = new[] { new { processId = 4242, message = eventCause } },
            captureErrors = Array.Empty<string>()
        });

        Assert.True(exit == 0, output);
        Assert.True(output.Contains(stderrCause, StringComparison.Ordinal) &&
            output.Contains(eventCause, StringComparison.Ordinal),
            $"Recorded stderr and app-process event causes must reach diagnostic output.\n" +
            $"Fixture stderr: {stderrCause}\nFixture event: {eventCause}\nObserved output:\n{output}");
    }

    [Fact]
    public async Task Capture_gaps_are_reported_without_attributing_another_process_event_to_the_app()
    {
        const string captureGap = "fixture stderr capture failed: access denied";
        const string foreignEvent = "AgentBridge.exe: another process event must not be attributed";
        var (exit, output) = await Run(new
        {
            processId = 4242, exitCode = 1, failed = true, stderr = "",
            events = new[] { new { processId = 4243, message = foreignEvent } },
            captureErrors = new[] { captureGap }
        });

        Assert.NotEqual(0, exit);
        Assert.Contains(captureGap, output);
        Assert.Contains("app stderr cause was not captured", output);
        Assert.Contains("no Windows event cause correlated to the app process was captured", output);
        Assert.DoesNotContain(foreignEvent, output);
    }

    static async Task<(int Exit, string Output)> Run(object observed)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var evidence = Path.Combine(folder, "app-exit-cause.json");
            File.WriteAllText(evidence, JsonSerializer.Serialize(observed));
            var script = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory),
                "build", "test-app-self-test-cause.ps1");
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Evidence", evidence })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }
}
