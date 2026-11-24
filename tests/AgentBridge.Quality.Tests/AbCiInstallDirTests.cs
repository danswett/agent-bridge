using System.Diagnostics;
using System.Text.Json;

namespace AgentBridge.Quality.Tests;

// setup/abci-install-dir.ps1, which fix-abci-env.ps1 uses to pick each ab-ci runner's DOTNET_INSTALL_DIR (#4104): run
// 45301025629 failed at setup-dotnet with no write access to the admin-owned <runner>\.dotnet. It starts pwsh, so ring 0
// leaves it to ring 1.
[Trait("Category", "Process")]
public class AbCiInstallDirTests
{
    // Guests and Power Users: well-known SIDs this test grants (or doesn't) on its own scratch folders only.
    const string Runner = "S-1-5-32-546";
    const string Other = "S-1-5-32-547";

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("C:\\work")]
    [InlineData("C:work")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\C:\\work")]
    [InlineData("_work.")]
    [InlineData("_work ")]
    [InlineData(" _work")]
    [InlineData("NUL")]
    [InlineData("com1.txt")]
    [InlineData("CONIN$")]
    [InlineData("_work:stream")]
    public void A_work_folder_that_isnt_one_plain_name_in_the_runner_folder_is_refused(string workFolder)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, ".runner"), JsonSerializer.Serialize(new { agentName = "x", workFolder }));
            var (exit, output) = Run(folder, "Get-AbCiWorkFolder $env:T");

            Assert.NotEqual(0, exit);
            Assert.Contains("isn't one plain folder name in the runner folder", output);
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    [Fact]
    public void The_install_dir_is_tool_dotnet_in_the_work_folder_runner_names()
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var (exit, output) = Run(folder, "Get-AbCiInstallDir (Get-AbCiWorkFolder $env:T)");
            Assert.True(exit == 0, output);
            Assert.Equal(Path.Combine(folder, "_work", "_tool", "dotnet"), output.Trim());

            foreach (var (written, expected) in new[] { ("w2", "w2"), ("", "_work"), ("_work.2", "_work.2") })
            {
                File.WriteAllText(Path.Combine(folder, ".runner"), JsonSerializer.Serialize(new { agentName = "x", workFolder = written }));
                (exit, output) = Run(folder, "Get-AbCiWorkFolder $env:T");
                Assert.True(exit == 0, output);
                Assert.Equal(Path.Combine(folder, expected), output.Trim());
            }
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    [Theory]
    [InlineData(".dotnet")]
    [InlineData("_work/../.dotnet")]
    [InlineData("_work-other/_tool/dotnet")]
    [InlineData("_work")]
    [InlineData("../2/_work/_tool/dotnet")]
    public void An_install_dir_outside_the_runners_work_folder_is_refused(string relative)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "_work"));
            var (exit, output) = Run(folder, $"Get-AbCiInstallDirProblems $env:T (Join-Path $env:T '{relative}') '{Runner}'");

            Assert.True(exit == 0, output);
            Assert.Contains("is outside the runner's work folder", output);
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    // Windows ACLs, on <scratch>\root\1\_work: root's inherited rules are removed and only the test's own account (standing
    // in for an admin) is granted; each case then adds what it names. $runner and $other are the SIDs above.
    [Theory]
    [InlineData("Grant $w $runner 'ContainerInherit, ObjectInherit'", "")]
    [InlineData("Grant $w $runner 'ContainerInherit, ObjectInherit'; New-Item -ItemType Directory (Join-Path $w '_tool') | Out-Null", "")]
    [InlineData("", "can't be written by the runner account")]
    [InlineData("Grant $w $other 'ContainerInherit, ObjectInherit'", "can't be written by the runner account")]
    [InlineData("Grant $w $runner 'None'", "can't be written by the runner account")]
    [InlineData("Grant $w $runner 'ContainerInherit, ObjectInherit'; $t = New-Item -ItemType Directory (Join-Path $w '_tool'); Grant $t $other 'None'", "_tool writable by S-1-5-32-547")]
    [InlineData("Grant $w $runner 'ContainerInherit, ObjectInherit'; Grant $w $other 'None'", "_work writable by S-1-5-32-547")]
    [InlineData("Grant $w $runner 'ContainerInherit, ObjectInherit'; Grant $r $other 'None'", "1 writable by S-1-5-32-547")]
    [InlineData("Grant $w $runner 'ContainerInherit, ObjectInherit'; Grant $root $other 'None'", "root writable by S-1-5-32-547")]
    [InlineData("Grant $w $runner 'ContainerInherit, ObjectInherit'; New-Item -ItemType Junction (Join-Path $w '_tool') -Target (New-Item -ItemType Directory (Join-Path $env:T 'elsewhere')).FullName | Out-Null", "has a reparse point at")]
    public void Up_to_the_runner_root_only_an_admin_and_the_runners_own_account_may_write(string setup, string problem)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var folder = Fixture.ScratchFolder();
        try
        {
            var (exit, output) = Run(folder,
                "$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; " +
                $"$runner = '{Runner}'; $other = '{Other}'; " +
                "function Get-Access($Path) { [IO.FileSystemAclExtensions]::GetAccessControl([IO.DirectoryInfo]$Path, 'Access') }; " +
                "function Set-Access($Path, $Acl) { [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]$Path, $Acl) }; " +
                "function Grant($Path, $Sid, $Inherit) { $a = Get-Access $Path; " +
                "$a.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]$Sid, 'Modify', $Inherit, 'None', 'Allow')); " +
                "Set-Access $Path $a }; " +
                "$root = (New-Item -ItemType Directory (Join-Path $env:T 'root')).FullName; " +
                "$a = Get-Access $root; $a.SetAccessRuleProtection($true, $false); " +
                "$a.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]$me, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')); " +
                "Set-Access $root $a; " +
                "$r = (New-Item -ItemType Directory (Join-Path $root '1')).FullName; $w = (New-Item -ItemType Directory (Join-Path $r '_work')).FullName; " +
                setup + "; " +
                "$p = @(Get-AbCiInstallDirProblems $r (Get-AbCiInstallDir (Get-AbCiWorkFolder $r)) $runner @($me)); 'problems=' + $p.Count; $p; " +
                // A junction is removed as a link (not followed), so the scratch folder can be deleted.
                "Get-ChildItem -LiteralPath $env:T -Recurse -Directory -Attributes ReparsePoint | ForEach-Object { [IO.Directory]::Delete($_.FullName) }");

            Assert.True(exit == 0, output);
            if (problem.Length == 0) { Assert.Contains("problems=0", output); }
            else { Assert.Contains(problem, output); }
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    static (int Exit, string Output) Run(string folder, string command)
    {
        var script = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "setup", "abci-install-dir.ps1");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["INSTALL_DIR_TEST_SCRIPT"] = script;
        start.Environment["T"] = folder;
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "$ErrorActionPreference = 'Stop'; try { . $env:INSTALL_DIR_TEST_SCRIPT; " + command + " } " +
                     "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }
}
