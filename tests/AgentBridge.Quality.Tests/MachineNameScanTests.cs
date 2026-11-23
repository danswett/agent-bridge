using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality.Tests;

// Owner principle on #2643: no machine name or list anywhere in code, scripts, docs or tests, so
// fleet membership can only come from the relay roster. Fixtures are exempt.
public class MachineNameScanTests
{
    // The shapes the fleet's machine names take (a numbered VM, also inside an underscore slug,
    // or the owner's name as a prefix), so that no name itself is written here.
    static readonly Regex MachineName = new(@"(?<![a-z0-9])vm[1-9]\d*(?![a-z0-9])|(?<![a-z0-9])dswett-[a-z0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A name written bare, without its owner prefix, has no shape to match. Only SHA-256 digests of
    // such upper-case words are kept, so the names are not written here either. Upper case only:
    // in lower case one of them is an ordinary word (a launch effort level).
    static readonly HashSet<string> BareNames =
        ["70934311f6667eb3003eb34372ef84a513a54d8c1d1e6934d332a551229b13c2", "ddb56b095eefb92f8dc8a34de80810f462f69b435538db5a4a021039d8e8062b"];
    static readonly Regex UpperWord = new(@"(?<![A-Za-z0-9])[A-Z][A-Z0-9]{2,}(?![A-Za-z0-9])", RegexOptions.CultureInvariant);

    static bool Names(string text) => MachineName.IsMatch(text) || UpperWord.Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal)
        .Any(word => BareNames.Contains(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(word)))));

    [Fact]
    public void A_name_inside_an_underscore_slug_is_still_a_name() => Assert.True(Names("agent_" + "dswett" + "-box"));

    [Fact]
    public void An_ordinary_upper_case_word_or_a_neutral_machine_label_is_not_a_name()
    {
        Assert.False(Names("Ultra effort, ULTRAVIOLET, MACHINE-B"));
    }

    // Areas a later #2643 slice still has to clean. The list only shrinks: an entry with nothing
    // left in it fails until it is removed.
    static readonly string[] NotYetCleaned =
    [
        "pwa/", "tests/AgentBridge.HomeAssistant.Tests/", "tools/ha-contract/",
    ];

    [Fact]
    public void Repo_scan_finds_no_machine_names_outside_fixtures()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var named = ChangedFiles.Tracked(root)
            .Where(path => !path.Split('/').Any(part => part.Equals("fixtures", StringComparison.OrdinalIgnoreCase)))
            .Where(path => File.Exists(Path.Combine(root, path)) && File.ReadAllText(Path.Combine(root, path)) is var text
                && !text.Contains('\0') && Names(text))
            .ToList();
        Assert.DoesNotContain(named, path => !NotYetCleaned.Any(area => path.StartsWith(area, StringComparison.Ordinal)));
        Assert.DoesNotContain(NotYetCleaned, area => !named.Any(path => path.StartsWith(area, StringComparison.Ordinal)));
    }
}
