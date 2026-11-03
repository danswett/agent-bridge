using System.Diagnostics;
using System.Xml.Linq;

namespace AgentBridge.Quality.Tests;

// quality/nuget-mirror.ps1, which run.ps1 uses when AGENT_BRIDGE_NUGET_SOURCE is set (#475). It
// starts pwsh, so ring 0 leaves it to ring 1.
[Trait("Category", "Process")]
public class NuGetMirrorTests
{
    const string Mirror = "https://mirror.example/nuget/v3/index.json?feed=a&b";

    [Fact]
    public void The_mirror_config_makes_the_mirror_the_only_package_source_and_the_only_audit_source()
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var path = Path.Combine(folder, "nested", "nuget.mirror.config");
            var (exit, error) = Generate(Mirror, path);

            Assert.True(exit == 0, error);
            var configuration = XDocument.Load(path).Root!;
            // Audit falls back to nuget.config's sources unless it has its own, which is what
            // failed with NU1900 when only the package sources were overridden.
            foreach (var section in new[] { "packageSources", "auditSources" })
            {
                var entries = configuration.Element(section)!.Elements().ToList();
                Assert.Equal(["clear", "add"], entries.Select(e => e.Name.LocalName));
                Assert.Equal(Mirror, entries[1].Attribute("value")!.Value);
            }
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    [Fact]
    public void A_feed_name_instead_of_a_url_is_refused_before_restore_sees_it()
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var path = Path.Combine(folder, "nuget.mirror.config");
            var (exit, error) = Generate("azure-default", path);

            Assert.NotEqual(0, exit);
            Assert.Contains("AGENT_BRIDGE_NUGET_SOURCE must be", error);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    static (int Exit, string Error) Generate(string source, string path)
    {
        var script = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", "nuget-mirror.ps1");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        // Through the environment, so no value needs quoting for PowerShell.
        start.Environment["MIRROR_TEST_SCRIPT"] = script;
        start.Environment["MIRROR_TEST_SOURCE"] = source;
        start.Environment["MIRROR_TEST_PATH"] = path;
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "$ErrorActionPreference = 'Stop'; . $env:MIRROR_TEST_SCRIPT; New-NuGetMirrorConfig -Source $env:MIRROR_TEST_SOURCE -Path $env:MIRROR_TEST_PATH" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error.Result);
    }
}
