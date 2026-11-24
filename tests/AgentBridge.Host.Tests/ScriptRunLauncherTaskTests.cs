using AgentBridge.Host;
using AgentBridge.Local;
using Xunit;

// #3305 slice S3 (docs/design/script-credentials-run.md sections 3, 4, 7 and 9): the task reads that bind a credential to a task whose ONE action is the admin-locked
// `script-run.exe` launcher: `script-run <name> -- <absolute program> [-NoProfile -NonInteractive -File <absolute script>]`. The reader pins the launcher, the child
// program AND the script (never optional), and refuses anything it cannot pin, any principal that stores a password (the owner's Q-run-2 answer), and an engine that
// is not the launcher itself.
public sealed class ScriptRunLauncherTaskTests
{
    const string Launcher = @"C:\Program Files\Slipgates\script-run.exe";
    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    const string Script = @"C:\Locked\Cycle.ps1";
    const string Good = "dispatcher -- \"" + Pwsh + "\" -NoProfile -NonInteractive -File \"" + Script + "\"";

    static string Xml(string arguments, string? logon = "InteractiveToken", string command = Launcher) =>
        "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><TimeTrigger><StartBoundary>2026-11-24T08:00:00</StartBoundary></TimeTrigger></Triggers>"
        + "<Principals><Principal id=\"Author\"><UserId>S-1-5-21-1-2-3-1000</UserId>" + (logon is null ? "" : $"<LogonType>{logon}</LogonType>") + "<RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
        + $"<Actions Context=\"Author\"><Exec><Command>{command}</Command><Arguments>{arguments.Replace("&", "&amp;")}</Arguments></Exec></Actions></Task>";

