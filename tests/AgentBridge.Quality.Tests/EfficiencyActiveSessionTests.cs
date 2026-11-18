using System.Diagnostics;
using System.Text;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class EfficiencyActiveSessionTests
{
    [Fact]
    public Task Driver_emits_transcript_lines_hook_events_and_one_question_at_the_scripted_rates()
        => Run("rates");

    [Fact]
    public Task Driver_stops_promptly_and_leaves_no_timer_after_cancel()
        => Run("cancel");

    [Fact]
    public Task Viewer_attach_and_detach_leave_no_residue()
        => Run("viewer");

    [Fact]
    public Task Slow_startup_does_not_consume_the_scenario_deadline()
        => RunFakeDriver("Start-Sleep -Seconds 3; Write-Output 'READY: efficiency-active-session'");

    [Fact]
    public async Task A_driver_that_never_stops_reports_the_phase_and_both_output_streams()
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(() => RunFakeDriver(
            "Write-Output 'driver output'; [Console]::Error.WriteLine('driver error'); " +
            "Write-Output 'READY: efficiency-active-session'; Start-Sleep -Seconds 60"));
        Assert.Contains("scenario", error.Message);
        Assert.Contains("driver output", error.Message);
        Assert.Contains("driver error", error.Message);
    }

    static Task RunFakeDriver(string script)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
            start.ArgumentList.Add(arg);
        return Run(start, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));
    }

    static Task Run(string scenario)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(root, "quality", "efficiency-active-session.Tests.ps1"), "-Scenario", scenario })
            start.ArgumentList.Add(arg);
        return Run(start, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15));
    }

    static async Task Run(ProcessStartInfo start, TimeSpan startupTimeout, TimeSpan scenarioTimeout)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        async Task CaptureOutput()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                output.AppendLine(line);
                if (line == "READY: efficiency-active-session")
                    ready.TrySetResult();
            }
        }
        var error = process.StandardError.ReadToEndAsync();
        var stdout = CaptureOutput();
        var exited = process.WaitForExitAsync();
        var phase = "startup";
        try
        {
            await Task.WhenAny(ready.Task, exited).WaitAsync(startupTimeout);
            if (exited.IsCompleted)
            {
                await stdout;
                Assert.True(ready.Task.IsCompletedSuccessfully,
                    $"Driver exited before ready.\nstdout:\n{output}\nstderr:\n{await error}");
            }
            phase = "scenario";
            await exited.WaitAsync(scenarioTimeout);
        }
        catch (TimeoutException exception)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await exited;
            await stdout;
            throw new TimeoutException(
                $"Driver timed out during {phase}.\nstdout:\n{output}\nstderr:\n{await error}", exception);
        }
        await stdout;
        Assert.True(process.ExitCode == 0, $"stdout:\n{output}\nstderr:\n{await error}");
    }
}
