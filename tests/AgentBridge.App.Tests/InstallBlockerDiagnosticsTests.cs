using System.Diagnostics;
using AgentBridge.App;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Velopack.Locators;
using Xunit;

public sealed class InstallBlockerDiagnosticsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "holder-fixture-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, InstallBlockers.Holder> holders = new();
    readonly List<FileStream> locks = [];
    bool unreadable;
    bool reusedPid;
    int queries;
    // Never this process's own id, which a fixed fake id could be on Linux runners.
    static readonly int External = Environment.ProcessId + 1;
    string? vanishing;
    bool vanishThrows;
    int calls;

    public InstallBlockerDiagnosticsTests()
    {
        VelopackLocator.Current.RootAppDir = root;
        Directory.CreateDirectory(Path.Combine(root, "current"));
        var ownFile = Path.Combine(root, "current", "own-runtime.dll");
        Hold("own-runtime.dll", new(Environment.ProcessId, 1, "own app", "", ownFile));
    }

    string Hold(string name, InstallBlockers.Holder? holder = null)
    {
        var file = Path.Combine(root, "current", name);
        File.WriteAllText(file, "old install");
        locks.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
        holders[file] = holder ?? new(External, 1, "fixture holder", "", Path.Combine(Path.GetTempPath(), "external", "fixture.exe"));
        return file;
    }

    List<InstallBlockers.Holder> Query(List<string> files)
    {
        if (unreadable) { throw new UnauthorizedAccessException("private-probe-path"); }
        if (calls++ > 0 && vanishing is { } gone && files.Contains(gone))
        {
            if (!vanishThrows) { files = files.Where(f => f != gone).ToList(); }
            else if (files.Count == 1) { throw new UnauthorizedAccessException("private-probe-path"); }
        }
        var generation = reusedPid && ++queries > 1 ? 2 : 1;
        return files.Where(holders.ContainsKey).Select(file => holders[file] with { Started = generation }).Distinct().ToList();
    }

    string? Probe() => InstallBlockers.Find(root,
        () => Directory.EnumerateFiles(Path.Combine(root, "current"), "*", SearchOption.AllDirectories).ToList(), Query);

    [Fact]
    public void The_in_place_install_is_given_every_file_held_from_outside_and_none_of_the_apps_own()
    {
        var dac = Hold("mscordaccore.dll", new(External, 1, "Connected User Experiences and Telemetry", "DiagTrack", null));
        var pri = Hold("resources.pri", new(External, 1, "unidentified", "", null));
        File.WriteAllText(Path.Combine(root, "current", "free.dll"), "free");
        Assert.Equal(new[] { dac, pri }, InstallBlockers.HeldFiles(root, Query).Order());
        unreadable = true;
        Assert.Throws<UnauthorizedAccessException>(() => InstallBlockers.HeldFiles(root, Query));
    }

    public void Dispose()
    {
        foreach (var handle in locks) { handle.Dispose(); }
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("blocked-runtime.dll")]
    [InlineData("resources.pri")]
    public void A_persistent_holder_names_the_process_and_confirmed_installation_file_not_just_executable_images(string name)
    {
        Hold(name);
        var blockers = Probe();
        Assert.NotNull(blockers);
        Assert.Contains($"fixture holder (pid {External})", blockers, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("current", name), blockers, StringComparison.Ordinal);
        Assert.DoesNotContain("own-runtime.dll", blockers, StringComparison.Ordinal);
        Assert.DoesNotContain(root, blockers, StringComparison.Ordinal);
        Assert.Contains(blockers, new UpdateBlockedException(blockers).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Releasing_the_confirmed_holder_first_allows_the_fake_installation_file_to_be_replaced()
    {
        var file = Hold("blocked-runtime.dll");
        Assert.Contains("blocked-runtime.dll", Probe()!, StringComparison.Ordinal);
        holders.Clear();
        foreach (var handle in locks) { handle.Dispose(); }
        var blockers = await InstallationFiles.AwaitReleaseAsync(Probe, new FakeTimeProvider(), default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(blockers);
        File.WriteAllText(file, "replacement");
        Assert.Equal("replacement", File.ReadAllText(file));
    }

    [Fact]
    public void An_unreadable_probe_is_unknown_not_permission_to_replace_files()
    {
        var file = Hold("blocked-runtime.dll");
        unreadable = true;
        Assert.Equal(InstallationFiles.Unknown, Probe());
        Assert.Equal("old install", File.ReadAllText(file));
    }

    [Fact]
    public void Reusing_a_pid_during_bisection_does_not_guess_the_previous_processes_file()
    {
        Hold("blocked-runtime.dll");
        reusedPid = true;
        Assert.Equal(InstallationFiles.Unknown, Probe());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_holder_that_cannot_be_confirmed_keeps_the_confirmed_holders_name_and_file_and_stays_blocked(bool verifyThrows, bool reverseHolders)
    {
        var confirmed = Hold("blocked-runtime.dll");
        vanishing = Hold("second.dll", new(External + 1, 1, "second holder", "", Path.Combine(Path.GetTempPath(), "external", "second.exe")));
        vanishThrows = verifyThrows;
        // An unreadable first partition would prevent either holder from being confirmed.
        var blockers = InstallBlockers.Find(root, () => [confirmed, vanishing], files =>
        {
            var result = Query(files);
            if (reverseHolders) { result.Reverse(); }
            return result;
        });
        Assert.NotNull(blockers);
        Assert.Contains($"fixture holder (pid {External}) holding {Path.Combine("current", "blocked-runtime.dll")}", blockers, StringComparison.Ordinal);
        Assert.Contains(InstallationFiles.Unknown, blockers, StringComparison.Ordinal);
        Assert.DoesNotContain("second.dll", blockers, StringComparison.Ordinal);
        Assert.DoesNotContain(root, blockers, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_first_partition_does_not_name_a_holder_that_was_never_confirmed()
    {
        var confirmed = Hold("blocked-runtime.dll");
        vanishing = Hold("second.dll", new(External + 1, 1, "second holder", "", Path.Combine(Path.GetTempPath(), "external", "second.exe")));
        vanishThrows = true;
        var blockers = InstallBlockers.Find(root, () => [vanishing, confirmed], Query);
        Assert.Equal(InstallationFiles.Unknown, blockers);
    }
}

namespace Velopack.Locators
{
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
