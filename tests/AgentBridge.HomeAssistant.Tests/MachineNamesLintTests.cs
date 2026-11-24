using System.Text.RegularExpressions;
using Xunit;

// Fleet machines are dynamic: their membership comes only from the relay roster, so no test names one. The tests of this project take the
// machine they use from the recorded contract fixture (Contract.Machine), and the Fixtures themselves, which are recorded by agent-ha-bridge's
// own functions and are not edited by hand, are the one place such a name may stay. This file, which holds sample names to prove the lint sees
// them, is the one source it skips (and bin and obj), and only the recorded JSON under Fixtures is exempt: a .cs file there is read like any other.
// A line lint cannot see a name split across a concatenation. Owner principle (Nov 17), #2880 and #2924.
public sealed class MachineNamesLintTests
{
    // Built in pieces so that this file does not name what it looks for.
    static readonly Regex Names = new("dsw" + "ett|dev[-_ ]?vm[0-9]+|(?<![a-z0-9])vm[-_ ]?[0-9]+(?![a-z0-9])|arm" + "8(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        return root;
    }

    // The sources of a project and the lines of them that name a machine.
    static (List<string> Files, List<string> Hits) Scan(string project)
    {
        var skipped = new[] { "bin", "obj" };

        // Judged on the path inside the project, so a checkout under a folder that is itself called bin or obj is still read.
        var files = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(project, f))
            .Where(r => r != "MachineNamesLintTests.cs" && !r.Split(Path.DirectorySeparatorChar).Take(1).Any(skipped.Contains))
            .ToList();
        var hits = files
            .SelectMany(r => File.ReadAllLines(Path.Combine(project, r)).Select((line, i) => (File: r.Replace(Path.DirectorySeparatorChar, '/'), Line: i + 1, Text: line)))
            .Where(l => Names.IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();
        return (files, hits);
    }

    [Fact]
    public void No_test_source_in_this_project_names_a_machine()
    {
        var (files, hits) = Scan(Path.Combine(Root(), "tests", "AgentBridge.HomeAssistant.Tests"));

        Assert.Contains("AuthBackoffTests.cs", files);
        Assert.True(files.Count > 20, "the lint must have read the project's sources");
        Assert.Empty(hits);
    }

    [Fact]
    public void Every_recorded_fixture_that_names_the_engineering_machine_names_the_one_the_tests_use()
    {
        var mine = new[] { Contract.Machine.ToLowerInvariant(), Contract.Slug.ToLowerInvariant() };
        var seen = Regex.Matches(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-contract.json")), "(dsw"+"ett[-_])?dev[-_]?vm[0-9][a-z0-9_]*", RegexOptions.IgnoreCase).Count;

        var other = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.json")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "(dsw"+"ett[-_])?dev[-_]?vm[0-9][a-z0-9_]*", RegexOptions.IgnoreCase).Select(m => (File: Path.GetFileName(f), Name: m.Value.ToLowerInvariant())))
            .Where(t => !mine.Any(m => t.Name.StartsWith(m, StringComparison.Ordinal)))
            .Select(t => $"{t.File}: {t.Name}")
            .Distinct()
            .ToList();

        Assert.True(seen > 0, "the contract fixture records the machine the tests use");
        Assert.Empty(other);
    }

    // Only the recorded JSON is exempt (Q10's P2 on #4390): a source file dropped into Fixtures is read like any other.
    [Fact]
    public void A_cs_file_under_Fixtures_is_read_while_the_recorded_JSON_there_is_not()
    {
        var project = Path.Combine(Path.GetTempPath(), "lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, "Fixtures"));
        try
        {
            File.WriteAllText(Path.Combine(project, "Fixtures", "Recorded.cs"), "const string M = \"" + "dsw" + "ett-dev-vm" + "1\";");
            File.WriteAllText(Path.Combine(project, "Fixtures", "ha.json"), "{\"machine\": \"" + "dsw" + "ett_dev_vm" + "1\"}");
            File.WriteAllText(Path.Combine(project, "Fine.cs"), "const string M = Contract.Machine;");

            var (files, hits) = Scan(project);

            Assert.Contains("Fine.cs", files);
            Assert.Equal(["Fixtures/Recorded.cs:1"], hits);
        }
        finally { Directory.Delete(project, true); }
    }

    [Theory]
    [InlineData("const string Machine = \"" + "DSW" + "ETT-DEV-VM" + "1\";")]
    [InlineData("MachineName = \"" + "DSW" + "ETT-ARM" + "8\"")]
    [InlineData("Slug(\"dsw" + "ett_dev_vm" + "1\")")]
    [InlineData("the dashboard " + "DSW" + "ETT-HOME generates")]
    [InlineData("DEV-" + "VM" + "2 (shadow)")]
    [InlineData("a host named vm" + "3 here")]
    [InlineData("host_vm" + "1")]
    [InlineData("vm" + "10")]
    [InlineData("a host named vm" + "-4 here")]
    [InlineData("a host named \"vm" + " 4\" here")]
    [InlineData("host_vm" + "_4")]
    public void The_lint_sees_the_names_it_is_about(string line)
    {
        Assert.Matches(Names, line);
    }

    [Theory]
    [InlineData("const string Machine = Contract.Machine;")]
    [InlineData("MachineName = \"machine-c\"")]
    [InlineData("a machine-a and a vm-less roster")]
    [InlineData("vm" + "10x and charm" + "8x")]
    public void It_leaves_neutral_names_alone(string line)
    {
        Assert.DoesNotMatch(Names, line);
    }
}
