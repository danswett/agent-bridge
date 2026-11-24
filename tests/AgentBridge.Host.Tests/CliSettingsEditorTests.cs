using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using Xunit;

// PR3a (docs/design/beta-messaging.md section 6, owner decision Nov 23 about 23:05 PT "Yes, with backup + notice"): the ONE editor for a CLI's own settings file,
// shared by the hook install and, later, the MCP server registration. These are the rules it carries; StandaloneHooksTests prove the hook install still behaves as before.
public sealed class CliSettingsEditorTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("abcli-").FullName;
    public void Dispose() => Directory.Delete(root, true);
    string P(string name) => Path.Combine(root, name);

    [Fact]
    public void Read_takes_comments_trailing_commas_and_a_byte_order_mark_and_refuses_anything_that_is_not_an_object()
    {
        File.WriteAllBytes(P("a.json"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\n // note\n \"model\": \"m\", \"list\": [1,],\n}")]);
        Assert.Equal("m", (string?)CliSettingsEditor.Read(P("a.json"))["model"]);

        File.WriteAllText(P("b.json"), "[1,2]");
        Assert.Throws<InvalidDataException>(() => CliSettingsEditor.Read(P("b.json")));
    }

    [Fact]
    public void The_first_original_is_backed_up_byte_for_byte_once_and_never_overwritten()
    {
        var live = P("settings.json");
        var backup = P("backups/settings.json");
        File.WriteAllText(live, "{\"a\":1} // mine");
        CliSettingsEditor.BackupOnce(backup, live);
        File.WriteAllText(live, "{\"a\":2}");
        CliSettingsEditor.BackupOnce(backup, live);

        Assert.Equal("{\"a\":1} // mine", File.ReadAllText(backup));
    }

    [Fact]
    public void A_file_that_does_not_exist_yet_has_nothing_to_back_up()
    {
        CliSettingsEditor.BackupOnce(P("backups/x.json"), P("missing.json"));
        Assert.False(File.Exists(P("backups/x.json")));
    }

    [Fact]
    public void The_original_comes_back_byte_for_byte_only_when_what_remains_is_what_it_held()
    {
        var backup = P("orig.json");
        File.WriteAllText(backup, "{ \"a\": 1, // keep me\n}");
        Assert.Equal(File.ReadAllBytes(backup), CliSettingsEditor.Original(backup, new JsonObject { ["a"] = 1 }, TextWriter.Null, "hooks"));
        Assert.Null(CliSettingsEditor.Original(backup, new JsonObject { ["a"] = 1, ["later"] = true }, TextWriter.Null, "hooks"));
        Assert.Null(CliSettingsEditor.Original(P("no-backup.json"), new JsonObject(), TextWriter.Null, "hooks"));
    }

    [Fact]
    public void An_unreadable_backup_warns_and_falls_back_to_removing_only_our_entries()
    {
        File.WriteAllText(P("bad.json"), "{not json");
        var said = new StringWriter();

        Assert.Null(CliSettingsEditor.Original(P("bad.json"), new JsonObject(), said, "servers"));

        Assert.Contains("unreadable", said.ToString());
        Assert.Contains("only the owned servers are removed", said.ToString());
    }

    [Fact]
    public void The_notice_names_the_path_what_is_added_the_backup_and_the_way_back_and_says_would_for_a_dry_run()
    {
        var dry = CliSettingsEditor.Notice(true, "C:/x/settings.json", "this product's own hooks (A, B)", "settings and hooks", "C:/x/backup.json");
        var real = CliSettingsEditor.Notice(false, "C:/x/settings.json", "this product's own hooks (A, B)", "settings and hooks", "C:/x/backup.json");

        Assert.StartsWith("Would change C:/x/settings.json: adds this product's own hooks (A, B)", dry);
        Assert.StartsWith("Changing C:/x/settings.json", real);
        Assert.Contains("and nothing else; your other settings and hooks are not touched", real);
        Assert.Contains("your other settings and servers are not touched", CliSettingsEditor.Notice(false, "p", "a", "settings and servers", "b"));
        Assert.Contains("C:/x/backup.json", real);
        Assert.Contains("Remove restores it", real);
        Assert.Contains("No token is written", real);
    }
}
