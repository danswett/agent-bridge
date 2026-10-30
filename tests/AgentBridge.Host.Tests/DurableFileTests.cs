using AgentBridge.Host.Migration;
using Xunit;

public sealed class DurableFileTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("abdurable-").FullName;

    public void Dispose()
    {
        DurableFile.BeforeCommit.Value = null;
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
