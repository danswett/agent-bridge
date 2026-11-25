using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// The rehearsal is run by agents, so the expected az sign-in defaults to the account az is already signed in as; the
// AGENT_BRIDGE_REHEARSAL_AZURE_USER variable is only an optional override. A fake az stands in: no real az, no subscription.
public class ResolveRehearsalAzureUserTests
{
    const string FakeAccount = "rehearsal-agent@example.com";

    static (int Exit, string Out) Run(string? overrideUser, string? signedInAs)
    {
        var dir = Directory.CreateTempSubdirectory("fake-az").FullName;
        try
        {
            var windows = OperatingSystem.IsWindows();
            var fake = Path.Combine(dir, windows ? "az.cmd" : "az");
            File.WriteAllText(fake, windows
                ? "@echo off\r\nif \"%FAKE_AZ_USER%\"==\"\" exit /b 1\r\necho %FAKE_AZ_USER%\r\n"
                : "#!/bin/sh\n[ -n \"$FAKE_AZ_USER\" ] || exit 1\necho \"$FAKE_AZ_USER\"\n");
            if (!windows) { File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            var script = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "build", "resolve-rehearsal-azure-user.ps1");
            var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-File", script, "-AzureConfigDir", dir]) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PATH"] = dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment.Remove("AGENT_BRIDGE_REHEARSAL_AZURE_USER");
            start.Environment.Remove("FAKE_AZ_USER");
            if (overrideUser is not null) { start.Environment["AGENT_BRIDGE_REHEARSAL_AZURE_USER"] = overrideUser; }
            if (signedInAs is not null) { start.Environment["FAKE_AZ_USER"] = signedInAs; }
            using var p = Process.Start(start)!;
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            // pwsh colours and wraps error text on a runner: compare plain words only.
            text = Regex.Replace(Regex.Replace(text, @"\[[0-9;]*m", ""), @"\s+", " ");
            return (p.ExitCode, text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Defaults_to_the_account_az_is_signed_in_as_and_says_which()
    {
        var (exit, text) = Run(null, FakeAccount);
        Assert.Equal(0, exit);
        Assert.Contains($"Rehearsal az account: {FakeAccount}", text);
    }

    [Fact]
    public void The_override_variable_wins_over_the_az_account()
    {
        var (exit, text) = Run("pinned-account@example.com", FakeAccount);
        Assert.Equal(0, exit);
        Assert.Contains("pinned-account@example.com", text);
        Assert.DoesNotContain(FakeAccount, text);
    }

    [Fact]
    public void No_az_sign_in_and_no_override_stops_with_a_plain_message()
    {
        var (exit, text) = Run(null, null);
        Assert.NotEqual(0, exit);
        Assert.Contains("az is not signed in", text);
        Assert.Contains("Nothing was started or stopped", text);
    }
}
