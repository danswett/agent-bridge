using System.Diagnostics;
using System.Text.Json;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class CiWasteScriptTests
{
    [Fact]
    public async Task Cancelled_and_overlapping_old_heads_count_once_but_completed_and_same_head_runs_are_not_superseded()
    {
        var runs = new[]
        {
            Run(1, "first", "a", 0, 10, "cancelled", null,
                Job("pwa-ui (chromium 1/3)", 1, 7), Job("app (win-x64)", 1, 6),
                Job("app-installer (win-x64)", 7, 9), Job("pwa-ui (webkit 1/3)", 1, 10),
                Job("app (win-arm64)", 1, 10, "skipped")),
            Run(2, "first", "b", 5, 15, "success", ["docs/guide.md"], Job("pwa-ui (chromium 2/3)", 6, 7)),
            Run(3, "second", "c", 0, 4, "success", null, Job("app (win-x64)", 0, 4)),
            Run(4, "second", "d", 20, 24, "success", ["src/AgentBridge.App/View.cs"], Job("app (win-x64)", 20, 22)),
            Run(5, "third", "e", 0, 10, "cancelled", null, Job("app (win-arm64)", 1, 7)),
            Run(6, "third", "e", 5, 12, "success", null, Job("app (win-x64)", 5, 7)),
        };
        using var report = await Measure(runs.Reverse().ToArray());
        var result = report.RootElement;
        Assert.Equal(6, result.GetProperty("runs").GetInt32());
        Assert.Equal(2, result.GetProperty("cancelled").GetInt32());
        Assert.Equal(1, result.GetProperty("superseded").GetInt32());
        Assert.Equal(2, result.GetProperty("cancelled_or_superseded").GetInt32());
        Assert.Equal(19, result.GetProperty("wasted_long_job_minutes").GetDouble());
        Assert.Equal(2, result.GetProperty("cancelled_runs_over_five_minutes").GetInt32());
        Assert.Equal(1, result.GetProperty("no_long_job_pushes").GetInt32());
        Assert.Equal(1, result.GetProperty("no_long_job_pushes_that_started_long_jobs").GetInt32());
        Assert.Equal(4, result.GetProperty("unknown_pushes").GetInt32());
        var wasted = result.GetProperty("long_jobs").EnumerateArray().ToDictionary(
            row => row.GetProperty("job").GetString()!, row => row.GetProperty("wasted_minutes").GetDouble());
        Assert.Equal(6, wasted["pwa-ui chromium"]);
        Assert.Equal(11, wasted["app"]);
        Assert.Equal(2, wasted["app-installer"]);
        Assert.Equal(new[] { "docs/guide.md" }, result.GetProperty("no_long_job_push_list")[0]
            .GetProperty("files").EnumerateArray().Select(file => file.GetString()));
    }

    [Fact]
    public async Task Missing_deltas_stay_unknown_and_running_job_time_is_clipped_to_the_snapshot()
    {
        var active = Run(7, "active", "f", 27, 30, null, null,
            Job("pwa-ui (chromium)", 28, null, null), Job("app (win-x64)", null, null, null));
        using var report = await Measure([active]);
        var result = report.RootElement;
        Assert.Equal(1, result.GetProperty("unknown_pushes").GetInt32());
        Assert.Equal(0, result.GetProperty("no_long_job_pushes").GetInt32());
        Assert.Equal(0, result.GetProperty("wasted_long_job_minutes").GetDouble());
        var chromium = result.GetProperty("long_jobs").EnumerateArray()
            .Single(row => row.GetProperty("job").GetString() == "pwa-ui chromium");
        Assert.Equal(2, chromium.GetProperty("minutes").GetDouble());
        Assert.Equal(1, chromium.GetProperty("started").GetInt32());
    }

    static string At(int minute) => new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero)
        .AddMinutes(minute).ToString("O");

    static object Job(string name, int? start, int? end, string? conclusion = "success") => new
    {
        name, started_at = start.HasValue ? At(start.Value) : null,
        completed_at = end.HasValue ? At(end.Value) : null, conclusion,
        status = conclusion is null ? start.HasValue ? "in_progress" : "queued" : "completed",
    };

    static object Run(int id, string branch, string sha, int start, int end, string? conclusion,
        string[]? files, params object[] jobs) => new
    {
        id, head_branch = branch, head_sha = sha, created_at = At(start), updated_at = At(end),
        status = conclusion is null ? "in_progress" : "completed", conclusion, jobs, files,
        html_url = $"https://example.test/runs/{id}",
        head_repository = new { full_name = "example/repo" },
    };

    static async Task<JsonDocument> Measure(object[] runs)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var input = Path.Combine(folder, "runs.json");
            File.WriteAllText(input, JsonSerializer.Serialize(new { captured_at = At(30), runs }));
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = ChangedFiles.RepoRoot(AppContext.BaseDirectory),
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
                         Path.Combine(start.WorkingDirectory, "quality", "ci-waste.ps1"), "-RunsFile", input })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, (await stderr) + (await stdout));
            return JsonDocument.Parse(await stdout);
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }
}
