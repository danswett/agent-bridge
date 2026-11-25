using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBridge.Quality.Tests;

// quality/ab-ci-watch.ps1: one read, when called, of the first same-repo PR run and the first main run of ci.yml after
// AB_CI_SELF_HOSTED goes back on (#4104): which runner took the product job, its time against the hosted median, and the
// failing or hung test if any. Its choices are pure functions, tested here without GitHub.
public sealed class AbCiWatchTests
{
    static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output."); }
        return root;
    }

    static string Run(string body)
    {
        var script = Path.Combine(Root(), "quality", "ab-ci-watch.ps1").Replace("'", "''");
        var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-Command", $". '{script}'; {body}"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        var error = p.StandardError.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
        return output;
    }

    static JsonObject RunRecord(long id, string created, string evt, string head = "danswett/agent-bridge", string branch = "x") => new()
    {
        ["id"] = id, ["created_at"] = created, ["event"] = evt, ["head_branch"] = branch,
        ["head_repository"] = new JsonObject { ["full_name"] = head },
    };

    [Fact]
    public void The_first_same_repo_PR_run_and_the_first_main_push_after_the_switch_are_chosen()
    {
        var runs = new JsonArray(
            RunRecord(9, "2026-11-24T18:30:00Z", "pull_request"),
            RunRecord(8, "2026-11-24T18:20:00Z", "push", branch: "main"),
            RunRecord(7, "2026-11-24T18:10:00Z", "pull_request", head: "someone/fork"),
            RunRecord(6, "2026-11-24T18:05:00Z", "pull_request"),
            RunRecord(5, "2026-11-24T17:50:00Z", "push", branch: "main"));
        var json = runs.ToJsonString().Replace("'", "''");

        var picked = JsonNode.Parse(Run($"Select-AbCiFirstRuns -Runs ('{json}' | ConvertFrom-Json) -Since ([datetime]'2026-11-24T18:00:00Z') -Repository 'danswett/agent-bridge' | ConvertTo-Json -Depth 5 -Compress"))!;

        // In order, the first few of each: a draft's run has no product job, so the next one is looked at.
        Assert.Equal([6L, 9L], picked["PullRequest"]!.AsArray().Select(r => (long)r!["id"]!));
        Assert.Equal([8L], picked["Main"]!.AsArray().Select(r => (long)r!["id"]!));
    }

    // ab-ci is an x64 pool: the arm64 product job runs hosted either way and is not the one to judge.
    [Fact]
    public void The_x64_product_job_is_judged_not_arm64_or_another_job()
    {
        var jobs = new JsonArray(
            new JsonObject { ["id"] = 1, ["name"] = "plan" },
            new JsonObject { ["id"] = 2, ["name"] = "product (windows-11-arm)" },
            new JsonObject { ["id"] = 3, ["name"] = "product (windows-latest)" },
            new JsonObject { ["id"] = 4, ["name"] = "product (ubuntu-latest)" }).ToJsonString().Replace("'", "''");

        Assert.Equal("3", Run($"(Select-AbCiProductJob -Jobs ('{jobs}' | ConvertFrom-Json)).id"));
    }

    [Theory]
    [InlineData("300,100,200", 200)]
    [InlineData("400,100,300,200", 250)]
    [InlineData("", -1)]
    public void The_hosted_median_is_the_middle_of_the_durations(string durations, double median)
    {
        var list = durations.Length == 0 ? "@()" : $"@({durations})";
        var output = Run($"$m = Get-AbCiMedian -Seconds {list}; if ($null -eq $m) {{ -1 }} else {{ $m }}");
        Assert.Equal(median, double.Parse(output, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_failed_test_and_a_hang_s_running_tests_are_named_from_the_job_log()
    {
        var log = string.Join("\n",
            "2026-11-24T04:31:30Z [xUnit.net 00:00:04.74]     AgentBridge.App.Tests.HostManagerTests.Quitting_during_an_update_brings_the_host_back [FAIL]",
            "2026-11-24T04:48:18Z The active test run was aborted. Reason: Test host process crashed",
            "2026-11-24T04:48:18Z The test running when the crash occurred: ",
            "2026-11-24T04:48:18Z SignInTests.A_late_code_from_a_cancelled_machine_is_never_held",
            "2026-11-24T04:48:18Z AutoUpdaterTests.A_local_restart_can_recover",
            "2026-11-24T04:48:18Z ",
            "2026-11-24T04:48:18Z This test may, or may not be the source of the crash.").Replace("'", "''");

        var named = JsonNode.Parse(Run($"Get-AbCiTestFailures -Log '{log}' | ConvertTo-Json -Compress"))!;

        Assert.Equal(["AgentBridge.App.Tests.HostManagerTests.Quitting_during_an_update_brings_the_host_back"], named["Failed"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["SignInTests.A_late_code_from_a_cancelled_machine_is_never_held", "AutoUpdaterTests.A_local_restart_can_recover"], named["Running"]!.AsArray().Select(n => (string)n!));
    }
}
