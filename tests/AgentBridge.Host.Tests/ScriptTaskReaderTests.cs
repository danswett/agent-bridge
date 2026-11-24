using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using AgentBridge.Host;
using AgentBridge.Host.Migration;
using Xunit;

// The real reads behind a script credential bound to a scheduled task (docs/design/script-credentials.md section 6 rule 3,
// docs/protocol/local-pipe.md): the logic is tested here with synthetic security descriptors and task XML, and the Windows parts on real files.
[SupportedOSPlatform("windows")]
public class ScriptTaskReaderTests
{
    static SecurityIdentifier User => new("S-1-5-21-1-2-3-1000");
    static SecurityIdentifier Users => new("S-1-5-32-545");
    static SecurityIdentifier Administrators => new("S-1-5-32-544");
    const string U = "S-1-5-21-1-2-3-1000";
    const string Admins = "O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)";

    static bool? Locked(string sddl, bool folder = false, SecurityIdentifier[]? groups = null, SecurityIdentifier[]? denyOnly = null) =>
        TaskAccess.Unchangeable(sddl, folder, User, groups ?? [], denyOnly);

    [Theory]
    [InlineData("O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;" + U + ")", true)]
    [InlineData("O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;" + U + ")", false)]
    [InlineData("O:BAG:SYD:(A;;SD;;;" + U + ")", false)]
    [InlineData("O:BAG:SYD:(A;;WD;;;" + U + ")", false)]
    [InlineData("O:BAG:SYD:(A;;WO;;;" + U + ")", false)]
    [InlineData("O:" + U + "G:SYD:(A;;FA;;;SY)(A;;FR;;;" + U + ")", false)]
    [InlineData("O:BAG:SYD:(D;;FA;;;" + U + ")(A;;FA;;;" + U + ")", true)]
    [InlineData("O:BAG:SY", false)]
    public void A_file_is_locked_only_when_the_user_cannot_write_delete_or_change_its_permissions(string sddl, bool locked)
    {
        Assert.Equal(locked, Locked(sddl));
    }

    [Fact]
    public void A_file_the_users_group_may_modify_is_not_locked_unless_a_deny_comes_first()
    {
        const string modifyForUsers = "O:BAG:SYD:(A;;0x1301bf;;;BU)";
        Assert.False(Locked(modifyForUsers, groups: [Users]));
        Assert.True(Locked("O:BAG:SYD:(D;;FA;;;BU)(A;;0x1301bf;;;BU)", groups: [Users]));
        Assert.True(Locked(modifyForUsers, groups: []));
    }

    [Fact]
    public void An_administrator_running_unelevated_does_not_count_through_a_group_held_for_denial_only()
    {
        const string sddl = "O:BAG:SYD:(A;;FA;;;BA)(A;;FR;;;" + U + ")";

        Assert.True(Locked(sddl, groups: [], denyOnly: [Administrators]));
        Assert.False(Locked(sddl, groups: [Administrators]));
    }

    [Fact]
    public void A_folder_is_locked_unless_the_user_may_delete_children_delete_it_or_change_its_permissions()
    {
        Assert.True(Locked("O:BAG:SYD:(A;;FA;;;SY)(A;;0x2;;;" + U + ")", folder: true));
        Assert.True(Locked("O:BAG:SYD:(A;;0x4;;;" + U + ")", folder: true));
        Assert.False(Locked("O:BAG:SYD:(A;;0x40;;;" + U + ")", folder: true));
        Assert.False(Locked("O:BAG:SYD:(A;;SD;;;" + U + ")", folder: true));
        Assert.False(Locked("O:BAG:SYD:(A;;WD;;;" + U + ")", folder: true));
    }

    [Fact]
    public void A_descriptor_that_cannot_be_read_or_decided_is_neither_locked_nor_unlocked()
    {
        Assert.Null(Locked("not a descriptor"));
        Assert.Null(Locked("O:BAG:SYD:(XA;;FA;;;" + U + ";(WIN://SYSAPPID Contains \"x\"))"));
    }

    const string Task = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Date>2026-11-20T08:00:00</Date></RegistrationInfo>"
        + "<Triggers><TimeTrigger><StartBoundary>2026-11-20T08:00:00</StartBoundary><Repetition><Interval>PT1M</Interval></Repetition></TimeTrigger></Triggers>"
        + "<Principals><Principal id=\"Author\"><UserId>S-1-5-21-1-2-3-1000</UserId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
        + "<Actions Context=\"Author\"><Exec><Command>C:\\Program Files\\PowerShell\\7\\pwsh.exe</Command>"
        + "<Arguments>-NoProfile -ExecutionPolicy Bypass -File \"C:\\Locked\\Cycle.ps1\" -Quiet</Arguments></Exec></Actions></Task>";

