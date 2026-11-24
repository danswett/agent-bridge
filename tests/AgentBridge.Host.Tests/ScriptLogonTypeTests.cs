using AgentBridge.Host;
using AgentBridge.Local;
using Xunit;

// #3305 (the owner's Q-run-2 answer, 2026-11-24: logged-on-only scheduled tasks, no stored password; #4514, #4524 P2-3): a task is bound only when its ONE principal runs
// with InteractiveToken, for EVERY bound task and not only the launcher's form, at the mint and again when the credential is accepted. S4U also runs while the user is
// logged off, so any logon type other than InteractiveToken is refused.
public sealed class ScriptLogonTypeTests
{
    const string Program = @"C:\Tools\cycle.exe";
    static readonly DateTimeOffset Started = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    static string Xml(string principals) =>
        "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Principals>" + principals + "</Principals>"
        + $"<Actions Context=\"Author\"><Exec><Command>{Program}</Command></Exec></Actions></Task>";

    static string One(string? logon) => "<Principal id=\"Author\"><UserId>S-1-5-21-1-2-3-1000</UserId>" + (logon is null ? "" : $"<LogonType>{logon}</LogonType>") + "</Principal>";

    [Fact]
    public void Only_a_single_principal_with_a_single_interactive_token_logon_is_logged_on_only()
    {
        Assert.True(ScriptTaskXml.LoggedOnOnly(Xml(One("InteractiveToken"))));
        Assert.True(ScriptTaskXml.LoggedOnOnly(Xml("<Principal id=\"A\"><LogonType> InteractiveToken </LogonType></Principal>")));
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("S4U")]
    [InlineData("InteractiveTokenOrPassword")]
    [InlineData("ServiceAccount")]
    [InlineData("Group")]
    [InlineData("interactivetoken")]
    [InlineData(null)]
    public void Any_other_logon_type_or_none_is_not_logged_on_only(string? logon) => Assert.False(ScriptTaskXml.LoggedOnOnly(Xml(One(logon))));

    [Fact]
    public void Two_principals_two_logon_elements_no_principals_or_malformed_xml_fail_closed()
    {
        Assert.False(ScriptTaskXml.LoggedOnOnly(Xml(One("InteractiveToken") + One("InteractiveToken"))));
        Assert.False(ScriptTaskXml.LoggedOnOnly(Xml("<Principal id=\"A\"><LogonType>InteractiveToken</LogonType><LogonType>InteractiveToken</LogonType></Principal>")));
        Assert.False(ScriptTaskXml.LoggedOnOnly(Xml("")));
        Assert.False(ScriptTaskXml.LoggedOnOnly("<not a task"));
    }

    // The read of #4565: only the single Principal's own LogonType counts. An InteractiveToken in Settings or in the action must not stand in for the principal's.
    [Fact]
    public void A_logon_type_elsewhere_in_the_task_is_a_decoy_and_the_principals_own_decides()
    {
        const string interactive = "<LogonType>InteractiveToken</LogonType>";
        var password = One("Password");
        var noLogon = One(null);
        foreach (var principal in new[] { password, noLogon })
        {
            Assert.False(ScriptTaskXml.LoggedOnOnly(Xml(principal).Replace("</Task>", "<Settings>" + interactive + "</Settings></Task>")));
            Assert.False(ScriptTaskXml.LoggedOnOnly(Xml(principal).Replace("<Command>", "<Command>" + interactive + "</Command><Arguments>").Replace("</Command></Exec>", "</Arguments></Exec>")));
            Assert.False(ScriptTaskXml.LoggedOnOnly(Xml(principal).Replace("</Principal>", "<RunLevel>" + interactive + "</RunLevel></Principal>")));
        }
    }

    [Fact]
    public void Only_a_Principal_element_inside_one_Principals_element_counts()
    {
        Assert.False(ScriptTaskXml.LoggedOnOnly(Xml("<Other><LogonType>InteractiveToken</LogonType></Other>")));
        Assert.False(ScriptTaskXml.LoggedOnOnly(Xml(One("InteractiveToken")).Replace("</Task>", "<Principals>" + One("Password") + "</Principals></Task>")));
    }

    sealed class Tasks(bool loggedOnOnly) : IScriptTaskReader
    {
        public bool LoggedOn { get; set; } = loggedOnOnly;
        public string? Definition(string task) => new string('a', 64);
        public IReadOnlyList<string>? ActionFiles(string task) => [Program];
        public IReadOnlyList<ScriptTaskEngine> RunningEngines(string task) => [new(4242, Started)];
        public bool TaskLocked(string task) => true;
        public bool FileLocked(string path) => true;
        public string? FileSha256(string path) => new string('b', 64);
        public bool LoggedOnOnly(string task) => LoggedOn;
    }

    static ProcessAncestry Engine() => new([new PeerProcess(4242, Started, Program), new PeerProcess(700, Started.AddHours(-9), @"C:\Windows\System32\svchost.exe")], true);

    [Fact]
    public void The_mint_refuses_a_task_that_is_not_logged_on_only_whatever_its_action_looks_like()
    {
        if (!OperatingSystem.IsWindows()) { return; } // the pin checks drive-letter paths, which is Windows' rule
        var refused = Assert.Throws<InvalidOperationException>(() => ScriptTaskBinding.Pin(@"\Fleet\Dispatcher", [], new Tasks(false)));
        Assert.Contains("logged off or with a stored password", refused.Message);
        Assert.NotNull(ScriptTaskBinding.Pin(@"\Fleet\Dispatcher", [], new Tasks(true)));
    }

    [Fact]
    public void A_credential_bound_to_a_task_that_has_since_stopped_being_logged_on_only_is_refused_at_accept()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var tasks = new Tasks(true);
        var pinned = ScriptTaskBinding.Pin(@"\Fleet\Dispatcher", [], tasks);
        Assert.Null(pinned.Refusal(Engine(), tasks));
        tasks.LoggedOn = false;
        Assert.Contains("logged-on-only", pinned.Refusal(Engine(), tasks));
    }
}
