using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class SelfTestMeasurementTests
{
    [Fact]
    public async Task Queue_wait_uses_attempt_one_and_missing_plan_is_unknown_not_zero()
    {
        var runs = """
            {"workflow_runs":[
              {"id":1,"event":"push","head_branch":"main","run_attempt":2,"run_started_at":"2026-11-17T02:00:00Z",
               "attempt1":{"run_started_at":"2026-11-17T01:00:00Z","status":"completed"},
               "jobs":[{"name":"plan","started_at":"2026-11-17T01:04:00Z","completed_at":"2026-11-17T01:05:00Z"},
                       {"name":"app","completed_at":"2026-11-17T01:10:00Z"}]},
              {"id":2,"event":"push","head_branch":"main","run_attempt":1,"run_started_at":"2026-11-17T03:00:00Z",
               "status":"completed","jobs":[]},
              {"id":3,"event":"pull_request","head_branch":"main","run_attempt":1}]}
            """;
        var output = JsonNode.Parse(await Run("main-queue-wait.ps1", runs, "-Json"))!.AsArray();
        Assert.Equal(2, output.Count);
        var measured = output.Single(r => r!["RunId"]!.GetValue<int>() == 1)!;
        Assert.Equal(4, measured["WaitMinutes"]!.GetValue<double>());
        Assert.Equal(10, measured["TotalMinutes"]!.GetValue<double>());
        var missing = output.Single(r => r!["RunId"]!.GetValue<int>() == 2)!;
        Assert.Null(missing["WaitMinutes"]);
        Assert.Null(missing["TotalMinutes"]);
    }

    [Fact]
    public async Task Summary_shows_only_the_slowest_ten_and_total_seconds_without_altering_results()
    {
        var checks = new JsonArray(Enumerable.Range(1, 12).Select(i => (JsonNode)new JsonObject
            { ["name"] = $"check {i}|<tag>", ["durationMs"] = i * 1000 }).ToArray());
        var result = new JsonObject { ["durationMs"] = 78000, ["checkDurations"] = checks };
        var output = await Run("self-test-summary.ps1", result.ToJsonString());
        Assert.Contains("78.000 s", output);
        Assert.Contains("check 12&#124;&lt;tag&gt;", output);
        Assert.DoesNotContain("check 2&#124;", output);
        Assert.Equal(10, output.Split('\n').Count(l => l.StartsWith("| check", StringComparison.Ordinal)));
        Assert.True(output.IndexOf("check 12", StringComparison.Ordinal) < output.IndexOf("check 3", StringComparison.Ordinal));
    }

    static async Task<string> Run(string script, string input, params string[] args)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var source = Path.Combine(folder, "input.json");
            var summary = Path.Combine(folder, "summary.md");
            await File.WriteAllTextAsync(source, input);
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(
                ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", script),
                script.StartsWith("main-", StringComparison.Ordinal) ? "-RunsFile" : "-Result", source }.Concat(args))
                start.ArgumentList.Add(arg);
            start.Environment["GITHUB_STEP_SUMMARY"] = summary;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("Measurement script exceeded 30 seconds.");
            }
            Assert.True(process.ExitCode == 0, (await stderr) + (await stdout));
            Assert.Equal(input, await File.ReadAllTextAsync(source));
            return File.Exists(summary) ? await File.ReadAllTextAsync(summary) : await stdout;
        }
        finally { Fixture.Delete(folder); }
    }
}
