using Xunit;

// #2558: Windows can still hold a just-exited program's image or a scanner's handle when a test's teardown deletes its
// temp folder, so the delete is retried for a while, and a folder that stays held is left behind rather than failing a
// test that has already passed.
public sealed class TempFolderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "temp-folder-" + Guid.NewGuid().ToString("N"));

    public TempFolderTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_file_held_for_a_moment_is_deleted_once_it_is_released()
    {
        var file = Path.Combine(root, "held.exe");
        var held = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var release = new Timer(_ => held.Dispose(), null, TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
        Assert.True(TempFolder.TryDelete(root, TimeSpan.FromSeconds(30)));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_file_that_stays_held_leaves_the_folder_behind_without_throwing()
    {
        using var held = new FileStream(Path.Combine(root, "held.exe"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.False(TempFolder.TryDelete(root, TimeSpan.FromMilliseconds(400)));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void A_folder_that_is_already_gone_counts_as_deleted()
    {
        Directory.Delete(root, true);
        Assert.True(TempFolder.TryDelete(root, TimeSpan.FromSeconds(1)));
    }
}
