using Xunit;

public sealed class LaunchFlowCleanupTests
{
    [Fact]
    public void Cleanup_accepts_a_git_lock_released_after_enumeration_and_still_normalizes_remaining_files()
    {
        using var f = new Fixture();
        var locked = Path.Combine(f.Root, "locked");
        var retained = Path.Combine(f.Root, "readonly");
        File.WriteAllText(locked, "launch reservation");
        File.WriteAllText(retained, "fixture");
        File.SetAttributes(retained, FileAttributes.ReadOnly);

        IEnumerable<string> DuringUnlock()
        {
            foreach (var file in Directory.GetFiles(f.Root))
            {
                if (file == locked) { File.Delete(locked); }
                yield return file;
            }
        }

        try
        {
            LaunchFlowFixture.NormalizeFiles(DuringUnlock());

            Assert.False(File.Exists(locked));
            Assert.Equal(FileAttributes.Normal, File.GetAttributes(retained));
        }
        finally { File.SetAttributes(retained, FileAttributes.Normal); }
    }
}