    [Fact]
    public void The_definition_hash_covers_actions_principal_and_triggers_and_ignores_formatting_and_registration()
    {
        var hash = ScriptTaskXml.Definition(Task);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, ScriptTaskXml.Definition(Task.Replace("2026-11-20T08:00:00</Date>", "2027-01-01T00:00:00</Date>").Replace("><", ">\r\n  <")));
        Assert.NotEqual(hash, ScriptTaskXml.Definition(Task.Replace("-Quiet", "-Loud")));
        Assert.NotEqual(hash, ScriptTaskXml.Definition(Task.Replace("PT1M", "PT1S")));
        Assert.NotEqual(hash, ScriptTaskXml.Definition(Task.Replace("LeastPrivilege", "HighestAvailable")));
        Assert.Null(ScriptTaskXml.Definition("<not a task"));
    }

    [Fact]
    public void A_program_action_pins_its_program_first_and_the_script_it_names()
    {
        Assert.Equal([@"C:\Program Files\PowerShell\7\pwsh.exe", @"C:\Locked\Cycle.ps1"], ScriptTaskXml.ActionFiles(Task));
        Assert.Equal([@"C:\Tools\run.exe"], ScriptTaskXml.ActionFiles(Task.Replace(@"C:\Program Files\PowerShell\7\pwsh.exe", @"C:\Tools\run.exe").Replace("-File \"C:\\Locked\\Cycle.ps1\"", "/x")));
    }

    [Theory]
    [InlineData("<Command>pwsh.exe</Command>", "relative program")]
    [InlineData("<Command>C:\\%ProgramFiles%\\pwsh.exe</Command>", "environment variable")]
    [InlineData("<Command>C:\\$env\\pwsh.exe</Command>", "dollar variable")]
    [InlineData("<Arguments>-File Cycle.ps1</Arguments>", "relative script")]
    [InlineData("<Arguments>-File \"C:\\%USERPROFILE%\\Cycle.ps1\"</Arguments>", "environment script")]
    [InlineData("<Arguments>-File \"C:\\$env\\Cycle.ps1\"</Arguments>", "dollar script")]
    [InlineData("<Arguments>-c \"&amp; C:\\Locked\\Cycle.ps1\"</Arguments>", "abbreviated command")]
    [InlineData("<Arguments>-Command \"&amp; C:\\Locked\\Cycle.ps1\"</Arguments>", "inline command")]
    [InlineData("<Arguments>-EncodedCommand AAAA</Arguments>", "encoded command")]
    public void An_action_whose_files_the_user_could_redirect_or_that_cannot_be_pinned_has_no_files(string changed, string why)
    {
        var xml = changed.StartsWith("<Command>", StringComparison.Ordinal)
            ? Task.Replace("<Command>C:\\Program Files\\PowerShell\\7\\pwsh.exe</Command>", changed)
            : Task.Replace("<Arguments>-NoProfile -ExecutionPolicy Bypass -File \"C:\\Locked\\Cycle.ps1\" -Quiet</Arguments>", changed);

        Assert.True(ScriptTaskXml.ActionFiles(xml) is null, why);
    }

    [Fact]
    public void Two_actions_or_an_action_that_is_not_a_program_have_no_files()
    {
        const string exec = "<Exec><Command>C:\\a.exe</Command></Exec>";
        Assert.Null(ScriptTaskXml.ActionFiles(Task.Replace("</Actions>", exec + "</Actions>")));
        Assert.Null(ScriptTaskXml.ActionFiles(Task.Replace(Task[Task.IndexOf("<Exec>", StringComparison.Ordinal)..(Task.IndexOf("</Exec>", StringComparison.Ordinal) + 7)], "<ComHandler><ClassId>{1}</ClassId></ComHandler>")));
    }

    [Fact]
    public void A_pinned_file_counts_as_locked_only_when_it_and_every_folder_above_it_are()
    {
        var me = WindowsIdentity.GetCurrent().User!.Value;
        string Readonly(string who) => $"O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;{who})";
        string Deletes(string who) => $"O:BAG:SYD:(A;;FA;;;SY)(A;;0x40;;;{who})";

        Assert.True(WindowsScriptTaskReader.AllLocked([(false, Readonly(me)), (true, Readonly(me)), (true, Readonly(me))]));
        Assert.False(WindowsScriptTaskReader.AllLocked([(false, Readonly(me)), (true, Deletes(me)), (true, Readonly(me))]));
        Assert.False(WindowsScriptTaskReader.AllLocked([(false, Readonly(me)), (true, Readonly(me)), (true, "not a descriptor")]));
        Assert.False(WindowsScriptTaskReader.AllLocked([(false, "O:BAG:SY")]));
    }

    [Fact]
    public void A_file_in_a_folder_this_user_can_write_is_not_locked_and_a_system_file_is()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var folder = Directory.CreateTempSubdirectory("script-task-reader-");
        try
        {
            var mine = Path.Combine(folder.FullName, "mine.ps1");
            File.WriteAllText(mine, "# mine");
            var reader = new WindowsScriptTaskReader();

            Assert.False(reader.FileLocked(mine));
            // An elevated administrator really can change the Windows folder, so the system file is locked only for an unelevated user.
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) { Assert.True(reader.FileLocked(Path.Combine(Environment.SystemDirectory, "cmd.exe"))); }
            Assert.False(reader.FileLocked(@"relative\path.ps1"));
            Assert.False(reader.FileLocked(Path.Combine(folder.FullName, "missing.ps1")));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(mine))), reader.FileSha256(mine));
            Assert.Null(reader.FileSha256(Path.Combine(folder.FullName, "missing.ps1")));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void A_task_that_does_not_exist_reads_as_nothing_and_is_never_locked()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var reader = new WindowsScriptTaskReader();
        const string none = @"\AgentBridgeTests\NoSuchTask-3312";

        Assert.Null(reader.Definition(none));
        Assert.Null(reader.ActionFiles(none));
        Assert.Empty(reader.RunningEngines(none));
        Assert.False(reader.TaskLocked(none));
    }
}
