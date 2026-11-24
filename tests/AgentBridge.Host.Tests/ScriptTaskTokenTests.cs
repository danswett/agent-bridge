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
}
