using System.Diagnostics;

namespace AgentBridge.Quality.Tests;

// setup/abci-install-dir.ps1, which fix-abci-env.ps1 uses to pick each ab-ci runner's DOTNET_INSTALL_DIR (#4104): run
// 45301025629 failed at setup-dotnet with no write access to the admin-owned <runner>\.dotnet. It starts pwsh, so ring 0
// leaves it to ring 1.
[Trait("Category", "Process")]
public class AbCiInstallDirTests
{
    // Guests and Power Users: well-known SIDs this test grants (or doesn't) on its own scratch folder only.
    const string Runner = "S-1-5-32-546";
    const string Other = "S-1-5-32-547";

    [Theory]
    [InlineData(".dotnet")]
    [InlineData("_work/../.dotnet")]
    [InlineData("_work-other/_tool/dotnet")]
    [InlineData("_work")]
    public void An_install_dir_outside_the_runners_work_folder_is_refused(string relative)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "_work"));
            var (exit, output) = Run(folder, $"Get-AbCiInstallDirProblems (Join-Path $env:T '_work') (Join-Path $env:T '{relative}') '{Runner}'");

            Assert.True(exit == 0, output);
            Assert.Contains("is outside the runner's work folder", output);
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
            Assert.Equal(Path.Combine(folder, "_work", "_tool", "dotnet").Replace('/', '\\'), output.Trim().Replace('/', '\\'));

            File.WriteAllText(Path.Combine(folder, ".runner"), """{ "agentName": "x", "workFolder": "w2" }""");
            (exit, output) = Run(folder, "Get-AbCiWorkFolder $env:T");
            Assert.True(exit == 0, output);
            Assert.Equal(Path.Combine(folder, "w2"), output.Trim());
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    // Windows ACLs: the runner account itself must be granted write on the work folder, inherited by new folders.
    [Theory]
    [InlineData("ContainerInherit, ObjectInherit", Runner, "")]
    [InlineData("ContainerInherit, ObjectInherit", Other, "can't be written by the runner account")]
    [InlineData("None", Runner, "can't be written by the runner account")]
    public void Inside_the_work_folder_the_runner_account_must_be_able_to_write_it(string inheritance, string granted, string problem)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var folder = Fixture.ScratchFolder();
        try
        {
            var (exit, output) = Run(folder,
                "$w = (New-Item -ItemType Directory (Join-Path $env:T '_work')).FullName; $acl = Get-Acl -LiteralPath $w; " +
                $"$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]'{granted}', 'Modify', '{inheritance}', 'None', 'Allow')); " +
                "Set-Acl -LiteralPath $w -AclObject $acl; " +
                $"$p = @(Get-AbCiInstallDirProblems $w (Get-AbCiInstallDir $w) '{Runner}'); 'problems=' + $p.Count; $p");

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
