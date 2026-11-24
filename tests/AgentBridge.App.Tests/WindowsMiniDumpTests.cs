using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

// The hang dump is MiniDumpNormal: thread stacks and modules, no heap. It is written for real here, from the test process itself.
// MiniDumpWriteDump on its own process suspends its other threads, which Microsoft warns can deadlock, and this assembly's test host
// hung on CI three times with this class in flight in one: so it runs ALONE, in a collection with no parallelism (xunit runs it after
// the parallel ones have finished), and keeps its coverage. QuarantineTests lists it. That removes one suspected cause of the hang.
[Collection(nameof(WindowsMiniDumpTests))]
public sealed class WindowsMiniDumpTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abdump-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_dump_is_a_real_minidump_named_for_its_time_and_process_and_far_smaller_than_a_full_one()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var path = new WindowsMiniDump(folder, time).Write();

        Assert.NotNull(path);
        Assert.Equal(folder, Path.GetDirectoryName(path));
        Assert.Matches(@"^AgentBridge-hang-20261123-120000-\d+\.dmp$", Path.GetFileName(path));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("MDMP", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        // MiniDumpNormal holds stacks only: a full dump of a test host is hundreds of megabytes.
        Assert.InRange(bytes.Length, 1024, 64 * 1024 * 1024);
    }

    [Fact]
    public void Only_the_newest_few_dumps_are_kept()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Directory.CreateDirectory(folder);
        for (var day = 1; day <= 7; day++) { File.WriteAllText(Path.Combine(folder, $"AgentBridge-hang-2026110{day}-000000-1.dmp"), "old"); }
        File.WriteAllText(Path.Combine(folder, "someone-elses.dmp"), "not ours");

        var path = new WindowsMiniDump(folder, time, keep: 3).Write();

        var kept = Directory.EnumerateFiles(folder, "AgentBridge-hang-*.dmp").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(3, kept.Length);
        Assert.Contains(Path.GetFileName(path), kept);
        Assert.Equal(new string?[] { "AgentBridge-hang-20261106-000000-1.dmp", "AgentBridge-hang-20261107-000000-1.dmp", Path.GetFileName(path) }, kept);
        Assert.True(File.Exists(Path.Combine(folder, "someone-elses.dmp")));
    }

    // A dump is a few megabytes, but a disk with no room left is the machine's emergency, not a thing to add to: below 1 GB free, no dump.
    [Theory]
    [InlineData(0L, false)]
    [InlineData((1L << 30) - 1, false)]
    [InlineData(1L << 30, true)]
    [InlineData(50L << 30, true)]
    public void With_less_than_a_gigabyte_of_free_disk_no_dump_is_written_and_the_reason_is_said(long freeBytes, bool writes)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var dumper = new WindowsMiniDump(folder, time, freeBytes: () => freeBytes);

        if (writes)
        {
            Assert.NotNull(dumper.Write());
            return;
        }

        var skipped = Assert.Throws<HangDumpSkipped>(() => dumper.Write());
        Assert.Contains("1 GB", skipped.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any());
    }

    [Fact]
    public void A_dump_that_cannot_be_created_is_an_exception_for_the_watchdog_to_say_not_a_crash_here()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Directory.CreateDirectory(folder);
        var blocked = Path.Combine(folder, "AgentBridge-hang-20261123-120000-" + Environment.ProcessId + ".dmp");
        File.WriteAllText(blocked, "already here");

        Assert.ThrowsAny<IOException>(() => new WindowsMiniDump(folder, time).Write());
    }
}

// The resume notice that tells the hang watchdog a ping waited through a sleep.
public sealed class ResumeNotifierTests
{
    [Fact]
    public void Windows_accepts_the_registration_and_dispose_is_safe_twice()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        using var notifier = new ResumeNotifier(() => { });

        Assert.True(notifier.Registered);
        notifier.Dispose();
        notifier.Dispose();
        Assert.False(notifier.Registered);
    }
}

[CollectionDefinition(nameof(WindowsMiniDumpTests), DisableParallelization = true)]
public sealed class WindowsMiniDumpCollection;
