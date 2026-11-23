using System.Text.RegularExpressions;
using Xunit;

// HA retirement row 3 (session registry, status and name lookup), HA off (#1396). The behaviour (list, find, ID-first lookup, scope, audit) is
// covered by the tests above. This one pins the dependency: no Home Assistant code is reachable from the fleet read path except the node id
// derivation, and the relay has no Home Assistant reference at all, so a change that makes fleet.list or fleet.find need Home Assistant fails here.
// It reads the sources at run time; it does not exercise behaviour.
public sealed partial class FleetListTests
{
    // The repository's src folder: walk up from the test binary until it is found, so the build layout depth does not matter (and a miss fails loudly).
    static string Source
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var src = Path.Combine(dir.FullName, "src", "AgentBridge.Host");
                if (Directory.Exists(src)) { return Path.Combine(dir.FullName, "src"); }
            }
            throw new DirectoryNotFoundException("The repository src folder was not found above " + AppContext.BaseDirectory);
        }
    }

    static IEnumerable<string> Sources(string folder, string pattern) =>
        Directory.GetFiles(Path.Combine(Source, folder), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void The_fleet_read_path_reaches_Home_Assistant_code_only_for_the_node_id_derivation()
    {
        var fleet = Path.Combine("AgentBridge.Host", "Fleet");
        var users = Sources(fleet, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("AgentBridge.HomeAssistant", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["FleetNameEnrollment.cs", "FleetPublisher.cs"], users);
        foreach (var file in users)
        {
            var text = File.ReadAllText(Sources(fleet, file).Single());
            var names = Regex.Matches(text, @"\bHa[A-Z]\w*").Select(m => m.Value).Distinct().ToArray();
            Assert.Equal(["HaContract"], names);
            Assert.DoesNotContain("HaContract.", Regex.Replace(text, @"HaContract\.NodeId\(", ""));
        }
        // The relay's registry holds records and forwards them; it has no Home Assistant dependency of any kind.
        Assert.DoesNotContain(Sources("AgentBridge.Relay", "*").Where(f => f.EndsWith(".cs") || f.EndsWith(".csproj")),
            f => File.ReadAllText(f).Contains("HomeAssistant", StringComparison.OrdinalIgnoreCase));
    }
}
