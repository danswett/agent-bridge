using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AgentBridge.Quality.Tests;

// build/test-app-self-test-cause.ps1 judges the evidence build/app-self-test.ps1 wrote for a FAILED installed
// self-test. main's win-arm64 run of 62c35b0a failed ONE check and the app exited normally with code 1: no crash,
// so no Windows event and no stderr exist, and the step failed on that missing crash evidence instead of
// leaving the failed check as the cause (job 137418151743).
public sealed class AppSelfTestCauseTests
{
    static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output."); }
        return root;
    }

    static (int Code, string Output) Judge(object evidence)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cause-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(evidence));
        try
        {
            var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-File", Path.Combine(Root(), "build", "test-app-self-test-cause.ps1"), "-Evidence", file])
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        finally { File.Delete(file); }
    }

    static object Evidence(int? exitCode, string[] failedChecks, string stderr = "", object[]? events = null, string[]? captureErrors = null) => new
    {
        processId = 9152, exitCode, failed = true, stderr, events = events ?? [], captureErrors = captureErrors ?? [], failedChecks,
    };

    [Fact]
    public void A_self_test_that_failed_a_check_and_exited_with_code_1_is_a_diagnosed_cause_without_a_crash_event()
    {
        var (code, output) = Judge(Evidence(1, ["holds nothing as the clear button in a text box of the question appears (held: True)"]));

        Assert.Equal(0, code);
        Assert.Contains("failed check", output);
    }

    // The wrapper's own line that reads result.json's failed checks, run as the wrapper runs it, under strict mode, for the
    // arm64 case: ONE failed check. An array of one assigned from an if is a string, and a string has no .Count (Codex, #3752).
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(0)]
    public void The_wrapper_counts_the_failed_checks_in_result_json_whether_there_is_one_or_many(int failures)
    {
        var result = Path.Combine(Path.GetTempPath(), $"result-{Guid.NewGuid():N}.json");
        File.WriteAllText(result, JsonSerializer.Serialize(new { failed = Enumerable.Range(1, failures).Select(i => $"check {i}").ToArray() }));
        try
        {
            var script = Path.Combine(Root(), "build", "app-self-test.ps1").Replace("'", "''");
            var command = "Set-StrictMode -Version Latest; $wrote = $true; $Result = '" + result.Replace("'", "''") + "'; " +
                "$ast = [System.Management.Automation.Language.Parser]::ParseFile('" + script + "', [ref]$null, [ref]$null); " +
                "$assign = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$failedChecks' }, $true) | Select-Object -First 1; " +
                "Invoke-Expression $assign.Extent.Text; $failedChecks.Count";
            var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-Command", command]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();

            Assert.Equal(failures.ToString(), output.Trim());
        }
        finally { File.Delete(result); }
    }

    [Fact]
    public void A_crash_with_no_stderr_or_event_is_still_an_evidence_gap()
    {
        var (code, output) = Judge(Evidence(-1073741819, []));

        Assert.NotEqual(0, code);
        Assert.Contains("ASSERTION FAILED", output);
    }

    [Fact]
    public void A_failed_check_with_a_capture_failure_is_still_an_evidence_gap()
    {
        var (code, _) = Judge(Evidence(1, ["a check"], captureErrors: ["app stderr capture failed: access denied"]));

        Assert.NotEqual(0, code);
    }

    [Fact]
    public void A_crash_with_stderr_and_its_event_is_diagnosed_as_before()
    {
        var (code, _) = Judge(Evidence(-1073741819, [], "boom", [new { processId = 9152, message = "Faulting application AgentBridge.exe" }]));

        Assert.Equal(0, code);
    }
}
