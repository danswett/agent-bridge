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

    [Fact]
    public void A_fourth_launch_by_one_agent_in_an_hour_and_the_eleventh_on_the_machine_are_refused_rate_limited_with_retry_seconds()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new AgentLaunchGate(folder, () => now);
        Write(Config(DefaultLimits));
        for (var i = 0; i < 3; i++) { Assert.True(gate.CheckLaunch("a", $"r{i}", "hash").Allowed); }
        now = now.AddSeconds(10);
        var own = gate.CheckLaunch("a", "fourth", "hash");
        Assert.False(own.Allowed);
        Assert.Equal("rate-limited", own.Reason);
        Assert.Equal(3590, own.RetryAfterSeconds);
        for (var i = 0; i < 7; i++) { Assert.True(gate.CheckLaunch($"o{i}", "r", "hash").Allowed); }
        var machine = gate.CheckLaunch("another", "eleventh", "hash");
        Assert.False(machine.Allowed);
        Assert.Equal("rate-limited", machine.Reason);
        Assert.Equal(3590, machine.RetryAfterSeconds);
        now = now.AddSeconds(3590);
        Assert.True(gate.CheckLaunch("a", "after-expiry", "hash").Allowed);
    }

    [Fact]
    public void The_same_request_id_and_hash_spends_the_cap_once_and_a_different_hash_with_the_same_id_is_refused_and_counted()
    {
        var gate = new AgentLaunchGate(folder);
        var settings = DefaultLimits.Replace(":3", ":2").Replace("}", ""","launchAttemptsPerOriginPerHour":3}""");
        Write(Config(settings));
        Assert.True(gate.CheckLaunch("a", "r", "original").Allowed);
        for (var i = 0; i < 25; i++)
        {
            var repeat = gate.CheckLaunch("a", "r", "original");
            Assert.False(repeat.Allowed);
            Assert.True(repeat.Duplicate);
        }
        for (var i = 0; i < 2; i++)
        {
            var mismatch = gate.CheckLaunch("a", "r", "changed");
            Assert.False(mismatch.Allowed);
            Assert.False(mismatch.Duplicate);
            Assert.Equal("requestReused", mismatch.Reason);
        }
        var exhausted = gate.CheckLaunch("a", "r", "changed");
        Assert.Equal("rate-limited", exhausted.Reason);
        Assert.InRange(exhausted.RetryAfterSeconds, 1, 3600);
        Assert.True(gate.CheckLaunch("a", "r", "original").Duplicate);
        Assert.True(gate.CheckLaunch("other", "r", "changed").Allowed);
        Write(Config(settings.Replace("Hour\":3", "Hour\":20")));
        Assert.True(gate.CheckLaunch("a", "second", "hash").Allowed);
        Assert.Equal("rate-limited", gate.CheckLaunch("a", "third", "hash").Reason);
    }

    [Fact]
    public void Lowering_a_live_cap_keeps_its_hits_and_waits_until_enough_of_them_expire()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new AgentLaunchGate(folder, () => now);
        Write(Config(DefaultLimits));
        Assert.True(gate.CheckLaunch("a", "first", "hash").Allowed);
        now = now.AddMinutes(1);
        Assert.True(gate.CheckLaunch("a", "second", "hash").Allowed);
        now = now.AddMinutes(1);
        Write(Config(DefaultLimits.Replace(":3", ":1")));
        Assert.Equal(3540, gate.CheckLaunch("a", "third", "hash").RetryAfterSeconds);
        now = now.AddMinutes(59);
        Assert.True(gate.CheckLaunch("a", "after-expiry", "hash").Allowed);
        Write(Config(DefaultLimits.Replace(":3", ":0")));
        Assert.False(gate.CheckLaunch("other", "bad-policy", "hash").Allowed);
        Write(Config(DefaultLimits, """{"enabled":false}"""));
        Assert.False(gate.CheckLaunch("other", "disabled", "hash").Allowed);
    }

    [Fact]
    public void An_absent_attempt_setting_defaults_to_twenty_and_a_lower_setting_applies_without_restart()
    {
        var gate = new AgentLaunchGate(folder);
        Write(Config(DefaultLimits));
        Assert.True(gate.CheckLaunch("a", "r", "hash").Allowed);
        for (var i = 0; i < 19; i++)
        {
            Assert.Equal("requestReused", gate.CheckLaunch("a", "r", "changed").Reason);
        }
        Assert.Equal("rate-limited", gate.CheckLaunch("a", "r", "changed").Reason);
        Assert.True(gate.CheckLaunch("other", "r", "hash").Allowed);
        Write(Config(DefaultLimits.Replace("}", ""","launchAttemptsPerOriginPerHour":1}""")));
        Assert.Equal("rate-limited", gate.CheckLaunch("other", "r", "changed").Reason);
        Assert.True(gate.CheckLaunch("other", "r", "hash").Duplicate);
    }

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
    [InlineData("launchAttemptsPerOriginPerHour", "0")]
    [InlineData("launchAttemptsPerOriginPerHour", "-1")]
    [InlineData("launchAttemptsPerOriginPerHour", "21")]
    [InlineData("launchAttemptsPerOriginPerHour", "1.5")]
    [InlineData("launchAttemptsPerOriginPerHour", "\"20\"")]
    [InlineData("launchAttemptsPerOriginPerHour", "null")]
    [InlineData("launchAttemptsPerOriginPerHour", "true")]
    [InlineData("launchAttemptsPerOriginPerHour", "{}")]
    [InlineData("launchAttemptsPerOriginPerHour", "[]")]
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
        Assert.False(new AgentLaunchGate(folder).CheckLaunch("a", "r", "hash").Allowed);
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
