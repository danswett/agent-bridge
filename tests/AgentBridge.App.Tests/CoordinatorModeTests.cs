using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class CoordinatorModeTests : IDisposable
{
    const string Supported = "fixture host launcher [coordinator-v1]";
    readonly string home = Path.Combine(Path.GetTempPath(), "ab-mode-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time = new();
    readonly Child worker = new();
    readonly List<string> starts = [];
    readonly CoordinatorHandoff.Copy stage;
    int factories;

    public CoordinatorModeTests()
    {
        var root = Path.Combine(home, "install");
        var packages = Path.Combine(root, "packages");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(current);
        var helper = Path.Combine(current, "agent-bridge-hostw.exe");
        File.WriteAllBytes(helper, HostwBundleTests.Bundle());
        var package = Path.Combine(packages, "older.nupkg");
        File.WriteAllText(package, "verified old target");
        File.WriteAllText(Path.Combine(current, "sq.version"),
            "<package><metadata><id>AgentBridgeApp</id><version>0.1.1</version><mainExe>AgentBridge.exe</mainExe></metadata></package>");
        stage = new CoordinatorHandoff(Path.Combine(home, "handoff"), HostwBundleTests.Target, _ => Supported)
            .Prepare(new(root, packages, package, HostwBundleTests.Digest(File.ReadAllBytes(package)),
                "AgentBridgeApp", "0.1.1", ["--background"], 11, 123), helper);
        stage.Dispose();
    }

    UpdateCoordinatorAdapter Adapter(UpdateCoordinatorRequest request)
    {
        factories++;
        return new(request, _ => null, start =>
        {
            starts.Add(start.FileName);
            return start.FileName.EndsWith("Update.exe", StringComparison.OrdinalIgnoreCase) ? worker : new Child();
        }, time, _ => new MemoryStream());
    }

    Task<int> Run(string[]? args = null, Action<UpdateApplyResult>? report = null) =>
        CoordinatorMode.RunAsync(args ?? ["--coordinator-v1", stage.ManifestPath], stage.ImagePath,
            HostwBundleTests.Target, Adapter, _ => Supported, report);

    [Fact]
    public async Task Valid_mode_uses_real_custody_and_adapter_once_and_the_handoff_is_single_use()
    {
        Assert.Equal(0, await Run());
        Assert.Equal(1, factories);
        Assert.Equal(2, starts.Count);
        var result = JsonSerializer.Deserialize<UpdateApplyResult>(
            File.ReadAllText(Path.Combine(stage.Directory, "coordinator-result.json")))!;
        Assert.Equal(UpdateApplyEnd.Completed, result.End);
        Assert.Equal(4, await Run());
        Assert.Equal(1, factories);
    }

    [Fact]
    public async Task A_timed_out_observer_cannot_exit_the_mode_before_real_apply_ownership_drains()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<UpdateApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Exit = exited.Task;
        var run = Run(report: result => observed.SetResult(result));
        await worker.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            time.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(UpdateApplyEnd.Unknown, (await observed.Task.WaitAsync(TimeSpan.FromSeconds(5))).End);
            Assert.False(run.IsCompleted);
            Assert.False(worker.Disposed);
            Assert.Throws<IOException>(() => File.OpenWrite(stage.ImagePath).Dispose());
            Assert.Single(starts);
            using var snapshot = Observation();
            Assert.Equal("updater", snapshot.RootElement.GetProperty("ProcessRole").GetString());
            Assert.Equal(22, snapshot.RootElement.GetProperty("ApplyProcessId").GetInt32());
            Assert.Equal("00:10:00", snapshot.RootElement.GetProperty("Elapsed").GetString());
        }
        finally { exited.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(worker.Disposed);
        Assert.Equal(4, await run);
    }

    JsonDocument Observation() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(stage.Directory, "coordinator-result.json")));

    [Theory]
    [InlineData(0, UpdateApplyEnd.Completed, 2)]
    [InlineData(7, UpdateApplyEnd.Unknown, 1)]
    public async Task Persistent_observation_names_the_updater_and_only_completed_apply_respawns(
        int exit, UpdateApplyEnd expected, int startCount)
    {
        worker.ExitCode = exit;
        var run = Run();
        await worker.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (exit != 0) time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(expected == UpdateApplyEnd.Completed ? 0 : 4, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(startCount, starts.Count);
        using var snapshot = Observation();
        var result = snapshot.RootElement;
        Assert.Equal((int)expected, result.GetProperty("End").GetInt32());
        Assert.Equal("updater", result.GetProperty("ProcessRole").GetString());
        Assert.Equal(22, result.GetProperty("ApplyProcessId").GetInt32());
        Assert.Equal(Path.Combine(home, "install", "Update.exe"), result.GetProperty("ApplyPath").GetString());
        var diagnostic = result.GetProperty("Diagnostic").GetString()!;
        Assert.Contains("updater-process pid=22", diagnostic);
        Assert.Contains("step=", diagnostic);
        Assert.Contains("elapsed=", diagnostic);
        Assert.DoesNotContain("holder", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_late_owned_updater_wait_error_replaces_the_durable_timeout_without_respawn()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Exit = exited.Task;
        var run = Run(report: _ => observed.TrySetResult());
        await worker.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            time.Advance(TimeSpan.FromMinutes(10));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
        }
        finally { exited.TrySetException(new IOException("private wait detail")); }
        Assert.Equal(4, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        using var snapshot = Observation();
        var result = snapshot.RootElement;
        Assert.Equal("ownership-drain", result.GetProperty("Step").GetString());
        Assert.Equal(nameof(IOException), result.GetProperty("ErrorType").GetString());
        Assert.Equal(22, result.GetProperty("ApplyProcessId").GetInt32());
        Assert.DoesNotContain("private wait detail", result.GetRawText());
        Assert.Single(starts);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task An_oversized_failure_becomes_a_bounded_explicit_observation_failure()
    {
        File.WriteAllText(Path.Combine(home, "install", "current", "sq.version"),
            "<package><metadata><id>AgentBridgeApp</id><version>" + new string('x', 70 * 1024)
            + "</version><mainExe>AgentBridge.exe</mainExe></metadata></package>");
        Assert.Equal(4, await Run());
        using var snapshot = Observation();
        Assert.Equal("observation-write", snapshot.RootElement.GetProperty("Step").GetString());
        Assert.Equal(nameof(InvalidDataException), snapshot.RootElement.GetProperty("ErrorType").GetString());
        Assert.InRange(new FileInfo(Path.Combine(stage.Directory, "coordinator-result.json")).Length, 1, 64 * 1024);
        Assert.Single(starts);
    }

    [Theory]
    [InlineData("--coordinator-v2")]
    [InlineData("--coordinator")]
    [InlineData("--coordinator-v1")]
    public async Task Malformed_coordinator_arguments_are_claimed_and_refused_without_ordinary_host_fallback(string flag)
    {
        Assert.True(CoordinatorMode.Requested([flag]));
        Assert.Equal(4, await Run([flag]));
        Assert.Equal(0, factories);
        Assert.Empty(starts);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("outside-package")]
    [InlineData("changed-image")]
    public async Task A_changed_handoff_cannot_construct_an_adapter(string damage)
    {
        var json = File.ReadAllText(stage.ManifestPath);
        if (damage == "duplicate") File.WriteAllText(stage.ManifestPath, json.Insert(1, "\"Apply\":null,"));
        else if (damage == "outside-package")
        {
            var info = JsonSerializer.Deserialize<UpdateCoordinatorHandoff>(json)!;
            File.WriteAllText(stage.ManifestPath, JsonSerializer.Serialize(info with { Apply = info.Apply with
                { Package = Path.Combine(home, "outside.nupkg") } }));
        }
        else File.AppendAllText(stage.ImagePath, "changed");
        Assert.Equal(4, await Run());
        Assert.Equal(0, factories);
        Assert.Empty(starts);
    }

    public void Dispose() { stage.Dispose(); Directory.Delete(home, recursive: true); }

    sealed class Child : IUpdateCoordinatorProcess
    {
        public int Id => 22;
        public long StartedUtcTicks => 123;
        public int ExitCode { get; set; }
        public Task Exit { get; set; } = Task.CompletedTask;
        public bool Disposed { get; private set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForExitAsync(CancellationToken cancel) { Waiting.TrySetResult(); return Exit.WaitAsync(cancel); }
        public void Dispose() => Disposed = true;
    }
}
