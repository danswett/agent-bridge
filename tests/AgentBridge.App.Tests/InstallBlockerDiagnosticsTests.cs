using System.Diagnostics;
using AgentBridge.App;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Velopack.Locators;
using Xunit;

public sealed class InstallBlockerDiagnosticsTests : IAsyncLifetime
{
    readonly string root = Path.Combine(Path.GetTempPath(), "holder-fixture-" + Guid.NewGuid().ToString("N"));
    Process? holder;
    nint ownImage;
    string FilePath => Path.Combine(root, "current", "blocked-runtime.dll");

    public async Task InitializeAsync()
    {
        VelopackLocator.Current.RootAppDir = root;
        if (!OperatingSystem.IsWindows()) { return; }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var ownFile = Path.Combine(root, "current", "own-runtime.dll");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll"), ownFile);
        ownImage = System.Runtime.InteropServices.NativeLibrary.Load(ownFile);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll"), FilePath);
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "PATH", "TEMP", "TMP" })
        {
            if (Environment.GetEnvironmentVariable(key) is { } value) { start.Environment[key] = value; }
        }
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "1";
        start.Environment["HELD_FILE_FIXTURE"] = FilePath;
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$h = [Runtime.InteropServices.NativeLibrary]::Load($env:HELD_FILE_FIXTURE); try { [Console]::WriteLine('ready'); [Console]::ReadLine() | Out-Null } finally { [Runtime.InteropServices.NativeLibrary]::Free($h) }" })
        {
            start.ArgumentList.Add(argument);
        }
        holder = Process.Start(start)!;
        holder.PriorityClass = ProcessPriorityClass.BelowNormal;
        Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
    }

    public async Task DisposeAsync()
    {
        if (holder is not null)
        {
            holder.StandardInput.Close();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            holder.Dispose();
        }
        if (ownImage != 0) { System.Runtime.InteropServices.NativeLibrary.Free(ownImage); }
        if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_confirmed_external_holder_names_the_process_and_the_actual_installation_file()
    {
        var blockers = InstallBlockers.Find();
        if (!OperatingSystem.IsWindows()) { Assert.Null(blockers); return; }

        Assert.NotNull(blockers);
        Assert.Contains($"pid {holder!.Id}", blockers, StringComparison.Ordinal);
        Assert.Contains("blocked-runtime.dll", blockers, StringComparison.Ordinal);
        Assert.DoesNotContain("own-runtime.dll", blockers, StringComparison.Ordinal);
        Assert.DoesNotContain(holder.MainModule!.FileName!, blockers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(blockers, new UpdateBlockedException(blockers).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Releasing_the_confirmed_holder_allows_the_real_installation_file_to_be_replaced()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Null(InstallBlockers.Find()); return; }
        Assert.Contains($"pid {holder!.Id}", InstallBlockers.Find()!, StringComparison.Ordinal);
        var blockers = await InstallationFiles.AwaitReleaseAsync(InstallBlockers.Find, TimeProvider.System,
            CancellationToken.None, waiting: _ => holder.StandardInput.Close()).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Null(blockers);
        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        File.WriteAllText(FilePath, "replacement");
        Assert.Equal("replacement", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Reusing_a_pid_in_later_snapshots_does_not_attribute_its_file_to_the_previous_process()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Null(InstallBlockers.Find()); return; }
        var calls = 0;
        var detail = InstallBlockers.Find(default, new FakeTimeProvider(), (_, _) =>
            [new(42, ++calls == 1 ? 1 : 2, "fixture", "", @"C:\external\fixture.exe")]);
        Assert.Equal(InstallationFiles.Unknown, detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bisection_stops_after_its_fake_clock_deadline_or_quit_without_retaining_the_monitor(bool quit)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Null(InstallBlockers.Find()); return; }
        var clock = new FakeTimeProvider();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        List<InstallBlockers.Holder> Query(List<string> files, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
            return [new(42, 1, "fixture", "", @"C:\external\fixture.exe")];
        }
        var work = new InstallationFiles.Budget(clock).ProbeOnceAsync(token =>
        {
            try { return InstallBlockers.Find(token, clock, Query); }
            finally { returned.SetResult(); }
        }, cancel.Token);
        Task? pending = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pending = InstallBlockers.PendingScan;
            if (quit) { cancel.Cancel(); }
            else { clock.Advance(InstallationFiles.ReleaseWait); }
            if (quit) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5))); }
            else { Assert.Contains(await work.WaitAsync(TimeSpan.FromSeconds(5)), new[] { InstallationFiles.Unknown, InstallationFiles.TimedOut }); }
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending!.IsCompleted);
            Assert.Equal(InstallationFiles.Unknown, InstallBlockers.Find(default, clock, Query));
        }
        finally
        {
            release.TrySetResult();
            if (pending is not null) { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        Assert.Equal(2, calls);
        Assert.Contains("holding current", InstallBlockers.Find(default, clock,
            (_, _) => [new(42, 1, "fixture", "", @"C:\external\fixture.exe")])!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unavailable_installation_probe_is_unknown_not_permission_to_replace_files()
    {
        VelopackLocator.Current.RootAppDir = Path.Combine(root, "unavailable");
        Assert.Equal(OperatingSystem.IsWindows() ? InstallationFiles.Unknown : null, InstallBlockers.Find());
    }
}

namespace Velopack.Locators
{
    // Only the locator boundary is substituted; the linked production scanner uses real Windows APIs.
    static class VelopackLocator
    {
        public static bool IsCurrentSet => true;
        public static FixtureLocator Current { get; } = new();
        public sealed class FixtureLocator { public string RootAppDir { get; set; } = ""; }
    }
}

namespace AgentBridge.App
{
    static class AppLog { public static void Write(string message) => Trace.WriteLine(message); }
}
