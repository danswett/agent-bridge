using AgentBridge.Host;
using Xunit;

// #4524 P2-1: the reader must see exactly the argv Windows (CommandLineToArgvW, which pwsh uses) hands the program. There a backslash-quote is a LITERAL quote, so a
// crafted action could pin Cycle.ps1 while pwsh ran an unpinned script. Anything that could make the two readings differ pins nothing.
public sealed class ScriptTaskTokenTests
{
    const string Launcher = @"C:\Program Files\Slipgates\script-run.exe";
    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    const string Script = @"C:\Locked\Cycle.ps1";
    const string Evil = @"C:\Evil\evil.ps1";

    static string Xml(string command, string arguments) =>
        "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Principals><Principal id=\"Author\"><UserId>S-1-5-21-1-2-3-1000</UserId>"
        + "<LogonType>InteractiveToken</LogonType></Principal></Principals>"
        + $"<Actions Context=\"Author\"><Exec><Command>{command}</Command><Arguments>{System.Security.SecurityElement.Escape(arguments)}</Arguments></Exec></Actions></Task>";

    // The crafted action of the read: Windows argv is -ExecutionPolicy, x" , -File C:\Evil\evil.ps1 , " , -File, C:\Locked\Cycle.ps1 -- so pwsh runs evil.ps1.
    const string Crafted = "-NoProfile -NonInteractive -ExecutionPolicy x\\\" -File " + Evil + " \\\" -File \"" + Script + "\"";

    [Fact]
    public void A_crafted_backslash_quote_that_would_hide_an_unpinned_script_pins_nothing_in_both_action_forms()
    {
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Pwsh, Crafted)));
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Launcher, "dispatcher -- \"" + Pwsh + "\" " + Crafted)));
    }

    [Theory]
    [InlineData(@"-File ""C:\Locked\""Cycle.ps1""", "a backslash-quote inside the script path")]
    [InlineData(@"-File C:\Locked""Cycle.ps1", "a quote in the middle of a token")]
    [InlineData(@"-File ""C:\Locked\Cycle.ps1""x", "text glued after the closing quote")]
    [InlineData(@"-File x""C:\Locked\Cycle.ps1""", "text glued before the opening quote")]
    [InlineData(@"-File ""C:\Locked\""", "a trailing backslash before the closing quote (it escapes the quote on Windows)")]
    [InlineData(@"-File ""C:\Locked\Cycle.ps1", "an unterminated quote")]
    [InlineData(@"-File ""C:\Locked\Cycle.ps1"" \""", "a stray backslash-quote after a good script")]
    public void A_quote_that_does_not_wrap_a_whole_token_or_a_backslash_quote_pins_nothing(string tail, string why)
    {
        Assert.True(ScriptTaskXml.ActionFiles(Xml(Pwsh, "-NoProfile -NonInteractive " + tail)) is null, why);
        Assert.True(ScriptTaskXml.ActionFiles(Xml(Launcher, "dispatcher -- \"" + Pwsh + "\" -NoProfile -NonInteractive " + tail)) is null, why);
    }

    [Fact]
    public void A_quote_that_wraps_a_whole_token_still_pins_it_in_both_forms()
    {
        var quoted = "-NoProfile -NonInteractive -File \"" + Script + "\"";
        Assert.Equal([Pwsh, Script], ScriptTaskXml.ActionFiles(Xml(Pwsh, quoted)));
        Assert.Equal([Launcher, Pwsh, Script], ScriptTaskXml.ActionFiles(Xml(Launcher, "dispatcher -- \"" + Pwsh + "\" " + quoted)));
        Assert.Equal([Pwsh, @"C:\Locked\My Cycle.ps1"], ScriptTaskXml.ActionFiles(Xml(Pwsh, "-NoProfile -NonInteractive -File \"C:\\Locked\\My Cycle.ps1\" -Quiet")));
    }

    // CommandLineToArgvW splits ONLY on space and tab. Any other separator (NBSP, newline, CR, U+0085, U+2003, U+2028, U+3000) is part of the token, so the reader must
    // not split there either, or it pins one script while pwsh runs another.
    [Theory]
    [InlineData(0x00A0)]
    [InlineData(0x000A)]
    [InlineData(0x000D)]
    [InlineData(0x0085)]
    [InlineData(0x2003)]
    [InlineData(0x2028)]
    [InlineData(0x3000)]
    public void A_separator_other_than_space_and_tab_stays_inside_the_token_so_nothing_hides_after_it(int separator)
    {
        var other = ((char)separator).ToString();
        var hidden = "-NoProfile -NonInteractive -File C:\\Locked\\A.ps1" + other + "\\..\\..\\Evil\\B.ps1";
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Pwsh, hidden)));
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Launcher, "dispatcher -- \"" + Pwsh + "\" " + hidden)));
        // What follows the separator is part of the same token: never the shorter script on its own.
        var glued = "-NoProfile -NonInteractive -File C:\\Locked\\A.ps1" + other + "-Command x";
        foreach (var files in new[] { ScriptTaskXml.ActionFiles(Xml(Pwsh, glued)), ScriptTaskXml.ActionFiles(Xml(Launcher, "dispatcher -- \"" + Pwsh + "\" " + glued)) })
        {
            Assert.True(files is null || files[^1] == "C:\\Locked\\A.ps1" + other + "-Command", "the reader pinned A.ps1 alone while pwsh runs the longer name");
        }
    }

    [Fact]
    public void A_tab_is_a_separator_like_a_space()
    {
        Assert.Equal([Pwsh, Script], ScriptTaskXml.ActionFiles(Xml(Pwsh, "-NoProfile\t-NonInteractive\t-File\t" + Script)));
    }

    // A quote inside a switch value is not a token's wrapping quote: pwsh would read it as part of the value, so the reader refuses it rather than pin what follows.
    [Fact]
    public void A_quote_inside_a_switch_value_pins_nothing()
    {
        const string tail = "-NoProfile -NonInteractive -ExecutionPolicy x\"y -File C:\\Locked\\A.ps1 z\"w -File C:\\Evil\\B.ps1";
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Pwsh, tail)));
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Launcher, "dispatcher -- \"" + Pwsh + "\" " + tail)));
    }

    // The two P3 fixes of the #4511 read: two logon elements fail closed (null, no exception), and a script name is anchored to the end (a trailing newline is not a name).
    [Fact]
    public void Two_logon_type_elements_pin_nothing_and_do_not_throw()
    {
        var xml = Xml(Launcher, "dispatcher -- \"" + Pwsh + "\" -NoProfile -NonInteractive -File \"" + Script + "\"")
            .Replace("<LogonType>InteractiveToken</LogonType>", "<LogonType>InteractiveToken</LogonType><LogonType>InteractiveToken</LogonType>");
        Assert.Null(ScriptTaskXml.ActionFiles(xml));
    }

    [Fact]
    public void A_script_name_with_a_trailing_newline_is_not_a_name()
    {
        Assert.Null(ScriptTaskXml.ActionFiles(Xml(Launcher, "dispatcher\n -- \"" + Pwsh + "\" -NoProfile -NonInteractive -File \"" + Script + "\"")));
        Assert.NotNull(ScriptTaskXml.ActionFiles(Xml(Launcher, "dispatcher -- \"" + Pwsh + "\" -NoProfile -NonInteractive -File \"" + Script + "\"")));
    }
}
