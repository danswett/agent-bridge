using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class HaConnectionControlTests
{
    static (int Code, string Output, string Error) Run(string folder, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = HaCommands.Run(args, folder, () => throw new InvalidOperationException("must not request a token"),
            output, error, devBuild: true, agentHaBridge: () => null);
        return (code, output.ToString(), error.ToString());
    }

    static string Configure(string folder, bool enabled = true)
    {
        var path = Path.Combine(folder, HostSettings.FileName);
        File.WriteAllText(path, $$$"""
            {"version":1,"homeAssistant":{"enabled":{{{enabled.ToString().ToLowerInvariant()}}},"baseUrl":"http://ha.fixture.invalid:8123","disposable":true},
             "agentMessaging":{"remote":"own-account"},"permissions":{"remoteApproval":true},"launch":{"enabled":true},"future":{"keep":42}}
            """);
        HaTokenStore.Save(folder, "fixture-token-retained");
        return path;
    }

    [Theory]
    [InlineData("off"), InlineData("on")]
    public void Connection_changes_are_person_only_host_management(string state) =>
        Assert.True(HaCommands.Writes(["ha", state]));

    [Fact]
    public async Task Off_then_on_preserves_the_relay_and_credentials_and_audits_both_restart_transitions()
    {
        using var fixture = new Fixture();
        var path = Configure(fixture.Root);
        var expected = JsonNode.Parse(File.ReadAllText(path))!;
        await using var source = fixture.Source();
        var log = new List<string>();
        foreach (var state in new[] { "off", "on" })
        {
            var result = Run(fixture.Root, "ha", state);
            Assert.Equal(0, result.Code);
            Assert.Empty(result.Error);
            Assert.Contains("Restart", result.Output);
            expected["homeAssistant"]!["enabled"] = state == "on";
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(path))));
            Assert.Equal("fixture-token-retained", HaTokenStore.Load(fixture.Root));
            var settings = HostSettings.Load(fixture.Root).Settings;
            if (state == "off")
            {
                Assert.Null(HaRunning.Start(settings, fixture.Root, source, null, () => null, true, "fixture", log.Add));
                Assert.Contains(log, line => line.Contains("connector is off", StringComparison.Ordinal));
            }
            else { Assert.Null(HaGate.WhyNot(settings.HomeAssistant, true, HaTokenStore.Exists(fixture.Root), true)); }
        }
        var (rows, complete) = GateAuditProjection.Load(fixture.Root);
        Assert.True(complete);
        Assert.Equal(["requested", "configured-off", "requested", "configured-on"], rows.Select(row => row.Outcome));
        Assert.All(rows, row =>
        {
            Assert.Equal("host.homeAssistant", row.Action);
            Assert.Equal("person:local", row.Principal);
            Assert.Equal("local", row.Direction);
            Assert.Null(row.Gate);
        });
        Assert.DoesNotContain("fixture-token-retained", string.Join("\n", Directory.GetFiles(
            Path.Combine(fixture.Root, AgentAuditLog.FolderName), "*.jsonl").SelectMany(File.ReadLines)));
    }

    [Theory]
    [InlineData("off"), InlineData("on")]
    public void An_unwritable_audit_refuses_the_change_before_touching_configuration(string state)
    {
        using var fixture = new Fixture();
        var path = Configure(fixture.Root, enabled: state == "off");
        var before = File.ReadAllBytes(path);
        File.WriteAllText(Path.Combine(fixture.Root, AgentAuditLog.FolderName), "blocked");
        var result = Run(fixture.Root, "ha", state);
        Assert.Equal(1, result.Code);
        Assert.Contains("audit", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("fixture-token-retained", HaTokenStore.Load(fixture.Root));
    }

    [Theory]
    [InlineData("off"), InlineData("on")]
    public void Extra_arguments_do_not_change_configuration_or_create_audit_evidence(string state)
    {
        using var fixture = new Fixture();
        var path = Configure(fixture.Root);
        var before = File.ReadAllBytes(path);
        Assert.Equal(2, Run(fixture.Root, "ha", state, "unexpected").Code);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, AgentAuditLog.FolderName)));
    }

    [Theory]
    [InlineData("token"), InlineData("disposable"), InlineData("baseUrl")]
    public void On_does_not_bypass_the_connector_startup_guards(string missing)
    {
        using var fixture = new Fixture();
        var path = Configure(fixture.Root, enabled: false);
        if (missing == "token") { HaTokenStore.Clear(fixture.Root); }
        else
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            root["homeAssistant"]!.AsObject().Remove(missing);
            File.WriteAllText(path, root.ToJsonString());
        }
        var before = File.ReadAllBytes(path);
        var result = Run(fixture.Root, "ha", "on");
        Assert.Equal(1, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
