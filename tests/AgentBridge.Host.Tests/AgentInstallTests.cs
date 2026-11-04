using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using Xunit;

public sealed class AgentInstallTests : IDisposable
{
    readonly Fixture fixture = new();
    readonly Locator locator = new();
    readonly Commands commands = new();
    int refreshes;

    AgentInstallService Service(Func<string?>? winget = null, Action<string>? preflight = null, CancellationToken stopping = default) =>
        new(fixture.Root, locator, () => refreshes++, commands, winget ?? (() => "fixture-winget.exe"), preflight ?? (_ => { }), stopping);

    public void Dispose() => fixture.Dispose();

    [Theory]
    [InlineData("copilot", "GitHub.Copilot")]
    [InlineData("claude", "Anthropic.ClaudeCode")]
    [InlineData("codex", "OpenAI.Codex")]
    public async Task Consent_installs_only_the_selected_package_and_rechecks_the_executable(string agent, string package)
    {
        commands.Run = (_, _, _) =>
        {
            locator.Executable = "fixture-installed.exe";
            return Task.FromResult(new CommandResult(true, false, 0, "private installer output"));
        };
        var result = await Service().InstallAsync(new(agent, true));

        Assert.True(result.Completed, result.Detail);
        Assert.Equal("fixture-installed.exe", result.Executable);
        Assert.Equal("fixture-winget.exe", commands.Program);
        Assert.Equal(new[] { "install", "--id", package, "--exact", "--source", "winget", "--dependency-source", "winget",
            "--scope", "user", "--silent", "--disable-interactivity", "--no-upgrade",
            "--accept-package-agreements", "--accept-source-agreements" }, commands.Arguments);
        Assert.Equal(2, refreshes);
        Assert.DoesNotContain("private", result.Detail);
        Assert.Contains("separate checks", result.Detail);
    }

