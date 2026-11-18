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
        var folder = Fixture.ScratchFolder();
        try
        {
            const string stderrCause = "self-test: failed: fixture validation step";
            const string eventCause = "AgentBridge.exe: fixture exception in fixture validation step";
            var evidence = Path.Combine(folder, "app-exit-cause.json");
            File.WriteAllText(evidence, JsonSerializer.Serialize(new
            {
                processId = 4242, exitCode = 1, failed = true, stderr = stderrCause,
                events = new[] { new { processId = 4242, message = eventCause } },
                captureErrors = Array.Empty<string>()
            }));
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
            var output = await stdout + await stderr;

            Assert.True(process.ExitCode == 0, output);
            Assert.True(output.Contains(stderrCause, StringComparison.Ordinal) &&
                output.Contains(eventCause, StringComparison.Ordinal),
                $"Recorded stderr and app-process event causes must reach diagnostic output.\n" +
                $"Fixture stderr: {stderrCause}\nFixture event: {eventCause}\nObserved output:\n{output}");
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }
}
