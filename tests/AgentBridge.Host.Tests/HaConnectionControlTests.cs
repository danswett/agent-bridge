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
        var (rows, complete) = GateAuditProjection.Load(fixture.Root);
        Assert.True(complete);
        Assert.Equal(["requested", "refused"], rows.Select(row => row.Outcome));
        Assert.Equal(rows[0].Request, rows[1].Request);
    }

    [Theory]
    [InlineData("set", "true", false), InlineData("set", "false", true), InlineData("unset", null, true)]
    public void Generic_config_changes_cannot_bypass_the_audited_connection_control(string verb, string? value, bool enabled)
    {
        using var fixture = new Fixture();
        var path = Configure(fixture.Root, enabled);
        var before = File.ReadAllBytes(path);
        var error = new StringWriter();
        Assert.Equal(2, ConfigCommands.Run(value is null ? ["config", verb, "homeAssistant.enabled"]
            : ["config", verb, "homeAssistant.enabled", value], fixture.Root, new StringWriter(), error));
        Assert.Contains("ha off", error.ToString());
        Assert.Contains("ha on", error.ToString());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, AgentAuditLog.FolderName)));
    }

    [Fact]
    public void A_real_terminal_audit_failure_reports_the_saved_setting_without_claiming_a_completed_transition()
    {
        using var fixture = new Fixture();
        Configure(fixture.Root);
        var audit = Path.Combine(fixture.Root, AgentAuditLog.FolderName);
        var held = audit + ".held";
        var output = new OnSaveWriter(() => { Directory.Move(audit, held); File.WriteAllText(audit, "blocked"); });
        var error = new StringWriter();
        Assert.Equal(1, HaCommands.Run(["ha", "off"], fixture.Root, () => null, output, error, true, () => null));
        Assert.False(HostSettings.Load(fixture.Root).Settings.HomeAssistant.Enabled);
        Assert.Contains("setting was saved", error.ToString());
        Assert.Contains("terminal audit is incomplete", error.ToString());
        Assert.Equal("fixture-token-retained", HaTokenStore.Load(fixture.Root));
        File.Delete(audit);
        Directory.Move(held, audit);
        Assert.Equal("requested", Assert.Single(GateAuditProjection.Load(fixture.Root).Rows).Outcome);
    }

    [Theory]
    [InlineData("off"), InlineData("on")]
    public void Repeating_a_control_keeps_the_same_state_and_separately_correlates_each_audited_request(string state)
    {
        using var fixture = new Fixture();
        var path = Configure(fixture.Root, enabled: state == "off");
        Assert.Equal(0, Run(fixture.Root, "ha", state).Code);
        var before = File.ReadAllBytes(path);
        Assert.Equal(0, Run(fixture.Root, "ha", state).Code);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("fixture-token-retained", HaTokenStore.Load(fixture.Root));
        var (rows, complete) = GateAuditProjection.Load(fixture.Root);
        Assert.True(complete);
        Assert.Equal(["requested", $"configured-{state}", "requested", $"configured-{state}"], rows.Select(row => row.Outcome));
        Assert.Equal(rows[0].Request, rows[1].Request);
        Assert.Equal(rows[2].Request, rows[3].Request);
        Assert.NotEqual(rows[0].Request, rows[2].Request);
    }

    sealed class OnSaveWriter(Action saved) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith("Set homeAssistant.enabled.", StringComparison.Ordinal) == true) { saved(); }
        }
    }
}