    [Theory]
    [InlineData("agency", true)]
    [InlineData("arbitrary.package", true)]
    [InlineData("claude", false)]
    public async Task An_unsupported_agent_or_missing_consent_cannot_start_an_installer(string agent, bool consent)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().InstallAsync(new(agent, consent)));
        Assert.Equal(0, commands.Calls);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public async Task Existing_executables_are_reused_without_even_looking_for_a_package_manager()
    {
        locator.Executable = "fixture-existing.exe";
        var result = await Service(() => throw new InvalidOperationException("must not resolve WinGet")).InstallAsync(new("claude", true));
        Assert.True(result.Completed);
        Assert.Equal(locator.Executable, result.Executable);
        Assert.Equal(0, commands.Calls);
    }

    [Fact]
    public async Task A_missing_configured_path_cannot_be_replaced_by_a_different_installation()
    {
        File.WriteAllText(Path.Combine(fixture.Root, HostSettings.FileName),
            new JsonObject { ["launch"] = new JsonObject { ["agents"] = new JsonObject { ["claude"] = new JsonObject { ["path"] = Path.Combine(fixture.Root, "missing.exe") } } } }.ToJsonString());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().InstallAsync(new("claude", true)));
        Assert.Contains("configured executable", error.Message);
        Assert.Equal(0, commands.Calls);
    }

    [Fact]
    public async Task Missing_WinGet_is_actionable_without_bootstrapping_another_package_manager()
    {
        var result = await Service(() => null).InstallAsync(new("claude", true));
        Assert.False(result.Completed);
        Assert.Contains("App Installer", result.Detail);
        Assert.Equal(0, commands.Calls);
    }

    [Theory]
    [InlineData(false, false, -1, "could not start")]
    [InlineData(true, true, -1, "timed out")]
    [InlineData(true, false, -1978335189, "code 0x")]
    [InlineData(true, false, 0, "not found")]
    public async Task Installer_failure_or_missing_launch_evidence_is_never_success(bool ran, bool timedOut, int exit, string detail)
    {
        commands.Run = (_, _, _) => Task.FromResult(new CommandResult(ran, timedOut, exit, "private output"));
        var result = await Service().InstallAsync(new("claude", true));
        Assert.False(result.Completed);
        Assert.Null(result.Executable);
        Assert.Contains(detail, result.Detail);
        Assert.DoesNotContain("private", result.Detail);
    }

    [Fact]
    public async Task Cancellation_keeps_the_operation_serialized_until_the_installer_has_stopped()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Run = async (_, _, ct) =>
        {
            entered.SetResult();
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await stopped.Task;
            ct.ThrowIfCancellationRequested();
            return new(true, false, 0, "");
        };
        var service = Service();
        Assert.False(service.Cancel());
        var pending = service.InstallAsync(new("claude", true));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.Cancel());
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(new("copilot", true)));
            Assert.False(pending.IsCompleted);
        }
        finally { stopped.TrySetResult(); }
        var result = await pending;
        Assert.False(result.Completed);
        Assert.Contains("cancelled", result.Detail);
        Assert.Contains("Nothing was uninstalled", result.Detail);
        Assert.False(service.Cancel());
    }

    [Fact]
    public async Task Disposal_closes_admission_and_waits_for_confirmed_installer_cleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Run = async (_, _, ct) =>
        {
            entered.SetResult();
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await stopped.Task;
            ct.ThrowIfCancellationRequested();
            return new(true, false, 0, "");
        };
        await using var service = Service();
        var states = new List<bool>();
        service.Changed += () => states.Add(service.Snapshot!.InProgress);
        var pending = service.InstallAsync(new("claude", true));
        Task closing = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            closing = service.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(closing.IsCompleted);
            Assert.True(service.Snapshot!.InProgress);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(new("copilot", true)));
        }
        finally { stopped.TrySetResult(); }
        Assert.False((await pending).Completed);
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { true, false }, states);
        Assert.False(service.Snapshot!.InProgress);
        Assert.Contains("cancelled", service.Snapshot.Detail);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(new("codex", true)));
    }

    [Fact]
    public async Task Unconfirmed_cleanup_is_reported_and_blocks_another_installer()
    {
        commands.Run = (_, _, _) => throw new CommandCleanupException(new IOException("fixture cleanup failure"));
        await using var service = Service();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(new("claude", true)));
        Assert.Contains("cleanup could not be confirmed", error.Message);
        Assert.True(service.Snapshot!.Blocked);
        Assert.False(service.Snapshot.InProgress);
        Assert.Equal(error.Message, service.Snapshot.Detail);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(new("copilot", true)));
        Assert.Equal(1, commands.Calls);
    }

    [Fact]
    public async Task Read_only_or_ownership_refusal_is_rechecked_before_starting_the_installer()
    {
        var checks = 0;
        var service = Service(preflight: _ =>
        {
            if (++checks == 2) { throw new InvalidOperationException("fixture ownership changed"); }
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(new("claude", true)));
        Assert.Equal("fixture ownership changed", error.Message);
        Assert.Equal(0, commands.Calls);
    }

    [Fact]
    public async Task Host_shutdown_cancels_installation_without_a_success_shaped_result()
    {
        using var stop = new CancellationTokenSource();
        commands.Run = (_, _, _) =>
        {
            stop.Cancel();
            return Task.FromResult(new CommandResult(true, false, 0, ""));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(stopping: stop.Token).InstallAsync(new("claude", true)));
    }

    sealed class Locator : IAgentLocator
    {
        public string? Executable;
        public string? Find(Launcher launcher, AgentConfig config) => Executable;
    }

    sealed class Commands : ICommandRunner
    {
        public int Calls;
        public string? Program;
        public IReadOnlyList<string> Arguments = [];
        public Func<string, IReadOnlyList<string>, CancellationToken, Task<CommandResult>> Run =
            (_, _, _) => Task.FromResult(new CommandResult(true, false, 0, ""));

        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            Program = program;
            Arguments = arguments;
            return Run(program, arguments, cancellationToken);
        }
    }
}
