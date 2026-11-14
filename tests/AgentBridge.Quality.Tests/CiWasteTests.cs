using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class CiWasteTests
{
    private static JsonObject Snapshot() => JsonNode.Parse(File.ReadAllText(Path.Combine(
        ChangedFiles.RepoRoot(AppContext.BaseDirectory), "tests", "fixtures", "ci-waste", "runs.json")))!.AsObject();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_runs_report_observed_waste_without_inventing_missing_evidence(bool enrich)
    {
        var snapshot = Snapshot();
        if (!enrich)
        {
            snapshot["workflow_runs"]![0]!["jobs"] = null;
            snapshot["workflow_runs"]![0]!["plan"] = null;
        }
        if (enrich)
        {
            foreach (var entry in snapshot["workflow_runs"]!.AsArray())
            {
                var run = entry!.AsObject();
                run["jobs"] = new JsonArray();
                run["plan"] = JsonNode.Parse("""{"changed_files":["docs/example.md"],"long_jobs":[]}""");
                if (run["id"]!.GetValue<long>() == 43586721146)
                    run["plan"] = JsonNode.Parse("""{"changed_files":["app/example.cs"],"long_jobs":["app"]}""");
                if (run["id"]!.GetValue<long>() == 43553451372)
                    run["jobs"] = new JsonArray(
                        Job(run, "pwa-ui (chromium 1/3)"), Job(run, "pwa-ui (chromium 2/3)"),
                        Job(run, "pwa-ui (chromium 3/3)"), Job(run, "pwa-ui (firefox)"),
                        Job(run, "pwa-ui (webkit 1/3)"),
                        Job(run, "pwa-ui (chromium 1/3)", "completed", "skipped"),
                        Job(run, "pwa-ui (chromium 2/3)", "queued"),
                        Job(run, "app (win-x64)", "completed", "skipped"), Job(run, "app-installer (win-x64)", "queued"));
                if (run["id"]!.GetValue<long>() == 43547869768)
                    run["jobs"] = new JsonArray(Job(run, "app (win-arm64)"), Job(run, "app-installer (win-x64)"));
            }
        }
        var result = await Report(snapshot);
        var waste = result["waste"]!;
        Assert.Equal(60, result["sampledRuns"]!.GetValue<int>());
        Assert.Equal(2, waste["cancelled"]!.GetValue<int>());
        Assert.Equal(1, waste["supersededObserved"]!.GetValue<int>());
        Assert.Equal(3, waste["failedThenReplacedObserved"]!.GetValue<int>());
        Assert.Equal(2, waste["observedWastedRuns"]!.GetValue<int>());
        Assert.Equal(42, waste["unknownPrRuns"]!.GetValue<int>());
        Assert.Equal("PARTIAL", waste["status"]!.GetValue<string>());
        Assert.Equal(enrich ? "KNOWN" : "UNKNOWN", result["longJobs"]!["status"]!.GetValue<string>());
        Assert.Equal(enrich ? 0 : 60, result["longJobs"]!["unknownRuns"]!.GetValue<int>());
        Assert.Equal(enrich ? new[] { "app (win-arm64)", "app-installer (win-x64)",
                "pwa-ui (chromium 1/3)", "pwa-ui (chromium 2/3)", "pwa-ui (chromium 3/3)" } : Array.Empty<string>(),
            result["longJobs"]!["burnedInObservedWaste"]!.AsArray().Select(j => j!["name"]!.GetValue<string>())
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(enrich ? "KNOWN" : "UNKNOWN", result["noLongJobPushes"]!["status"]!.GetValue<string>());
        Assert.Equal(enrich ? 0 : 60, result["noLongJobPushes"]!["unknownRuns"]!.GetValue<int>());
        Assert.Equal(enrich ? 58 : 0, result["noLongJobPushes"]!["observed"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Reruns_and_finish_timing_distinguish_supersession_from_replacement(bool sameHead, bool beforeFinish)
    {
        var runs = Snapshot()["workflow_runs"]!.AsArray();
        var older = runs.Single(r => r!["id"]!.GetValue<long>() == 43547869768)!.DeepClone();
        var newer = runs.Single(r => r!["id"]!.GetValue<long>() == 43549544866)!.DeepClone();
        older["conclusion"] = "failure";
        if (sameHead)
            newer["head_sha"] = older["head_sha"]!.DeepClone();
        else if (!beforeFinish)
            older["updated_at"] = newer["created_at"]!.DeepClone();
        var result = await Report(new JsonObject { ["workflow_runs"] = new JsonArray(older, newer) });
        Assert.Equal(beforeFinish ? 1 : 0, result["waste"]!["supersededObserved"]!.GetValue<int>());
        Assert.Equal(sameHead || beforeFinish ? 0 : 1, result["waste"]!["failedThenReplacedObserved"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_window_uses_the_latest_sixty_pr_runs_not_input_order_or_other_events()
    {
        var snapshot = Snapshot();
        var runs = snapshot["workflow_runs"]!.AsArray();
        var older = runs[0]!.DeepClone();
        older["id"] = 1;
        older["created_at"] = "2026-11-13T00:00:00Z";
        older["conclusion"] = "cancelled";
        runs.Insert(0, older);
        var push = older.DeepClone();
        push["id"] = 2;
        push["event"] = "push";
        push["created_at"] = "2026-11-14T11:00:00Z";
        runs.Add(push);
        var result = await Report(snapshot);
        Assert.Equal(60, result["sampledRuns"]!.GetValue<int>());
        Assert.Equal(2, result["waste"]!["cancelled"]!.GetValue<int>());
    }

    private static JsonObject Job(JsonNode run, string name, string status = "completed", string conclusion = "success") =>
        new() { ["name"] = name, ["status"] = status, ["conclusion"] = conclusion,
            ["started_at"] = run["run_started_at"]!.DeepClone() };

    private static async Task<JsonObject> Report(JsonObject snapshot)
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"ci-waste-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(fixture, snapshot.ToJsonString());
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(
                         ChangedFiles.RepoRoot(AppContext.BaseDirectory), "tools", "measure-ci-waste.ps1"),
                         "-RunsFile", fixture })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            var error = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout;
            Assert.True(process.ExitCode == 0, (await error) + output);
            return JsonNode.Parse(output)!.AsObject();
        }
        finally { File.Delete(fixture); }
    }
}
