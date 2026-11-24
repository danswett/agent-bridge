using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// Owner principle on #2643: no machine name or list anywhere in code, scripts, docs or tests, so
// fleet membership can only come from the relay roster. Recorded fixtures (the JSON under a fixtures folder) are exempt.
public class MachineNameScanTests
{
    // The shapes the fleet's machine names take (a numbered VM, also inside an underscore slug,
    // or the owner's name as a prefix), so that no name itself is written here.
    static readonly Regex MachineName = new(@"(?<![a-z0-9])vm[-_ ]?[1-9]\d*(?![a-z0-9])|\bdswett-[a-z0-9]|(?<![a-z0-9])arm" + @"8(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Areas a later #2643 slice still has to clean. The list only shrinks: an entry with nothing
    // left in it fails until it is removed. docs/design/ also names a machine the shapes above
    // can't catch, so it is cleaned with design/.
    static readonly string[] NotYetCleaned =
    [
        "design/", "docs/design/", "pwa/",
    ];

    // What the scan leaves alone: the recorded JSON under a fixtures folder, and nothing else. A source file dropped into Fixtures is read (Q10's P2 on #4390).
    static bool Exempt(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && path.Split('/').Any(part => part.Equals("fixtures", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("tests/AgentBridge.HomeAssistant.Tests/Fixtures/ha-contract.json", true)]
    [InlineData("tests/fixtures/claude/session.json", true)]
    [InlineData("tests/AgentBridge.HomeAssistant.Tests/Fixtures/Recorded.cs", false)]
    [InlineData("quality/fixtures/run.ps1", false)]
    [InlineData("tests/fixtures/claude/transcript.jsonl", false)]
    [InlineData("src/AgentBridge.HomeAssistant/Contract.json", false)]
    public void Only_the_recorded_JSON_under_fixtures_is_exempt(string path, bool exempt)
    {
        Assert.Equal(exempt, Exempt(path));
    }

    // The spellings of a numbered VM and the company's host that the scan must see (built in pieces: this file is scanned too).
    [Theory]
    [InlineData("a host named vm" + "-4 here")]
    [InlineData("a host named \"vm" + " 4\" here")]
    [InlineData("host_vm" + "_4")]
    [InlineData("on dsw" + "ett-arm" + "8 today")]
    [InlineData("the arm" + "8 box")]
    public void The_scan_sees_these_spellings(string line)
    {
        Assert.Matches(MachineName, line);
    }

    [Theory]
    [InlineData("a vm-less roster and a vm with no number")]
    [InlineData("charm" + "8x and arm" + "64")]
    public void The_scan_leaves_neutral_text_alone(string line)
    {
        Assert.DoesNotMatch(MachineName, line);
    }

    [Fact]
    public void Repo_scan_finds_no_machine_names_outside_fixtures()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var named = ChangedFiles.Tracked(root)
            .Where(path => !Exempt(path))
            .Where(path => File.Exists(Path.Combine(root, path)) && File.ReadAllText(Path.Combine(root, path)) is var text
                && !text.Contains('\0') && MachineName.IsMatch(text))
            .ToList();
        Assert.DoesNotContain(named, path => !NotYetCleaned.Any(area => path.StartsWith(area, StringComparison.Ordinal)));
        Assert.DoesNotContain(NotYetCleaned, area => !named.Any(path => path.StartsWith(area, StringComparison.Ordinal)));
    }

    // #2870: the contract exporter, the app's self-test, the merge-rule comment and the PWA demo name no machine (a machine they need comes from
    // a parameter or a neutral fixture name), whatever area they sit in. A name is any of the shapes above or the company's host.
    [Fact]
    public void The_exporter_the_self_test_the_merge_rule_and_the_demo_name_no_machine()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        string[] files =
        [
            "tools/ha-contract/Export-HaContract.ps1", "src/AgentBridge.HomeAssistant/HaContract.cs",
            "src/AgentBridge.App/SelfTest.MachineTabs.cs", "quality/merge-rule.yml", "pwa/js/demo.js",
        ];
        var host = new Regex("dsw" + "ett-arm|arm" + "8(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var named = files.Where(f => File.ReadAllText(Path.Combine(root, f)) is var text && (MachineName.IsMatch(text) || host.IsMatch(text))).ToList();

        Assert.Empty(named);
    }

    // The exporter takes its machines as mandatory parameters (none is written in it), and the drift check, which the daily workflow runs, records with
    // it: every mandatory parameter of the exporter must be handed over by the drift check, or the check stops with "could not record" every day.
    [Fact]
    public void The_drift_check_hands_the_exporter_every_parameter_it_requires()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var exporter = File.ReadAllText(Path.Combine(root, "tools", "ha-contract", "Export-HaContract.ps1"));
        var drift = File.ReadAllText(Path.Combine(root, "tools", "ha-contract", "Test-HaContractDrift.ps1"));

        var required = Regex.Matches(exporter, @"\[Parameter\(Mandatory\)\]\[string\]\$(\w+)").Select(m => m.Groups[1].Value).ToList();
        var missing = required.Where(name => !Regex.IsMatch(drift, "-" + name + "(?![A-Za-z0-9])")).ToList();

        Assert.Contains("Machine", required);
        Assert.Empty(missing);
    }
}
