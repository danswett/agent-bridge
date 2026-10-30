using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentBridge.Hooks;
using Xunit;
using Xunit.Abstractions;

// The real agent-bridge-host-hook, started as an agent starts it: event on stdin, reply on
// stdout. AGENT_BRIDGE_HOOK points at a published (NativeAOT) one, as CI does; otherwise the
// build's own. Each test runs a copy in a folder of its own, beside a pointer to a runtime
// folder of its own, so nothing reaches any real installation.
public sealed class HookProcessTests(ITestOutputHelper output) : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("abhookp-").FullName;

    string Bin => Path.Combine(_root, "bin");
    string Runtime => Path.Combine(_root, "runtime");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string Install()
    {
        var published = Environment.GetEnvironmentVariable("AGENT_BRIDGE_HOOK");
        var source = published is { Length: > 0 } ? Path.GetDirectoryName(Path.GetFullPath(published))! : AppContext.BaseDirectory;
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Runtime);
        foreach (var file in Directory.GetFiles(source).Where(f => Path.GetFileName(f).StartsWith("agent-bridge-host-hook", StringComparison.Ordinal) || Path.GetFileName(f) == "AgentBridge.Hooks.dll"))
        {
            File.Copy(file, Path.Combine(Bin, Path.GetFileName(file)));
        }
        File.WriteAllBytes(Path.Combine(Bin, HookRunner.PointerFile), HookRunner.Pointer(Runtime));
        return Path.Combine(Bin, OperatingSystem.IsWindows() ? "agent-bridge-host-hook.exe" : "agent-bridge-host-hook");
    }

    static (string Stdout, int Exit, TimeSpan Took) Run(string exe, string stdin, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) { start.ArgumentList.Add(a); }
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (stdout, process.ExitCode, watch.Elapsed);
    }

    [Fact]
    public void The_real_hook_spools_with_this_process_as_its_first_ancestor_while_the_host_takes_events()
    {
        var exe = Install();
        File.WriteAllText(SpoolContract.HeartbeatPath(Runtime), "1");
        var times = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var (stdout, exit, took) = Run(exe, "{\"session_id\":\"s\",\"hook_event_name\":\"UserPromptSubmit\"}", "claude", "register");
            Assert.Equal(("", 0), (stdout, exit));
            times.Add(took.TotalMilliseconds);
        }
        times.Sort();
        output.WriteLine($"median {times[2]:F0} ms from start to exit ({Environment.GetEnvironmentVariable("AGENT_BRIDGE_HOOK") ?? "the build's own"})");

        var files = Directory.GetFiles(SpoolContract.SpoolDirectory(Runtime));
        Assert.Equal(5, files.Length);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(files.Order(StringComparer.Ordinal).First()));
        var ancestors = doc.RootElement.GetProperty("ancestors").EnumerateArray().Select(a => a.GetInt32()).ToList();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) { Assert.Equal(Environment.ProcessId, ancestors.FirstOrDefault()); }
        Assert.Equal("UserPromptSubmit", doc.RootElement.GetProperty("event").GetProperty("hook_event_name").GetString());
        Assert.Equal(5, File.ReadAllLines(SpoolContract.LogPath(Runtime)).Length);
    }

    [Fact]
    public void With_the_host_down_the_real_hook_still_gives_each_agent_its_fixed_reply_and_exits_zero()
    {
        var exe = Install();
        Assert.Equal(("{\"permissionDecision\":\"allow\"}\n", 0), Trim(Run(exe, "{\"toolName\":\"ask_user\"}", "copilot", "ask_user")));
        Assert.Equal(("{}\n", 0), Trim(Run(exe, "{}", "copilot", "agent_stop")));
        Assert.Equal(("", 0), Trim(Run(exe, "not even json", "codex", "hook")));
        Assert.Equal(("{}\n", 0), Trim(Run(exe, "{}", "--fallback", "copilot", "permission")));
        Assert.False(Directory.Exists(SpoolContract.SpoolDirectory(Runtime)));

        // And with nothing beside it at all.
        File.Delete(Path.Combine(Bin, HookRunner.PointerFile));
        Assert.Equal(("{\"permissionDecision\":\"allow\"}\n", 0), Trim(Run(exe, "{}", "copilot", "ask_user")));
        Assert.Equal(0, Run(exe, "", "--version").Exit);

        static (string, int) Trim((string Stdout, int Exit, TimeSpan _) r) => (r.Stdout, r.Exit);
    }
}
