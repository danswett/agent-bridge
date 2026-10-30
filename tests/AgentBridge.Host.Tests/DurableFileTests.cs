using AgentBridge.Host.Migration;
using Xunit;

public sealed class DurableFileTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("abdurable-").FullName;

    public void Dispose()
    {
        DurableFile.BeforeCommit.Value = null;
        DurableFile.AfterReplace.Value = null;
        DurableFile.AfterCheck.Value = null;
        _held?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    string File_(string text)
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, text);
        return path;
    }

    static string Hash(string text) => MigrationRecord.Hash(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void A_save_landing_after_the_check_is_kept_and_the_write_refused()
    {
        var path = File_("planned");
        DurableFile.BeforeCommit.Value = p => File.WriteAllText(p, "theirs");

        Assert.Throws<DurableFile.ChangedMeanwhileException>(() => DurableFile.Replace(path, "ours"u8.ToArray(), Hash("planned")));

        Assert.Equal("theirs", File.ReadAllText(path));
        Assert.Equal(["settings.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    FileStream? _held;

    [Fact]
    public void A_save_landing_after_the_check_that_cannot_be_put_back_is_kept_beside_the_file()
    {
        // Only Windows lets a reader refuse a rename over the file it holds.
        if (!OperatingSystem.IsWindows()) { return; }
        var path = File_("planned");
        DurableFile.BeforeCommit.Value = p => File.WriteAllText(p, "theirs");
        DurableFile.AfterReplace.Value = p => _held = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);

        var e = Assert.Throws<IOException>(() => DurableFile.Replace(path, "ours"u8.ToArray(), Hash("planned")));

        var kept = Assert.Single(Directory.GetFiles(_root, "*.old"));
        Assert.Contains(kept, e.Message);
        Assert.Equal("theirs", File.ReadAllText(kept));
    }

    [Fact]
    public void A_second_save_landing_while_the_first_is_put_back_is_kept_too()
    {
        var path = File_("planned");
        DurableFile.BeforeCommit.Value = p => File.WriteAllText(p, "first");
        DurableFile.AfterReplace.Value = p => File.WriteAllText(p, "second");

        var e = Assert.Throws<IOException>(() => DurableFile.Replace(path, "ours"u8.ToArray(), Hash("planned")));

        Assert.Equal("first", File.ReadAllText(path));
        var kept = Assert.Single(Directory.GetFiles(_root, "*.newer"));
        Assert.Equal("second", File.ReadAllText(kept));
        Assert.Contains(kept, e.Message);
    }

    [Fact]
    public void A_file_created_after_the_check_is_kept_and_the_write_refused()
    {
        var path = Path.Combine(_root, "settings.json");
        DurableFile.BeforeCommit.Value = p => File.WriteAllText(p, "theirs");

        Assert.Throws<DurableFile.ChangedMeanwhileException>(() => DurableFile.Replace(path, "ours"u8.ToArray(), expected: null));

        Assert.Equal("theirs", File.ReadAllText(path));
        Assert.Equal(["settings.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_deleted_after_the_check_stays_deleted_and_the_write_is_refused()
    {
        var path = File_("planned");
        DurableFile.AfterCheck.Value = File.Delete;
        Assert.Throws<DurableFile.ChangedMeanwhileException>(() => DurableFile.Replace(path, "ours"u8.ToArray(), Hash("planned")));
        Assert.Empty(Directory.GetFiles(_root));

        // Or later, as it is about to be replaced.
        path = File_("planned");
        DurableFile.AfterCheck.Value = null;
        DurableFile.BeforeCommit.Value = File.Delete;
        Assert.Throws<DurableFile.ChangedMeanwhileException>(() => DurableFile.Replace(path, "ours"u8.ToArray(), Hash("planned")));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void A_save_landing_after_the_check_is_kept_rather_than_removed()
    {
        var path = File_("planned");
        DurableFile.BeforeCommit.Value = p => File.WriteAllText(p, "theirs");

        Assert.Throws<DurableFile.ChangedMeanwhileException>(() => DurableFile.Remove(path, Hash("planned")));

        Assert.Equal("theirs", File.ReadAllText(path));
        Assert.Equal(["settings.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_still_as_planned_is_replaced_or_removed_leaving_nothing_beside_it()
    {
        var path = File_("planned");
        DurableFile.Replace(path, "ours"u8.ToArray(), Hash("planned"));
        Assert.Equal("ours", File.ReadAllText(path));
        DurableFile.Remove(path, Hash("ours"));
        Assert.Empty(Directory.GetFiles(_root));
    }
}
