using System.Diagnostics;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

// Resolving a folder never waits for ever: a link to an offline network share can stall the file system call, and a launch or an approval must
// not hang on it. A stalled resolve answers "timed out" (the launch path reads that as not offered, an approval as not now) and is not asked again
// for a while; with localOnly a link whose target is not on a local fixed drive is refused before the target is touched.
public sealed class FoldersBoundedTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abfb-" + Guid.NewGuid().ToString("N")[..8]);

    public FoldersBoundedTests()
    {
        Directory.CreateDirectory(root);
        Folders.ForgetStalled();
    }

    public void Dispose()
    {
        Folders.ForgetStalled();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void A_folder_resolves_as_before_and_a_missing_one_says_so()
    {
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;

        var found = Folders.Resolve(real);
        var missing = Folders.Resolve(Path.Combine(root, "nothing"));

        Assert.Equal((Folders.Physical(real), FolderProblem.None), (found.Path, found.Problem));
        Assert.Equal((null, FolderProblem.Missing), (missing.Path, missing.Problem));
        Assert.Null(Folders.Physical(Path.Combine(root, "nothing")));
    }

    [Fact]
    public void A_resolve_that_does_not_come_back_is_given_up_on_and_not_waited_for_again_for_a_while()
    {
        using var stuck = new ManualResetEventSlim();
        var runs = 0;
        string? Work() { Interlocked.Increment(ref runs); stuck.Wait(); return "never"; }

        var clock = Stopwatch.StartNew();
        var first = Folders.Bounded("offline-share", Work, TimeSpan.FromMilliseconds(200), out var firstResult);
        var firstTook = clock.Elapsed;
        clock.Restart();
        var second = Folders.Bounded("offline-share", Work, TimeSpan.FromSeconds(30), out var secondResult);
        var secondTook = clock.Elapsed;
        stuck.Set();

        Assert.False(first);
        Assert.False(second);
        Assert.Null(firstResult);
        Assert.Null(secondResult);
        Assert.InRange(firstTook, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
        Assert.True(secondTook < TimeSpan.FromSeconds(1), "the second ask must not wait again");
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_stalled_folder_and_anything_below_it_is_answered_timed_out_at_once_by_Resolve_and_Physical_is_null()
    {
        var link = Path.GetFullPath(Path.Combine(root, "share-link"));
        using var stuck = new ManualResetEventSlim();
        Folders.Bounded(link, () => { stuck.Wait(); return "never"; }, TimeSpan.FromMilliseconds(100), out string? _);

        var clock = Stopwatch.StartNew();
        var below = Folders.Resolve(Path.Combine(link, "sub", "deeper"));
        var self = Folders.Resolve(link);
        var physical = Folders.Physical(link);
        stuck.Set();

        Assert.Equal((null, FolderProblem.TimedOut), (below.Path, below.Problem));
        Assert.Equal(FolderProblem.TimedOut, self.Problem);
        Assert.Null(physical);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), "none of these may wait again");
    }

    [Fact]
    public void A_folder_that_was_only_slow_is_asked_again_as_soon_as_the_slow_call_finishes()
    {
        using var release = new ManualResetEventSlim();
        var runs = 0;
        string? Slow() { Interlocked.Increment(ref runs); release.Wait(); return "done"; }
        Assert.False(Folders.Bounded("slow-disk", Slow, TimeSpan.FromMilliseconds(100), out string? _));

        release.Set();
        var asked = SpinWait.SpinUntil(() => Folders.Bounded("slow-disk", Slow, TimeSpan.FromSeconds(2), out string? _), TimeSpan.FromSeconds(5));

        Assert.True(asked, "once the slow call has finished the folder is tried again, not held as stalled for the rest of the window");
        Assert.True(runs >= 2);
    }

    [Fact]
    public void Work_that_finishes_in_time_is_answered_and_another_folder_is_not_held_back_by_a_stalled_one()
    {
        using var stuck = new ManualResetEventSlim();
        Folders.Bounded("offline-share", () => { stuck.Wait(); return "never"; }, TimeSpan.FromMilliseconds(100), out string? _);

        var ok = Folders.Bounded("local-folder", () => "fine", TimeSpan.FromSeconds(5), out var result);
        stuck.Set();

        Assert.True(ok);
        Assert.Equal("fine", result);
    }

    [Fact]
    public void A_link_to_a_network_share_is_refused_as_not_local_without_touching_the_share()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var link = Path.Combine(root, "link");
        try { Directory.CreateSymbolicLink(link, @"\\offline-server.invalid\share\work"); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; } // no right to make links here

        var clock = Stopwatch.StartNew();
        var resolved = Folders.Resolve(link, localOnly: true);

        Assert.Equal((null, FolderProblem.NotLocal), (resolved.Path, resolved.Problem));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "a share must not be asked");
    }

    [Fact]
    public void Only_a_local_fixed_drive_counts_as_local()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var unused = Enumerable.Range('D', 23).Select(i => (char)i).First(c => !DriveInfo.GetDrives().Any(d => d.Name[0] == c));

        Assert.True(Folders.IsLocalFixed(root));
        Assert.False(Folders.IsLocalFixed(@"\\server\share"));
        Assert.False(Folders.IsLocalFixed(unused + @":\none"));
    }
}
