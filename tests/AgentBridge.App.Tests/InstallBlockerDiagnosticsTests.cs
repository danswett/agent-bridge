using System.Diagnostics;
using AgentBridge.App;
using AgentBridge.App.Core;
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
