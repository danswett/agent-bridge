using Xunit;

// The launch fixtures' teardown clears every file's read-only mark before deleting the temp folder. Git can still be
// at work in a fixture's worktree then, and deletes its own lock file (`.git/worktrees/<name>/locked`) from under the sweep.
public sealed class LaunchFixtureTeardownTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "launch-teardown-" + Guid.NewGuid().ToString("N"));

    public LaunchFixtureTeardownTests() => Directory.CreateDirectory(Path.Combine(root, "wt", "admin"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void A_file_that_vanishes_between_the_listing_and_the_clearing_does_not_fail_the_teardown()
    {
        var locked = Path.Combine(root, "wt", "admin", "locked");
        var kept = Path.Combine(root, "kept.txt");
        File.WriteAllText(locked, "git"); File.WriteAllText(kept, "x");
        File.SetAttributes(kept, FileAttributes.ReadOnly);

        LaunchFlowFixture.ClearReadOnly(root, listed => { if (listed == locked) { File.Delete(locked); } });

        Assert.Equal(FileAttributes.Normal, File.GetAttributes(kept)); // the rest is still cleared
    }

    [Fact]
    public void A_folder_that_vanishes_during_the_sweep_does_not_fail_the_teardown()
    {
        var admin = Path.Combine(root, "wt", "admin");
        File.WriteAllText(Path.Combine(admin, "locked"), "git");

        LaunchFlowFixture.ClearReadOnly(root, listed => { if (listed.StartsWith(admin, StringComparison.Ordinal)) { Directory.Delete(admin, true); } });
    }
}
