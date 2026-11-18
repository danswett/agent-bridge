using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdateCoordinatorAdapterTests : IDisposable
{
    const string Target = "0.1.200";
    readonly string root = Path.Combine(Path.GetTempPath(), "abcoord-" + Guid.NewGuid().ToString("N"));
    readonly Clock time = new();
    readonly Child original = new(11);
    readonly Child worker = new(22);
    readonly List<ProcessStartInfo> starts = [];
    readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string Package => Path.Combine(root, "handoff", "verified.nupkg");
    string Packages => Path.Combine(root, "packages");
    string Manifest => Path.Combine(root, "current", "sq.version");
    static PackageDigest Digest => new(PackageDigest.Sha256,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified"))));

    public UpdateCoordinatorAdapterTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Package)!);
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        File.WriteAllText(Package, "verified");
        WriteManifest(Target);
    }

    void WriteManifest(string version, string id = "AgentBridgeApp") =>
        File.WriteAllText(Manifest, $"<package xmlns=\"urn:nuspec\"><metadata><id>{id}</id><version>{version}</version><mainExe>AgentBridge.exe</mainExe></metadata></package>");

    UpdateCoordinatorAdapter Adapter(MemoryStream? pin = null) => new(new UpdateCoordinatorRequest(root, Packages, Package, Digest,
        "AgentBridgeApp", Target, ["--background", "--profile", "two words"], 11, 123),
        _ => original, info =>
        {
            starts.Add(info);
            if (info.FileName.EndsWith("Update.exe", StringComparison.OrdinalIgnoreCase))
            {
                started.TrySetResult();
                return worker;
            }
            return new Child(33);
        }, time, _ => pin ?? new MemoryStream());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observation_cancellation_keeps_real_apply_custody_until_exit(bool deadline)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Wait = exited.Task;
        using var cancel = new CancellationTokenSource();
        using var pin = new MemoryStream();
        var adapter = Adapter(pin);
        var run = adapter.RunAsync(cancel.Token);
        await worker.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(worker.WaitCancellation.CanBeCanceled);
            if (deadline)
            {
                time.Advance(TimeSpan.FromMinutes(10));
                Assert.Equal(UpdateApplyEnd.Unknown, (await run.WaitAsync(TimeSpan.FromSeconds(5))).End);
            }
            else
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            Assert.False(worker.Disposed);
            Assert.True(pin.CanRead);
            Assert.Throws<IOException>(() => File.OpenWrite(Package).Dispose());
            Assert.Throws<InvalidOperationException>(() => { _ = adapter.RunAsync(); });
        }
        finally
        {
            exited.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            await adapter.Ownership.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(worker.Disposed);
        Assert.False(pin.CanRead);
    }

    [Fact]
    public async Task Actual_apply_completion_precedes_the_fresh_manifest_and_profile_respawn()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Wait = finished.Task;
        WriteManifest("0.1.199");
        var run = Adapter().RunAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(starts);
        WriteManifest(Target);
        finished.SetResult();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateApplyEnd.Completed, result.End);
        Assert.True(worker.Disposed);
        Assert.Equal(2, starts.Count);
        Assert.Equal(Path.Combine(root, "current", "AgentBridge.exe"), starts[1].FileName);
        Assert.Equal(["--background", "--profile", "two words"], starts[1].ArgumentList);
        Assert.Equal(Path.Combine(root, "current"), starts[1].WorkingDirectory);
        Assert.False(starts[1].UseShellExecute);
        Assert.Equal(["--silent", "apply", "--norestart", "--package", Package,
            "--rootDir", root, "--packageDir", Packages], starts[0].ArgumentList);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Original_process_wait_is_bound_to_its_creation_time(bool sameIdentity)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.Wait = exited.Task;
        original.StartedUtcTicks = sameIdentity ? 123 : 124;
        var run = Adapter().RunAsync();
        if (sameIdentity)
        {
            await original.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(starts);
        }
        else await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        exited.TrySetResult();

        Assert.Equal(UpdateApplyEnd.Completed, (await run.WaitAsync(TimeSpan.FromSeconds(5))).End);
        Assert.Equal(sameIdentity, original.WaitStarted.Task.IsCompleted);
        Assert.True(original.Disposed);
    }

    [Fact]
    public async Task A_changed_package_starts_neither_updater_nor_app()
    {
        File.WriteAllText(Package, "changed");

        var result = await Adapter().RunAsync();

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("package-custody", result.Step);
        Assert.Equal("PackageMismatchException", result.ErrorType);
        Assert.Empty(starts);
    }

    [Fact]
    public async Task A_new_attempt_cannot_report_the_previous_attempts_process_as_current()
    {
        var adapter = Adapter();
        Assert.Equal(UpdateApplyEnd.Completed, (await adapter.RunAsync()).End);
        Assert.Equal(22, adapter.ApplyProcessId);
        File.WriteAllText(Package, "changed");

        var result = await adapter.RunAsync();

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Null(adapter.ApplyProcessId);
        Assert.Equal(2, starts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unproven_updater_contract_cannot_authorize_apply(bool versioned)
    {
        var updater = Path.Combine(root, "Update.exe");
        if (versioned) File.Copy(typeof(UpdateCoordinatorAdapter).Assembly.Location, updater);
        else File.WriteAllText(updater, "unknown");
        var result = await new UpdateCoordinatorAdapter(new(root, Packages, Package, Digest,
            "AgentBridgeApp", Target, [], 11, 123), _ => original,
            _ => throw new InvalidOperationException("must not start")).RunAsync();
        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("apply-start", result.Step);
        Assert.Equal(nameof(InvalidDataException), result.ErrorType);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public async Task Nonzero_exit_does_not_prove_a_delegated_worker_finished(int exit)
    {
        worker.ExitCode = exit;
        var run = Adapter().RunAsync();
        await time.Polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(10));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal(exit, result.ApplyExitCode);
        Assert.Single(starts);
    }

    [Fact]
    public async Task A_busy_real_package_lock_is_not_permission_to_respawn()
    {
        using var holder = new FileStream(Path.Combine(Packages, ".velopack_lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var result = await Adapter().RunAsync();

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("package-lock", result.Step);
        Assert.Single(starts);
    }

    [Fact]
    public async Task Another_package_identity_cannot_authorize_respawn()
    {
        WriteManifest(Target, "OtherPackage");

        var result = await Adapter().RunAsync();

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("active-version", result.Step);
        Assert.Single(starts);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    sealed class Clock() : FakeTimeProvider(Make.T0)
    {
        public TaskCompletionSource Polling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMilliseconds(250)) Polling.TrySetResult();
            return timer;
        }
    }

    sealed class Child(int id) : IUpdateCoordinatorProcess
    {
        public int Id => id;
        public long StartedUtcTicks { get; set; } = 123;
        public int ExitCode { get; set; }
        public Task Wait { get; set; } = Task.CompletedTask;
        public bool Disposed { get; private set; }
        public CancellationToken WaitCancellation { get; private set; }
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForExitAsync(CancellationToken cancel)
        {
            WaitCancellation = cancel;
            WaitStarted.TrySetResult();
            return Wait.WaitAsync(cancel);
        }
        public void Dispose() => Disposed = true;
    }
}
