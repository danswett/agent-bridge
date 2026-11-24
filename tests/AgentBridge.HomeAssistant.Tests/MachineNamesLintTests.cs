using System.Text.RegularExpressions;
using Xunit;

// Fleet machines are dynamic: their membership comes only from the relay roster, so no test names one. The tests of this project take the
// machine they use from the recorded contract fixture (Contract.Machine), and the Fixtures themselves, which are recorded by agent-ha-bridge's
// own functions and are not edited by hand, are the one place such a name may stay. Owner principle (Nov 17), #2880 and #2924.
public sealed class MachineNamesLintTests
{
    // Built in pieces so that this file does not name what it looks for.
    static readonly Regex Names = new("dsw" + "ett|dev[-_ ]?vm[0-9]|\\bvm[1-9]\\b|arm" + "8", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void No_test_source_in_this_project_names_a_machine()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        var project = Path.Combine(root, "tests", "AgentBridge.HomeAssistant.Tests");
        var sep = Path.DirectorySeparatorChar;

        var hits = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}Fixtures{sep}"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetRelativePath(project, f), Line: i + 1, Text: line)))
            .Where(l => Names.IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.Empty(hits);
    }

    [Theory]
    [InlineData("const string Machine = \"" + "DSW" + "ETT-DEV-VM1\";")]
    [InlineData("MachineName = \"" + "DSW" + "ETT-ARM" + "8\"")]
    [InlineData("Slug(\"dsw" + "ett_dev_vm1\")")]
    [InlineData("the dashboard " + "DSW" + "ETT-HOME generates")]
    [InlineData("DEV-VM2 (shadow)")]
    public void The_lint_sees_the_names_it_is_about(string line)
    {
        Assert.Matches(Names, line);
    }

    [Theory]
    [InlineData("const string Machine = Contract.Machine;")]
    [InlineData("MachineName = \"machine-c\"")]
    [InlineData("a machine-a and a vm-less roster")]
    public void It_leaves_neutral_names_alone(string line)
    {
        Assert.DoesNotMatch(Names, line);
    }
}
