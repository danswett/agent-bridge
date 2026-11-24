using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentBridge.Host.Migration;
using Xunit;

namespace AgentBridge.Host.Tests;

// #4520: a test that runs the real host must never be able to write the machine's real profile. The
// host takes its profile root from AGENT_BRIDGE_PROFILE_ROOT when it is set (ProfileFolders.DefaultRoot),
// and these tests prove that, and that nothing falls back to the real folder.
public sealed class ProfileRootIsolationTests
{
    static async Task<(int Exit, string Output, string Error)> RunAsync(ProcessStartInfo start)
    {
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        return (child.ExitCode, (await output).Trim(), (await error).Trim());
    }

    static ProcessStartInfo Child(string mode)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(mode);
        return start;
    }

    // The read-only check every write below waits for: this process's profile root is the given one.
    static async Task AssertRootIsHonouredAsync(string root)
    {
        var start = Child("profile-root-child");
        HostProcessIsolation.Isolate(start, root);
        var (exit, output, error) = await RunAsync(start);
        Assert.True(exit == 0, error);
        Assert.Equal(Path.GetFullPath(root), output);
    }

    [Fact]
    public async Task The_profile_root_variable_is_the_root_the_product_uses()
    {
        await AssertRootIsHonouredAsync(HostProcessIsolation.NewRoot("root-honoured-"));
    }

    static readonly string RelativeRoot = Path.Combine("relative", "root");

    // The read-only check for a relative root: the product's code throws instead of using any folder.
    static async Task AssertRelativeRootIsRefusedByTheProductAsync()
    {
        var start = Child("profile-root-child");
        HostProcessIsolation.Isolate(start, RelativeRoot);
        var (exit, output, error) = await RunAsync(start);
        Assert.NotEqual(0, exit);
        Assert.Empty(output);
        Assert.Contains(AgentBridge.Transport.ProfileFolders.RootRefusal, error);
    }

    [Fact]
    public async Task A_relative_root_is_refused_and_the_real_folder_is_not_used_instead()
    {
        await AssertRelativeRootIsRefusedByTheProductAsync();
    }

    // Said once, in one fixed line, before the command reads or writes anything (R8c, #4520). The
    // read-only check above goes first, so a regression is caught before the real host is started.
    [Fact]
    public async Task The_host_command_line_refuses_a_relative_root_with_one_line_and_exit_2()
    {
        await AssertRelativeRootIsRefusedByTheProductAsync();
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "agent-bridge-host.dll"));
        start.ArgumentList.Add("config");
        start.ArgumentList.Add("show");
        HostProcessIsolation.Isolate(start, RelativeRoot);
        var (exit, output, error) = await RunAsync(start);
        Assert.Equal(2, exit);
        Assert.Empty(output);
        Assert.Equal(AgentBridge.Transport.ProfileFolders.RootRefusal, error);
    }

    // No production path moves: with the variable unset, migrate's hook folder is the very folder it
    // used before the override existed (the real local folder's AgentBridge\bin), byte for byte.
    [Fact]
    public async Task With_the_variable_unset_migrate_puts_its_hook_folder_where_it_always_did()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        MigrationOptions? seen = null;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrateCommands.RunAsync(
            ["migration", "status"], AgentBridge.Transport.ProfileFolders.ResolveRoot(null), TextWriter.Null, TextWriter.Null,
            o => { seen = o; throw new InvalidOperationException("captured"); }));
        Assert.Equal("captured", thrown.Message);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge", "bin"), seen!.HookFolder);
    }

    // The guard that refuses config set inside an agent session is what stopped these writes, and a
    // test of it must still be safe the day it fails: so this does what a bypassed guard would, a
    // config set from a process that is not told it is an agent's, and checks the machine's real
    // config.json files are byte for byte what they were. The read-only check comes first: if the
    // isolation did not hold, the test fails there, before it has written anything.
    // When this runs inside an agent's own process tree the host refuses (exit 4) and writes
    // nothing anywhere, which is also fine: the real files are still unchanged.
    [Fact]
    public async Task A_config_set_the_guard_does_not_refuse_writes_only_the_given_root_and_never_the_real_profile()
    {
        var root = HostProcessIsolation.NewRoot("config-bypass-");
        Directory.CreateDirectory(root);
        try
        {
            await AssertRootIsHonouredAsync(root);
            var before = HostProcessIsolation.RealConfigSnapshot();

            var start = new ProcessStartInfo("dotnet");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "agent-bridge-host.dll"));
            foreach (var arg in new[] { "config", "set", "agentMessaging.scope", "off" }) { start.ArgumentList.Add(arg); }
            HostProcessIsolation.Isolate(start, root);
            start.Environment.Remove(AgentBridge.Local.AgentAncestry.SessionVariable);
            var (exit, _, error) = await RunAsync(start);

            Assert.Equal(before, HostProcessIsolation.RealConfigSnapshot());
            var written = Directory.GetFiles(root, "config.json", SearchOption.AllDirectories);
            if (exit == 0) { Assert.Single(written); Assert.StartsWith(root, written[0], StringComparison.OrdinalIgnoreCase); }
            else { Assert.True(exit == 4 && written.Length == 0, $"exit {exit}: {error}"); }
        }
        finally { try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { } }
    }

    // The next test that starts the real host and forgets the variable is caught here, not by a machine
    // that lost its config. Any test file that runs the product's host or CLI must use HostProcessIsolation.
    [Fact]
    public void Every_test_that_runs_the_real_host_as_a_process_isolates_its_profile_root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        var runsHost = new Regex(@"agent-bridge-host\.dll|typeof\((HostSettings|AgentCredentials)\)\.Assembly\.Location", RegexOptions.CultureInvariant);
        var unisolated = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests", "AgentBridge.Host.Tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(file) is nameof(ProfileRootIsolationTests) + ".cs" or "HostProcessIsolation.cs") { continue; }
            var code = File.ReadAllText(file);
            if (code.Contains("ProcessStartInfo", StringComparison.Ordinal) && runsHost.IsMatch(code)
                && !code.Contains("HostProcessIsolation.Isolate(", StringComparison.Ordinal))
            {
                unisolated.Add(Path.GetRelativePath(root, file));
            }
        }
        Assert.Empty(unisolated);
    }
}
