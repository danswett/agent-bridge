using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace AgentBridge.Quality.Tests;

public sealed class WindowsX64FactAttribute : FactAttribute
{
    public WindowsX64FactAttribute()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "The quarantine applies only to Windows x64.";
    }
}

[Trait("Category", "Process")]
public sealed class QuarantineTests
{
    const string Name = "AnswerFlowTests.A_form_stopped_after_every_field_is_resumed_at_most_three_times_and_then_left_with_no_Esc";
    const string Other = "UnlistedFailureTests.Fails";
    const string Expiry = "2026-11-16T07:59:00Z";
    static string Root => ChangedFiles.RepoRoot(AppContext.BaseDirectory);
    static string Script => Path.Combine(Root, "quality", "quarantine-filter.ps1");
    static string Manifest => Path.Combine(Root, "quality", "quarantine.json");

    [WindowsX64Fact]
    public async Task Quarantine_expires_by_manifest_time_and_missing_manifest_fails_closed()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Manifest));
        Assert.Equal(Expiry, manifest.RootElement.GetProperty("tests")[0].GetProperty("expires").GetString());
        var expires = DateTimeOffset.Parse(Expiry, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (now, active) in new[] { (expires.AddSeconds(-1), true), (expires, false), (expires.AddDays(1), false) })
        {
            var result = await Helper("-Mode", "Plan", "-Manifest", Manifest, "-Now", now.ToString("O"));
            Assert.Equal(0, result.ExitCode);
            using var plan = JsonDocument.Parse(result.Output);
            Assert.Equal(Name, plan.RootElement.GetProperty("name").GetString());
            Assert.Equal(active, plan.RootElement.GetProperty("active").GetBoolean());
            Assert.Equal($"FullyQualifiedName!={Name}", plan.RootElement.GetProperty("gateFilter").GetString());
        }
        var missing = await Helper("-Mode", "Plan", "-Manifest", Path.Combine(Root, "missing-quarantine.json"));
        Assert.NotEqual(0, missing.ExitCode);
    }

    [WindowsX64Fact]
    public async Task Changed_deadline_cannot_extend_the_authorization()
    {
        var dir = Directory.CreateTempSubdirectory("quarantine-expiry-").FullName;
        try
        {
            var path = Path.Combine(dir, "quarantine.json");
            var original = File.ReadAllText(Manifest);
            foreach (var changed in new[] { "2026-11-16T07:58:59Z", "2026-11-17T07:59:00Z" })
            {
                File.WriteAllText(path, original.Replace(Expiry, changed, StringComparison.Ordinal));
                var result = await Helper("-Mode", "Plan", "-Manifest", path, "-Now", "2026-11-16T07:59:01Z");
                Assert.NotEqual(0, result.ExitCode);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [WindowsX64Fact]
    public async Task Discovery_requires_exactly_one_named_test()
    {
        var dir = Directory.CreateTempSubdirectory("quarantine-discovery-").FullName;
        try
        {
            var path = Path.Combine(dir, "discovery.txt");
            foreach (var (text, succeeds) in new[] { ("other", false), ($"  {Name}\n", true), ($"{Name}\n{Name}\n", false) })
            {
                File.WriteAllText(path, text);
                var result = await Helper("-Mode", "Discovery", "-Evidence", path, "-Name", Name);
                Assert.Equal(succeeds, result.ExitCode == 0);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [WindowsX64Fact]
    public async Task Gate_requires_valid_evidence_of_executed_tests()
    {
        var dir = Directory.CreateTempSubdirectory("quarantine-gate-").FullName;
        try
        {
            Assert.NotEqual(0, (await Helper("-Mode", "Gate", "-Evidence", dir)).ExitCode);
            var path = Path.Combine(dir, "gate.trx");
            File.WriteAllText(path, "<broken");
            Assert.NotEqual(0, (await Helper("-Mode", "Gate", "-Evidence", dir)).ExitCode);
            File.WriteAllText(path, """<TestRun><ResultSummary><Counters executed="0" /></ResultSummary></TestRun>""");
            Assert.NotEqual(0, (await Helper("-Mode", "Gate", "-Evidence", dir)).ExitCode);
            File.WriteAllText(path, GateReport("AgentBridge.Other.Tests.dll", "Passed", 1));
            Assert.NotEqual(0, (await Helper("-Mode", "Gate", "-Evidence", dir)).ExitCode);
            var host = Path.Combine(dir, "host.trx");
            File.WriteAllText(host, GateReport("AgentBridge.Host.Tests.dll", "NotExecuted", 0));
            Assert.NotEqual(0, (await Helper("-Mode", "Gate", "-Evidence", dir)).ExitCode);
            File.WriteAllText(host, GateReport("AgentBridge.Host.Tests.dll", "NotExecuted", 1));
            Assert.NotEqual(0, (await Helper("-Mode", "Gate", "-Evidence", dir)).ExitCode);
            File.WriteAllText(host, GateReport("AgentBridge.Host.Tests.dll", "Passed", 1)
                .Replace("id=\"a\"", "id=\"\"", StringComparison.Ordinal).Replace("testId=\"a\"", "testId=\"\"", StringComparison.Ordinal));
            Assert.NotEqual(0, (await Helper("-Mode", "Gate", "-Evidence", dir)).ExitCode);
            File.WriteAllText(host, GateReport("AgentBridge.Host.Tests.dll", "Passed", 1));
            Assert.Equal(0, (await Helper("-Mode", "Gate", "-Evidence", dir)).ExitCode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [WindowsX64Fact]
    public async Task Report_distinguishes_test_failure_from_missing_invalid_or_aborted_evidence()
    {
        var dir = Directory.CreateTempSubdirectory("quarantine-report-").FullName;
        try
        {
            var path = Path.Combine(dir, "report.trx");
            Assert.NotEqual(0, (await Helper("-Mode", "Report", "-Evidence", path, "-Name", Name, "-TestExit", "1")).ExitCode);
            File.WriteAllText(path, "<broken");
            Assert.NotEqual(0, (await Helper("-Mode", "Report", "-Evidence", path, "-Name", Name, "-TestExit", "1")).ExitCode);
            File.WriteAllText(path, Report(Name, "Failed", "Failed", 0, 1));
            Assert.Equal(0, (await Helper("-Mode", "Report", "-Evidence", path, "-Name", Name, "-TestExit", "1")).ExitCode);
            Assert.NotEqual(0, (await Helper("-Mode", "Report", "-Evidence", path, "-Name", Name, "-TestExit", "2")).ExitCode);
            File.WriteAllText(path, Report("other", "Failed", "Failed", 0, 1));
            Assert.NotEqual(0, (await Helper("-Mode", "Report", "-Evidence", path, "-Name", Name, "-TestExit", "1")).ExitCode);
            File.WriteAllText(path, Report(Name, "Passed", "Completed", 1, 0));
            Assert.Equal(0, (await Helper("-Mode", "Report", "-Evidence", path, "-Name", Name, "-TestExit", "0")).ExitCode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [WindowsX64Fact]
    public async Task Vstest_excludes_only_the_named_failure_and_its_report_remains_verifiable()
    {
        var dir = Directory.CreateTempSubdirectory("quarantine-vstest-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Fixture.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>AgentBridge.Host.Tests</AssemblyName></PropertyGroup>
                <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="xunit" Version="2.9.3" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" /></ItemGroup></Project>
                """);
            File.WriteAllText(Path.Combine(dir, "Fixture.cs"), """
                using Xunit;
                public class AnswerFlowTests { [Fact] public void A_form_stopped_after_every_field_is_resumed_at_most_three_times_and_then_left_with_no_Esc() => Assert.Fail("QUARANTINE_LISTED_RED"); }
                public class UnlistedFailureTests { [Fact] public void Fails() => Assert.Fail("QUARANTINE_UNLISTED_RED"); }
                public class HealthyTests { [Fact] public void Passes() { } }
                """);
            var filter = "";
            var scriptExists = File.Exists(Script);
            var manifestExists = File.Exists(Manifest);
            if (scriptExists || manifestExists)
            {
                Assert.True(scriptExists && manifestExists, "Quarantine implementation is incomplete.");
                using var manifest = JsonDocument.Parse(File.ReadAllText(Manifest));
                var expires = manifest.RootElement.GetProperty("tests")[0].GetProperty("expires").GetDateTimeOffset();
                var planned = await Helper("-Mode", "Plan", "-Manifest", Manifest, "-Now", expires.AddSeconds(-1).ToString("O"));
                Assert.Equal(0, planned.ExitCode);
                using var plan = JsonDocument.Parse(planned.Output);
                Assert.True(plan.RootElement.GetProperty("active").GetBoolean());
                filter = plan.RootElement.GetProperty("gateFilter").GetString()!;
            }
            var passingFilter = string.IsNullOrEmpty(filter) ? $"FullyQualifiedName!={Other}" : $"{filter}&FullyQualifiedName!={Other}";
            var passing = await Test(dir, "passing", passingFilter);
            if (!scriptExists && !manifestExists)
            {
                Assert.NotEqual(0, passing.ExitCode);
                Assert.Contains("QUARANTINE_LISTED_RED", passing.Output, StringComparison.Ordinal);
                var redTrx = File.ReadAllText(passing.Trx);
                var results = XDocument.Parse(redTrx).Descendants().Where(e => e.Name.LocalName == "UnitTestResult");
                var listed = Assert.Single(results, e => (string?)e.Attribute("testName") == Name);
                Assert.Equal("Failed", (string?)listed.Attribute("outcome"));
                Assert.Contains("QUARANTINE_LISTED_RED", listed.ToString(), StringComparison.Ordinal);
                var healthy = Assert.Single(results, e => (string?)e.Attribute("testName") == "HealthyTests.Passes");
                Assert.Equal("Passed", (string?)healthy.Attribute("outcome"));
                var evidence = Path.Combine(Root, "TestResults", "QuarantineContract");
                Directory.CreateDirectory(evidence);
                File.Copy(passing.Trx, Path.Combine(evidence, $"red-fixture-{Guid.NewGuid():N}.trx"));
            }
            var message = !scriptExists && !manifestExists
                ? $"QUARANTINE_GATE_EXPECTED_SUCCESS: fixture console follows.{Environment.NewLine}{passing.Output}"
                : "QUARANTINE_GATE_EXPECTED_SUCCESS: the named failure must not gate.";
            Assert.True(passing.ExitCode == 0, message);
            var trx = File.ReadAllText(passing.Trx);
            Assert.Contains("HealthyTests.Passes", trx, StringComparison.Ordinal);
            Assert.DoesNotContain(Name, trx, StringComparison.Ordinal);
            Assert.Equal(0, (await Helper("-Mode", "Gate", "-Evidence", Path.Combine(dir, "passing"))).ExitCode);

            var gate = await Test(dir, "gate", filter);
            Assert.NotEqual(0, gate.ExitCode);
            Assert.DoesNotContain("QUARANTINE_LISTED_RED", gate.Output, StringComparison.Ordinal);
            Assert.Contains("QUARANTINE_UNLISTED_RED", gate.Output, StringComparison.Ordinal);
            var before = await Test(dir, "report", $"FullyQualifiedName={Name}");
            Assert.NotEqual(0, before.ExitCode);
            Assert.Contains("QUARANTINE_LISTED_RED", before.Output, StringComparison.Ordinal);
            Assert.Equal(0, (await Helper("-Mode", "Report", "-Evidence", before.Trx, "-Name", Name, "-TestExit", before.ExitCode.ToString())).ExitCode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static string Report(string name, string outcome, string summary, int passed, int failed) =>
        $"""<TestRun><Results><UnitTestResult testName="{name}" outcome="{outcome}" /></Results><ResultSummary outcome="{summary}"><Counters total="1" executed="1" passed="{passed}" failed="{failed}" /></ResultSummary></TestRun>""";

    static string GateReport(string assembly, string outcome, int executed) =>
        $"""<TestRun><TestDefinitions><UnitTest id="a" storage="C:\tests\{assembly}" /></TestDefinitions><Results><UnitTestResult testId="a" outcome="{outcome}" /></Results><ResultSummary><Counters executed="{executed}" /></ResultSummary></TestRun>""";

    static Task<(int ExitCode, string Output)> Helper(params string[] args) =>
        Run("pwsh", ["-NoProfile", "-File", Script, .. args]);

    static async Task<(int ExitCode, string Output, string Trx)> Test(string dir, string run, string filter)
    {
        var results = Path.Combine(dir, run);
        var trx = Path.Combine(results, run + ".trx");
        var result = await Run("dotnet", ["test", Path.Combine(dir, "Fixture.csproj"), "-c", "Release", "--nologo",
            "--results-directory", results, "--logger", $"trx;LogFileName={run}.trx", "--logger", "console;verbosity=normal", "--filter", filter]);
        return (result.ExitCode, result.Output, trx);
    }

    static async Task<(int ExitCode, string Output)> Run(string file, string[] args)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, $"{await stdout}{await stderr}");
    }
}
