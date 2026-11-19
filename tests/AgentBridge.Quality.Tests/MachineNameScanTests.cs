using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// Owner principle on #2643: no machine name or list anywhere in code, scripts, docs or tests, so
// fleet membership can only come from the relay roster. Fixtures are exempt.
public class MachineNameScanTests
{
    // The shapes the fleet's machine names take (a numbered VM, or the owner's name as a prefix),
    // so that no name itself is written here.
    static readonly Regex MachineName = new(@"\bvm[1-9]\d*\b|\bdswett-[a-z0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Areas a later #2643 slice still has to clean. The list only shrinks: an entry with nothing
    // left in it fails until it is removed.
    static readonly string[] NotYetCleaned =
    [
        "design/", "pwa/", "tests/AgentBridge.HomeAssistant.Tests/", "tools/ha-contract/",
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
}
