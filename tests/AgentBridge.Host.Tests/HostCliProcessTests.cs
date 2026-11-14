using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

[Trait("Category", "Process")]
public sealed class HostCliProcessTests(ITestOutputHelper output)
{
    const string Id = "88888888-8888-4888-8888-888888888888";

    [Fact]
    public async Task Cli_sessions_command_prints_fixture_sessions_and_leaves_roots_unchanged()
    {
        using var fixture = new Fixture();
        using var agent = StartFixtureAgent(fixture);
        try
        {
            Fixture.Append(fixture.CopilotSession(Id, agent.Id, "Fixture CLI session", DateTimeOffset.UtcNow),
                Copilot.User("Fixture request", 1), Copilot.Assistant("Fixture response", 2), Copilot.TurnEnd(3));
            var observation = new SystemProcessInspector().Observe(AgentKind.Copilot);
            var seen = observation.Processes.SingleOrDefault(p => p.Id == agent.Id);
            var lockWrite = File.GetLastWriteTimeUtc(Path.Combine(fixture.CopilotHome, "session-state", Id, $"inuse.{agent.Id}.lock"));
            var generationMatches = seen?.StartedAt is not { } started || lockWrite >= started.UtcDateTime - TimeSpan.FromSeconds(1);
            output.WriteLine($"Parent inspector: fixturePid={agent.Id}; listed={observation.Listed}; known={observation.Known}; matched={seen is not null}; unidentified={observation.Unidentified.Any(p => p.Id == agent.Id)}");
            output.WriteLine($"lockUtc={lockWrite:O}; processStartUtc={agent.StartTime.ToUniversalTime():O}; observedStartUtc={seen?.StartedAt:O}; generationMatches={generationMatches}");
            var result = await RunCli(fixture, "sessions", "--compact");
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Error);
            using var json = JsonDocument.Parse(result.Output);
            var session = Assert.Single(json.RootElement.GetProperty("sessions").EnumerateArray());
            Assert.Equal(Id, session.GetProperty("id").GetString());
            Assert.Equal("Fixture CLI session", session.GetProperty("title").GetString());
        }
        finally
        {
            if (!agent.HasExited) { agent.Kill(); }
            await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Cli_get_without_a_known_session_exits_nonzero_with_the_reason_on_stderr()
    {
        using var fixture = new Fixture();
        var result = await RunCli(fixture, "get", Id);
        Assert.Equal(1, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("missing").GetBoolean());
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_process_tests_start_the_product_host_executable_not_the_rehearsal_parser()
    {
        using var fixture = new Fixture();
        Assert.Equal("agent-bridge-host.dll", Path.GetFileName(typeof(HostSettings).Assembly.Location));
        var result = await RunCli(fixture, "sessions");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Empty(json.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.True(json.RootElement.GetProperty("revision").GetInt64() >= 0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("nonexistent")]
    [InlineData("--copilot-home")]
    [InlineData("--claude-home")]
    [InlineData("--codex-home")]
    [InlineData("--profile")]
    [InlineData("command")]
    public async Task Cli_read_root_refuses_ambiguous_roots_before_reading_sessions(string refusal)
    {
        using var fixture = new Fixture();
        string[] arguments = refusal switch
        {
            "missing" => ["sessions", "--read-root"],
            "relative" => ["sessions", "--read-root", "relative-home"],
            "nonexistent" => ["sessions", "--read-root", Path.Combine(fixture.Root, "absent")],
            "--copilot-home" or "--claude-home" or "--codex-home" or "--profile" =>
                ["sessions", "--read-root", Path.Combine(fixture.Root, "home"), refusal, "conflict"],
            _ => ["usage", "--read-root", Path.Combine(fixture.Root, "home")],
        };
        var result = await RunCli(fixture, arguments, addRoot: false);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("--read-root", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_read_root_uses_no_default_roots_and_without_it_preserves_existing_defaults()
    {
        using var fixture = new Fixture();
        var defaults = fixture.Options();
        Assert.Same(defaults, ReadCommandRoots.Resolve(["sessions"], () => defaults));
        var home = Directory.CreateDirectory(Path.Combine(fixture.Root, "home")).FullName;
        var isolated = ReadCommandRoots.Resolve(["sessions", "--read-root", home],
            () => throw new InvalidOperationException("Isolated reads must not consult current-user roots."));
        Assert.Equal(fixture.CopilotHome, isolated.CopilotHome);
        Assert.Equal(fixture.ClaudeHome, isolated.ClaudeHome);
        Assert.Equal(fixture.CodexHome, isolated.CodexHome);
        Assert.Equal([fixture.ClaudeRegistrations], isolated.ClaudeRegistrationRoots);
        Assert.Equal([fixture.CodexRegistrations], isolated.CodexRegistrationRoots);
        Assert.Equal([Path.Combine(home, ".agent-bridge")], isolated.UsageProbeRoots);
    }

    static Process StartFixtureAgent(Fixture fixture)
    {
        // A renamed native sleeper, not a provider CLI: discovery needs a live matching pid.
        var executable = Path.Combine(fixture.Root, OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
        File.Copy(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : Path.Combine(Path.DirectorySeparatorChar.ToString(), "bin", "sleep"), executable);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in OperatingSystem.IsWindows() ? new[] { "/d", "/q", "/c", "set /p fixture=ready" } : ["120"])
        { start.ArgumentList.Add(argument); }
        return Process.Start(start)!;
    }

    static Task<Result> RunCli(Fixture fixture, string command, params string[] arguments) =>
        RunCli(fixture, [command, .. arguments]);

    static async Task<Result> RunCli(Fixture fixture, string[] arguments, bool addRoot = true)
    {
        var home = Path.Combine(fixture.Root, "home");
        foreach (var directory in new[] { fixture.CopilotHome, fixture.ClaudeHome, fixture.CodexHome,
            fixture.ClaudeRegistrations, fixture.CodexRegistrations, Path.Combine(home, ".agent-bridge"), Path.Combine(fixture.Root, "tmp") })
        { Directory.CreateDirectory(directory); }
        var before = fixture.Listing();
        var assembly = typeof(HostSettings).Assembly.Location;
        var testAssembly = typeof(HostCliProcessTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.Root,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // Execute the product entry point with the already-built test dependency closure.
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), assembly }.Concat(arguments))
        { start.ArgumentList.Add(argument); }
        if (addRoot)
        {
            start.ArgumentList.Add("--read-root");
            start.ArgumentList.Add(home);
        }
        start.Environment["TMP"] = start.Environment["TEMP"] = Path.Combine(fixture.Root, "tmp");
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(before, fixture.Listing());
        return new(child.ExitCode, await output, await error);
    }

    sealed record Result(int ExitCode, string Output, string Error);
}
