using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// HA retirement row 3 (session registry, status and name lookup), with HA off (#1396). The behaviour is covered by the tests above; these two
// pin that it does not depend on Home Assistant: no Home Assistant type is reachable from the fleet read path except the node id derivation,
// and the local API answers fleet.list and fleet.find with every Home Assistant input absent.
public sealed partial class FleetListTests
{
    static string Source => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));

    static IEnumerable<string> Sources(string folder, string pattern, SearchOption option = SearchOption.TopDirectoryOnly) =>
        Directory.GetFiles(Path.Combine(Source, folder), pattern, option)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void The_fleet_read_path_reaches_Home_Assistant_code_only_for_the_node_id_derivation()
    {
        var users = Sources(Path.Combine("AgentBridge.Host", "Fleet"), "*.cs")
            .Where(f => File.ReadAllText(f).Contains("AgentBridge.HomeAssistant", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["FleetNameEnrollment.cs", "FleetPublisher.cs"], users);
        foreach (var file in users)
        {
            var text = File.ReadAllText(Path.Combine(Source, "AgentBridge.Host", "Fleet", file));
            var names = Regex.Matches(text, @"\bHa[A-Z]\w*").Select(m => m.Value).Distinct().ToArray();
            Assert.Equal(["HaContract"], names);
            Assert.DoesNotContain("HaContract.", Regex.Replace(text, @"HaContract\.NodeId\(", ""));
        }
        // The relay's registry holds records and forwards them; it has no Home Assistant dependency of any kind.
        Assert.DoesNotContain(Sources("AgentBridge.Relay", "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".cs") || f.EndsWith(".csproj")),
            f => File.ReadAllText(f).Contains("HomeAssistant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_local_api_answers_fleet_list_and_find_with_every_Home_Assistant_input_absent()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        roster.Machines = [new FleetMachine(Device, true, [new FleetSession(Node, "my project", "working", Started, "s1")], Verified: true)];
        // Every Home Assistant parameter of the handler is null, and no Home Assistant setting or connector exists in this test.
        var handler = LocalApi.Handler(null!, null!, null!, null, null, store, null, Op());
        var chan = Hello(AgentProtocol.ScopeRead);
        var listed = JsonSerializer.SerializeToElement(await handler(chan, FleetList.Op, Body("{}")), Web);
        Assert.Equal(Node, listed.GetProperty("sessions")[0].GetProperty("node").GetString());
        var found = JsonSerializer.SerializeToElement(await handler(chan, FleetList.FindOp, Body(JsonSerializer.Serialize(new { sessionId = Node, machine = Device }))), Web);
        Assert.Equal("found", found.GetProperty("result").GetString());
        Assert.Equal(Node, found.GetProperty("session").GetProperty("node").GetString());
    }
}
