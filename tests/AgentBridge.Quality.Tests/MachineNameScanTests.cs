using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// Owner principle on #2643: no machine name or list anywhere in code, scripts, docs or tests, so
// fleet membership can only come from the relay roster. Fixtures are exempt.
public class MachineNameScanTests
{
    // The shapes the fleet's machine names take (a numbered VM, also inside an underscore slug,
    // or the owner's name as a prefix), so that no name itself is written here.
    static readonly Regex MachineName = new(@"(?<![a-z0-9])vm[1-9]\d*(?![a-z0-9])|\bdswett-[a-z0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Areas a later #2643 slice still has to clean. The list only shrinks: an entry with nothing
    // left in it fails until it is removed. docs/design/ also names a machine the shapes above
    // can't catch, so it is cleaned with design/.
    static readonly string[] NotYetCleaned =
    [
        "design/", "docs/design/", "pwa/",
    ];

    [Fact]
    public void Repo_scan_finds_no_machine_names_outside_fixtures()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var named = ChangedFiles.Tracked(root)
            .Where(path => !path.Split('/').Any(part => part.Equals("fixtures", StringComparison.OrdinalIgnoreCase)))
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
}