    [Fact]
    public void The_launcher_the_child_program_and_the_script_are_all_pinned_launcher_first()
    {
        Assert.Equal([Launcher, Pwsh, Script], ScriptTaskXml.ActionFiles(Xml(Good)));
        Assert.Equal([Launcher, @"C:\Tools\cycle.exe"], ScriptTaskXml.ActionFiles(Xml(@"dispatcher -- C:\Tools\cycle.exe")));
        Assert.Equal([Launcher, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", Script],
            ScriptTaskXml.ActionFiles(Xml(@"fleet.dispatcher-1 -- C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe /NoProfile /NonInteractive /File " + Script)));
    }

    [Theory]
    [InlineData("dispatcher", "no child at all")]
    [InlineData("dispatcher " + Pwsh, "no separator")]
    [InlineData("-- " + Pwsh, "no name")]
    [InlineData(".. -- " + Pwsh, "a name that is a path segment")]
    [InlineData("a/b -- C:\\Tools\\cycle.exe", "a name with a separator")]
    [InlineData("dispatcher -- pwsh.exe -NoProfile -NonInteractive -File C:\\Locked\\Cycle.ps1", "a relative child program")]
    [InlineData("dispatcher -- C:\\%ProgramFiles%\\pwsh.exe -NoProfile -NonInteractive -File C:\\Locked\\Cycle.ps1", "an environment variable in the child")]
    [InlineData("dispatcher -- " + Pwsh + " -NoProfile -NonInteractive -Command \"& C:\\Locked\\Cycle.ps1\"", "inline code, no script file can be named")]
    [InlineData("dispatcher -- " + Pwsh + " -NoProfile -NonInteractive -c \"& C:\\Locked\\Cycle.ps1\"", "abbreviated inline code")]
    [InlineData("dispatcher -- " + Pwsh + " -NoProfile -NonInteractive -EncodedCommand AAAA", "encoded command")]
    [InlineData("dispatcher -- " + Pwsh + " -NonInteractive -File C:\\Locked\\Cycle.ps1", "an interpreter without -NoProfile")]
    [InlineData("dispatcher -- " + Pwsh + " -NoProfile -File C:\\Locked\\Cycle.ps1", "an interpreter without -NonInteractive")]
    [InlineData("dispatcher -- " + Pwsh + " -NoProfile -NonInteractive -Unknown -File C:\\Locked\\Cycle.ps1", "an unknown switch")]
    [InlineData("dispatcher -- " + Pwsh + " -NoProfile -NonInteractive -File Cycle.ps1", "a relative script")]
    [InlineData("dispatcher -- " + Pwsh + " -NoProfile -NonInteractive -File \\\\server\\share\\Cycle.ps1", "a share script")]
    [InlineData("dispatcher -- C:\\Tools\\cycle.exe --flag", "extra arguments for a program that is not an interpreter")]
    [InlineData("dispatcher -- C:\\Windows\\System32\\cmd.exe /c C:\\Locked\\Cycle.cmd", "a command interpreter")]
    public void Anything_that_cannot_be_pinned_has_no_files(string arguments, string why) =>
        Assert.True(ScriptTaskXml.ActionFiles(Xml(arguments)) is null, why);

    // The owner's Q-run-2 answer: logged-on-only tasks, no stored password. A task that stores one (or runs without the user's session) pins nothing, so no credential
    // is minted for it.
    [Theory]
    [InlineData("Password")]
    [InlineData("S4U")]
    [InlineData("InteractiveTokenOrPassword")]
    [InlineData("ServiceAccount")]
    [InlineData("Group")]
    [InlineData(null)]
    public void A_task_that_is_not_logged_on_only_pins_nothing(string? logon) => Assert.Null(ScriptTaskXml.ActionFiles(Xml(Good, logon)));

    [Fact]
    public void A_second_action_or_a_second_principal_pins_nothing()
    {
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Good).Replace("</Actions>", "<Exec><Command>C:\\a.exe</Command></Exec></Actions>")));
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Good).Replace("</Principals>", "<Principal id=\"Other\"><LogonType>InteractiveToken</LogonType></Principal></Principals>")));
    }

    [Fact]
    public void The_definition_covers_the_launcher_arguments_so_changing_what_it_runs_changes_the_pin()
    {
        var hash = ScriptTaskXml.Definition(Xml(Good));
        Assert.NotEqual(hash, ScriptTaskXml.Definition(Xml(Good.Replace("Cycle.ps1", "Other.ps1"))));
        Assert.NotEqual(hash, ScriptTaskXml.Definition(Xml(Good.Replace("dispatcher", "another"))));
        Assert.NotEqual(hash, ScriptTaskXml.Definition(Xml(Good, "Password")));
    }

    // The accepted engine must be the launcher itself: an intermediate engine (the Scheduler's own host, or the script's interpreter) is refused.
    static readonly DateTimeOffset Started = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    sealed class FakeTasks(string? openFile = null) : IScriptTaskReader
    {
        public string? Definition(string task) => new string('a', 64);
        public IReadOnlyList<string>? ActionFiles(string task) => [Launcher, Pwsh, Script];
        public IReadOnlyList<ScriptTaskEngine> RunningEngines(string task) => [new(4242, Started)];
        public bool TaskLocked(string task) => true;
        public bool FileLocked(string path) => !string.Equals(path, openFile, StringComparison.OrdinalIgnoreCase);
        public string? FileSha256(string path) => new string('b', 64);
    }

    static ProcessAncestry Ancestry(string engineImage) => new(
        [new PeerProcess(9000, Started.AddSeconds(5), Pwsh), new PeerProcess(4242, Started, engineImage), new PeerProcess(700, Started.AddHours(-9), @"C:\Windows\System32\svchost.exe")], true);

    [Fact]
    public void Only_the_launcher_itself_is_an_accepted_engine()
    {
        if (!OperatingSystem.IsWindows()) { return; } // the pin checks drive-letter paths with Path.IsPathFullyQualified, which is Windows' rule
        var reader = new FakeTasks();
        var pinned = ScriptTaskBinding.Pin(@"\Fleet\Dispatcher", [], reader);
        Assert.Equal([Launcher, Pwsh, Script], pinned.Files.Select(f => f.Path));
        Assert.Null(pinned.Refusal(Ancestry(Launcher), reader));
        Assert.Contains("engine", pinned.Refusal(Ancestry(@"C:\Windows\System32\taskhostw.exe"), reader));
        Assert.Contains("engine", pinned.Refusal(Ancestry(Pwsh), reader));
        Assert.Contains("engine", pinned.Refusal(Ancestry(@"C:\Users\someone\script-run.exe"), reader));
    }

    [Theory]
    [InlineData(Launcher)]
    [InlineData(Pwsh)]
    [InlineData(Script)]
    public void An_unlocked_launcher_child_or_script_is_refused_at_the_mint(string unlocked)
    {
        if (!OperatingSystem.IsWindows()) { return; } // the pin checks drive-letter paths with Path.IsPathFullyQualified, which is Windows' rule
        Assert.Contains(unlocked, Assert.Throws<InvalidOperationException>(() => ScriptTaskBinding.Pin(@"\Fleet\Dispatcher", [], new FakeTasks(unlocked))).Message);
    }
}
