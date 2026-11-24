using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentBridge.Quality.Tests;

// setup/fix-abci-env.ps1 end to end (#4104), through its test-only seam: AGENT_BRIDGE_ABCI_TEST=1 runs it against a
// fake runner root (its numbered folders, a stand-in account, no service touched). Windows only (ACLs); starts pwsh,
// so ring 0 leaves it to ring 1.
[Trait("Category", "Process")]
public class AbCiEnvScriptTests
{
    // Guests and Power Users: well-known SIDs granted (or not) on the test's own scratch folders only.
    const string Runner = "S-1-5-32-546";
    const string Other = "S-1-5-32-547";
    const string Mirror = "https://packagefeedproxy.microsoft.io/nuget/v3/index.json";

    [Fact]
    public void It_writes_each_runners_env_with_dotnet_in_its_work_folder_and_keeps_other_lines()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var folder = Fixture.ScratchFolder();
        try
        {
            var root = Tree(folder, "root", 2);
            File.WriteAllLines(Path.Combine(root, "1", ".env"), ["LANG=C", "PATH=C:\\old", "DOTNET_INSTALL_DIR=C:\\old"]);

            var (exit, output) = Run(root);

            Assert.True(exit == 0, output);
            foreach (var n in new[] { "1", "2" })
            {
                var install = Path.Combine(root, n, "_work", "_tool", "dotnet");
                Assert.Contains($"Runner {n}: DOTNET_INSTALL_DIR={install}", output);
                var lines = File.ReadAllLines(Path.Combine(root, n, ".env"));
                Assert.Equal(n == "1" ? new[] { "LANG=C" } : Array.Empty<string>(), lines.Take(lines.Length - 4));
                Assert.Equal(["PATH=C:\\Windows", $"DOTNET_INSTALL_DIR={install}", $"AGENT_BRIDGE_NUGET_SOURCE={Mirror}", "DOTNET_CLI_TELEMETRY_OPTOUT=1"], lines.TakeLast(4));
            }
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    [Fact]
    public void WhatIf_prints_each_runners_install_dir_and_writes_nothing()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var folder = Fixture.ScratchFolder();
        try
        {
            var root = Tree(folder, "root", 1);

            var (exit, output) = Run(root, "-WhatIf");

            Assert.True(exit == 0, output);
            Assert.Contains($"Runner 1: DOTNET_INSTALL_DIR={Path.Combine(root, "1", "_work", "_tool", "dotnet")}", output);
            Assert.False(File.Exists(Path.Combine(root, "1", ".env")));
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    // A value that would end or split its .env line, or any refused runner, changes no runner's .env.
    [Theory]
    [InlineData("root", "-Mirror", "https://packagefeedproxy.microsoft.io/nuget/v3/index.json;x", ".env values can't hold a newline, ';' or '='")]
    [InlineData("root", "-Mirror", "https://packagefeedproxy.microsoft.io/nuget/v3/index.json?a=b", ".env values can't hold a newline, ';' or '='")]
    [InlineData("ro;ot", "", "", "can't be an .env value")]
    [InlineData("ro=ot", "", "", "can't be an .env value")]
    [InlineData("root", "other-writer", "", "writable by S-1-5-32-547")]
    [InlineData("root", "junction", "", "has a reparse point at")]
    public void A_bad_value_or_runner_refuses_the_whole_run_before_any_env_is_written(string rootName, string arg, string value, string problem)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var folder = Fixture.ScratchFolder();
        try
        {
            var root = Tree(folder, rootName, 2);
            if (arg == "other-writer") { Grant(Path.Combine(root, "2", "_work"), Other, InheritanceFlags.None); }
            if (arg == "junction")
            {
                var elsewhere = Directory.CreateDirectory(Path.Combine(folder, "elsewhere")).FullName;
                // A junction needs no privilege (a symbolic link does).
                using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", Path.Combine(root, "2", "_work", "_tool"), elsewhere]) { RedirectStandardOutput = true })!;
                mklink.StandardOutput.ReadToEnd();
                mklink.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }

            var (exit, output) = arg.StartsWith('-') ? Run(root, arg, value) : Run(root);

            Assert.NotEqual(0, exit);
            Assert.Contains("Refusing, nothing changed", output);
            Assert.Contains(problem, output);
            Assert.False(File.Exists(Path.Combine(root, "1", ".env")));
            Assert.False(File.Exists(Path.Combine(root, "2", ".env")));
        }
        finally
        {
            foreach (var link in Directory.EnumerateDirectories(folder, "_tool", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget != null).ToList())
                Directory.Delete(link);
            Fixture.Delete(folder);
        }
    }

    [Fact]
    public void A_fake_root_needs_the_test_flag_and_the_test_flag_refuses_the_real_root()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var folder = Fixture.ScratchFolder();
        try
        {
            var root = Tree(folder, "root", 1);

            var (exit, output) = Run(root, testFlag: false);
            Assert.NotEqual(0, exit);
            Assert.Contains("-Root other than C:\\ab-ci-runners is for tests only", output);

            (exit, output) = Run("C:\\ab-ci-runners", "-WhatIf");
            Assert.NotEqual(0, exit);
            Assert.Contains("AGENT_BRIDGE_ABCI_TEST is set: refusing to touch C:\\ab-ci-runners", output);
            Assert.False(File.Exists(Path.Combine(root, "1", ".env")));
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    // <folder>\<rootName> with inherited rules removed and only the test's own account (standing in for an admin)
    // granted; runners 1..n each with a _work the runner account may write.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string Tree(string folder, string rootName, int runners)
    {
        var root = Directory.CreateDirectory(Path.Combine(folder, rootName));
        var acl = root.GetAccessControl(AccessControlSections.Access);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        root.SetAccessControl(acl);
        for (var n = 1; n <= runners; n++)
            Grant(Directory.CreateDirectory(Path.Combine(root.FullName, $"{n}", "_work")).FullName, Runner, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        return root.FullName;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void Grant(string path, string sid, InheritanceFlags inheritance)
    {
        var dir = new DirectoryInfo(path);
        var acl = dir.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Modify, inheritance, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(acl);
    }

    static (int Exit, string Output) Run(string root, string arg = "", string value = "", bool testFlag = true)
    {
        var script = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "setup", "fix-abci-env.ps1");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        if (testFlag) { start.Environment["AGENT_BRIDGE_ABCI_TEST"] = "1"; }
        start.Environment["AGENT_BRIDGE_ABCI_TEST_ACCOUNT"] = Runner;
        start.Environment["AGENT_BRIDGE_ABCI_TEST_PATH"] = "C:\\Windows";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Root", root })
            start.ArgumentList.Add(a);
        if (arg.Length > 0) { start.ArgumentList.Add(arg); }
        if (value.Length > 0) { start.ArgumentList.Add(value); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }
}
