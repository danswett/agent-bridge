using System.Diagnostics;
using AgentBridge.HostLauncher;
using Xunit;

// #4520: AGENT_BRIDGE_PROFILE_ROOT is for tests and diagnostics and must be an absolute path. The
// launcher shares the host's rule (ProfileFolders, compiled in), so it refuses a relative one the
// same way: the same line, exit 2, no host started, and nothing written, since there is no folder
// to log in and the product's own must not be used instead.
[Trait("Category", "Process")]
public sealed class HostwProfileRootTests
{
    [Fact]
    public async Task The_launcher_refuses_a_relative_profile_root_with_one_line_and_exit_2()
    {
        var testAssembly = typeof(HostwProfileRootTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // The launcher's own entry point, run with the already-built test dependency closure.
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), typeof(HostJob).Assembly.Location, "serve" })
        { start.ArgumentList.Add(argument); }
        start.Environment["AGENT_BRIDGE_PROFILE_ROOT"] = Path.Combine("relative", "root");
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        Assert.Equal(2, child.ExitCode);
        Assert.Empty((await output).Trim());
        Assert.Equal("AGENT_BRIDGE_PROFILE_ROOT must be an absolute path; nothing was read or written", (await error).Trim());
    }
}
