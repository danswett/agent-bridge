using AgentBridge.Host;
using Velopack.Locators;
using Xunit;

public sealed class SlotLocatorTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ab-locator-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("AgentBridge.exe")]
    [InlineData("agent-bridge-host.exe")]
    public void A_running_slot_reports_its_own_version_not_legacy_or_the_latest_pointer(string entry)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
        var slot = Directory.CreateDirectory(Path.Combine(root, "slots", "0.1.200-preview.1-" + new string('a', 64))).FullName;
        File.WriteAllText(Path.Combine(root, "Update.exe"), "fixture");
        File.WriteAllText(Path.Combine(current, "sq.version"),
            "<package><metadata><id>AgentBridgeApp</id><version>0.1.100</version><mainExe>AgentBridge.exe</mainExe><channel>win-arm64</channel></metadata></package>");
        File.WriteAllText(Path.Combine(root, "active-slot.json"), "a newer generation is deliberately unreadable here");
        var process = new Process(Path.Combine(slot, entry));

        var locator = SlotVelopackLocator.Create(process);

        Assert.Equal(root, locator.RootAppDir);
        Assert.Equal(slot, locator.AppContentDir);
        Assert.Equal("0.1.200-preview.1", locator.CurrentlyInstalledVersion!.ToString());
        Assert.Equal("AgentBridgeApp", locator.AppId);
        Assert.Equal("win-arm64", locator.Channel);
        Assert.Equal(Path.Combine(root, "packages"), locator.PackagesDir);
        Assert.Equal(entry, locator.ThisExeRelativePath);
        Assert.Same(process, locator.Process);
        var updates = new VelopackHostUpdates(root, "default", () => throw new InvalidOperationException("must not stop"),
            _ => { }, locator);
        Assert.Contains("native apply is disabled", Assert.Throws<InvalidOperationException>(() =>
            updates.ApplyAndRestart(new("0.1.201", null, null))).Message);
    }

    [Theory]
    [InlineData("bad-slot")]
    [InlineData("0.1.200-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("0.1.200-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_malformed_slot_name_fails_closed_instead_of_becoming_an_uninstalled_native_copy(string name)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var slot = Directory.CreateDirectory(Path.Combine(root, "slots", name)).FullName;
        Directory.CreateDirectory(Path.Combine(root, "current"));
        File.WriteAllText(Path.Combine(root, "Update.exe"), "fixture");
        File.WriteAllText(Path.Combine(root, "current", "sq.version"),
            "<package><metadata><id>AgentBridgeApp</id><version>0.1.100</version><mainExe>AgentBridge.exe</mainExe></metadata></package>");
        Assert.Throws<InvalidDataException>(() => SlotVelopackLocator.Create(new Process(Path.Combine(slot, "AgentBridge.exe"))));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    sealed class Process(string path) : IProcessImpl
    {
        public string GetCurrentProcessPath() => path;
        public uint GetCurrentProcessId() => 1;
        public void StartProcess(string file, IEnumerable<string> args, string workingDirectory, bool silent) =>
            throw new InvalidOperationException("No process execution in locator tests.");
        public void Exit(int code) => throw new InvalidOperationException("No process exit in locator tests.");
    }
}
