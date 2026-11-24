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

    // The ab-ci legs' config (ci.yml product, self-hosted only; #4218): the pinned host, and nuget.org-signed packages only.
    [Fact]
    public void The_ab_ci_config_pins_the_mirror_and_requires_a_nuget_org_repository_signature()
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var path = Path.Combine(folder, "mirror.nuget.config");
            var (exit, error) = Generate("https://packagefeedproxy.microsoft.io/nuget/v3/index.json", path, "New-AbCiNuGetConfig");

            Assert.True(exit == 0, error);
            var configuration = XDocument.Load(path).Root!;
            Assert.Equal("require", configuration.Element("config")!.Elements("add").Single(e => e.Attribute("key")!.Value == "signatureValidationMode").Attribute("value")!.Value);
            foreach (var section in new[] { "packageSources", "auditSources" })
            {
                var entries = configuration.Element(section)!.Elements().ToList();
                Assert.Equal(["clear", "add"], entries.Select(e => e.Name.LocalName));
                Assert.Equal("https://packagefeedproxy.microsoft.io/nuget/v3/index.json", entries[1].Attribute("value")!.Value);
            }
            Assert.Equal(["clear"], configuration.Element("fallbackPackageFolders")!.Elements().Select(e => e.Name.LocalName));
            var repository = configuration.Element("trustedSigners")!.Elements("repository").Single();
            Assert.Equal("nuget.org", repository.Attribute("name")!.Value);
            // nuget.org's current and two earlier repository certificates: packages signed under each are still served.
            Assert.Equal(["1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D", "5A2901D6ADA3D18260B9C6DFE2133C95D74B9EEF6AE0E5DC334C8454D1477DF4", "0E5F38F57DC1BCC806D8494F4F90FBCEDD988B46760709CBEEC6F4219AA6157D"],
                repository.Elements("certificate").Select(c => c.Attribute("fingerprint")!.Value));
            Assert.All(repository.Elements("certificate"), c => Assert.Equal("false", c.Attribute("allowUntrustedRoot")!.Value));
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    [Theory]
    [InlineData("https://evil.example/nuget/v3/index.json")]
    [InlineData("https://packagefeedproxy.microsoft.io.evil.example/nuget/v3/index.json")]
    [InlineData("https://evil.example/packagefeedproxy.microsoft.io/nuget/v3/index.json")]
    [InlineData("https://user:pw@packagefeedproxy.microsoft.io/nuget/v3/index.json")]
    [InlineData("https://packagefeedproxy.microsoft.io@evil.example/nuget/v3/index.json")]
    [InlineData("https://packagefeedproxy.microsoft.io:8443/nuget/v3/index.json")]
    [InlineData("https://packagefeedproxy.microsoft.io:443/nuget/v3/index.json")]
    [InlineData("http://packagefeedproxy.microsoft.io/nuget/v3/index.json")]
    [InlineData("https://packagefeedproxy.microsoft.io/nuget/v3/index.json?feed=x")]
    [InlineData("https://packagefeedproxy.microsoft.io/nuget/v3/index.json#x")]
    [InlineData("")]
    public void The_ab_ci_config_refuses_any_other_mirror_and_never_echoes_it(string source)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var path = Path.Combine(folder, "mirror.nuget.config");
            var (exit, error) = Generate(source, path, "New-AbCiNuGetConfig");

            Assert.NotEqual(0, exit);
            Assert.Contains("AGENT_BRIDGE_NUGET_SOURCE must be https://packagefeedproxy.microsoft.io/", error);
            Assert.False(File.Exists(path));
            if (source.Length > 0) { Assert.DoesNotContain(source, error); }
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    static (int Exit, string Error) Generate(string source, string path, string function = "New-NuGetMirrorConfig")
    {
        var script = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", "nuget-mirror.ps1");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        // Through the environment, so no value needs quoting for PowerShell.
        start.Environment["MIRROR_TEST_SCRIPT"] = script;
        start.Environment["MIRROR_TEST_SOURCE"] = source;
        start.Environment["MIRROR_TEST_PATH"] = path;
        // pwsh's own error view is coloured (ANSI) and wrapped to the console width on Linux, which split the message in
        // CI (#4218). The script's message is written as is instead: no formatting to depend on, on any platform.
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "$ErrorActionPreference = 'Stop'; try { . $env:MIRROR_TEST_SCRIPT; " + function + " -Source $env:MIRROR_TEST_SOURCE -Path $env:MIRROR_TEST_PATH } " +
                     "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error.Result);
    }
}
