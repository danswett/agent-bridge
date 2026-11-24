using System.Diagnostics;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class EfficiencyActiveSessionTests
{
    [Fact]
    public Task Driver_emits_transcript_lines_hook_events_and_one_question_at_the_scripted_rates()
        => Run("rates");

    [Fact(Skip = "Quarantined: hosted timing flake; owner Main-Green; tracking issue https://github.com/danswett/agent-bridge/issues/3121; fix deadline 2026-11-19 02:00 PT")]
    public Task Driver_stops_promptly_and_leaves_no_timer_after_cancel()
        => Run("cancel");

    [Fact]
    public Task Viewer_attach_and_detach_leave_no_residue()
        => Run("viewer");

    static async Task Run(string scenario)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(root, "quality", "efficiency-active-session.Tests.ps1"), "-Scenario", scenario })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var error = process.StandardError.ReadToEndAsync();
        // pwsh's own start isn't the scenario's: cold, on a runner VM shared by several runners and test assemblies, it
        // took most of the 15 s (#4104). It has the 120 s the other pwsh tests give; the scenario keeps its 15 s from READY.
        using (var started = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            string? line;
            try { do { line = await process.StandardOutput.ReadLineAsync(started.Token); } while (line is not null and not "READY"); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            if (line is null) { await process.WaitForExitAsync(); Assert.Fail("pwsh ended before READY: " + await error); }
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, (await error) + (await stdout));
    }
}
