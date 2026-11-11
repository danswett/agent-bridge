using System.Text.Json;
using AgentBridge.Host;
using Xunit;

public sealed class AgentLaunchGateTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-launch-policy-").FullName;
    const string DefaultLimits = """
        {"launchesPerOriginPerHour":3,"launchesPerMachinePerHour":10,
         "liveAgentLaunched":6,"agentLaunchedIdleHours":2}
        """;

    public void Dispose() => Directory.Delete(folder, true);

    void Write(string text) => File.WriteAllText(Path.Combine(folder, HostSettings.FileName), text);
    static string Config(string limits, string launch = """{"enabled":true}""") =>
        """{"agents":{"enabled":true,"launch":""" + launch + ""","limits":""" + limits + "}}";

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"agents":{"enabled":true}}""")]
    [InlineData("""{"agents":{"enabled":true,"launch":{"enabled":false}}}""")]
    public void Launch_is_off_by_default_and_the_policy_gate_says_why(string text)
    {
        Write(text);
        var result = new AgentLaunchGate(folder).CheckPolicy();
        Assert.False(result.Allowed);
        Assert.Contains("agents.launch.enabled", result.Reason);
        Assert.Null(result.Limits);
    }

    [Fact]
    public void Disabled_installations_keep_the_existing_hourly_defaults_and_explicit_lower_settings()
    {
        Assert.False(new AgentLaunchGate(folder).CheckPolicy().Allowed);
        var defaults = HostSettings.Load(folder).Settings.Agents.Limits;
        Assert.Equal((3, 10), (defaults.LaunchesPerOriginPerHour, defaults.LaunchesPerMachinePerHour));
        Write(Config("""{"launchesPerOriginPerHour":1,"launchesPerMachinePerHour":4}""", """{"enabled":false}"""));
        var settings = HostSettings.Load(folder).Settings;
        Assert.Empty(settings.AgentsProblems);
        Assert.Equal((1, 4), (settings.Agents.Limits.LaunchesPerOriginPerHour, settings.Agents.Limits.LaunchesPerMachinePerHour));
        Assert.False(new AgentLaunchGate(folder).CheckPolicy().Allowed);
    }

    [Fact]
    public void Approved_defaults_are_loaded_and_the_same_gate_reads_limit_changes_and_disable_at_once()
    {
        var gate = new AgentLaunchGate(folder);
        Write(Config(DefaultLimits));
        var defaults = gate.CheckPolicy();
        Assert.True(defaults.Allowed, defaults.Reason);
        Assert.Equal((3, 10, 6, 2), (defaults.Limits!.LaunchesPerOriginPerHour,
            defaults.Limits.LaunchesPerMachinePerHour, defaults.Limits.LiveAgentLaunched,
            defaults.Limits.AgentLaunchedIdleHours));
        Write(Config(DefaultLimits.Replace(":3", ":1").Replace(":10", ":4").Replace(":6", ":2").Replace(":2}", ":1}")));
        var changed = gate.CheckPolicy();
        Assert.True(changed.Allowed, changed.Reason);
        Assert.Equal((1, 4, 2, 1), (changed.Limits!.LaunchesPerOriginPerHour, changed.Limits.LaunchesPerMachinePerHour,
            changed.Limits.LiveAgentLaunched, changed.Limits.AgentLaunchedIdleHours));
        Write(Config(DefaultLimits.Replace(":3", ":0")));
        Assert.False(gate.CheckPolicy().Allowed);
        Write(Config(DefaultLimits));
        Assert.True(gate.CheckPolicy().Allowed);
        Write(Config(DefaultLimits, """{"enabled":false}"""));
        Assert.False(gate.CheckPolicy().Allowed);
    }

    [Theory]
    [InlineData("launchesPerOriginPerHour", "0")]
    [InlineData("launchesPerOriginPerHour", "4")]
    [InlineData("launchesPerMachinePerHour", "-1")]
    [InlineData("launchesPerMachinePerHour", "11")]
    [InlineData("liveAgentLaunched", "7")]
    [InlineData("liveAgentLaunched", "\"6\"")]
    [InlineData("agentLaunchedIdleHours", "3")]
    [InlineData("agentLaunchedIdleHours", "1.5")]
    [InlineData("agentLaunchedIdleHours", "null")]
    [InlineData("agentLaunchedIdleHours", "true")]
    [InlineData("liveAgentLaunched", null)]
    [InlineData("launchesPerOriginPerHour", null)]
    [InlineData("launchesPerMachinePerHour", null)]
    [InlineData("agentLaunchedIdleHours", null)]
    public void A_bad_or_missing_limit_is_reported_at_load_and_never_admitted(string key, string? value)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(DefaultLimits)!;
        if (value is null) { values.Remove(key); }
        else { values[key] = JsonSerializer.Deserialize<JsonElement>(value); }
        Write(Config(JsonSerializer.Serialize(values)));
        var settings = HostSettings.Load(folder).Settings;
        Assert.Contains(settings.AgentsProblems, p => p.Contains($"agents.limits.{key}"));
        var result = new AgentLaunchGate(folder).CheckPolicy();
        Assert.False(result.Allowed);
        Assert.Contains($"agents.limits.{key}", result.Reason);
        Assert.Null(result.Limits);
    }

    [Theory]
    [InlineData("""{"agents":{"enabled":true,"launch":{"enabled":true}}}""", "agents.limits")]
    [InlineData("""{"agents":{"enabled":true,"launch":{"enabled":true},"limits":null}}""", "agents.limits")]
    [InlineData("""{"agents":{"enabled":true,"launch":{"enabled":"true"}}}""", "agents.launch.enabled")]
    [InlineData("""{"agents":{"enabled":true,"launch":true}}""", "agents.launch")]
    [InlineData("""{"agents":null}""", "agents.launch.enabled")]
    [InlineData("{", "config.json")]
    [InlineData("""{"version":2}""", "version")]
    public void Unreadable_policy_never_grants_launch_and_includes_a_reason(string text, string reason)
    {
        Write(text);
        var result = new AgentLaunchGate(folder).CheckPolicy();
        Assert.False(result.Allowed);
        Assert.Contains(reason, result.Reason);
        Assert.Null(result.Limits);
    }

    [Fact]
    public void The_master_switch_and_an_unreadable_file_each_refuse_a_previously_valid_policy()
    {
        var gate = new AgentLaunchGate(folder);
        Write(Config(DefaultLimits).Replace("\"agents\":{\"enabled\":true", "\"agents\":{\"enabled\":false"));
        Assert.Contains("agents.enabled", gate.CheckPolicy().Reason);
        File.Delete(Path.Combine(folder, HostSettings.FileName));
        Directory.CreateDirectory(Path.Combine(folder, HostSettings.FileName));
        var unreadable = gate.CheckPolicy();
        Assert.False(unreadable.Allowed);
        Assert.Contains("could not be read", unreadable.Reason);
    }
}
