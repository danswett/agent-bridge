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
    static string Root => ChangedFiles.RepoRoot(AppContext.BaseDirectory);
    static string Script => Path.Combine(Root, "quality", "quarantine-filter.ps1");
    static string Manifest => Path.Combine(Root, "quality", "quarantine.json");

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
