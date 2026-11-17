using System.Text.Json;
using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
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
    public void A_temporary_raise_lapses_after_four_hours_and_the_refusal_names_the_limit_in_force()
    {
        var clock = new FakeTimeProvider();
        var start = clock.GetUtcNow();
        var baseline = DefaultLimits.Replace(":3", ":1");
        var temporary = JsonSerializer.Serialize(new
        { startedAt = start, expiresAt = start.AddHours(4), limits = new { launchesPerOriginPerHour = 3 } });
        Write(Config(baseline.Replace("}", ",\"temporaryRaise\":" + temporary + "}")));
        var gate = new AgentLaunchGate(folder, () => clock.GetUtcNow(), clock);
        Assert.Equal(3, gate.CheckPolicy().Limits?.LaunchesPerOriginPerHour);
        clock.Advance(TimeSpan.FromHours(4) - TimeSpan.FromSeconds(1));
        Assert.True(gate.CheckLaunch("a", Guid.NewGuid().ToString("D"), "hash").Allowed);
        Assert.True(gate.CheckLaunch("a", Guid.NewGuid().ToString("D"), "hash").Allowed);
        clock.Advance(TimeSpan.FromSeconds(1));

        var refusal = gate.CheckLaunch("a", Guid.NewGuid().ToString("D"), "hash");

        Assert.False(refusal.Allowed);
        Assert.Equal("rate-limited", refusal.Reason);
        Assert.Equal(1, refusal.Limits?.LaunchesPerOriginPerHour);
        var row = JsonSerializer.Deserialize<JsonElement>(File.ReadAllLines(
            Assert.Single(Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl")))[^1]);
        Assert.Contains("launches 1 per agent", row.GetProperty("reason").GetString());
        Assert.Equal(1, new AgentLaunchGate(folder, () => clock.GetUtcNow()).CheckPolicy().Limits?.LaunchesPerOriginPerHour);
    }

    [Theory]
    [InlineData("launchesPerOriginPerHour", 3)]
    [InlineData("launchesPerMachinePerHour", 10)]
    [InlineData("launchAttemptsPerOriginPerHour", 20)]
    [InlineData("liveAgentLaunched", 6)]
    [InlineData("agentLaunchedIdleHours", 2)]
    public void Each_launch_limit_can_be_temporarily_restored_to_its_ceiling(string key, int ceiling)
    {
        var start = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.FromHours(-8));
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(DefaultLimits)!;
        values[key] = JsonSerializer.SerializeToElement(1);
        values["temporaryRaise"] = JsonSerializer.SerializeToElement(new
        { startedAt = start, expiresAt = start.AddHours(1), limits = new Dictionary<string, int> { [key] = ceiling } });
        Write(Config(JsonSerializer.Serialize(values)));
        int? Value(DateTimeOffset now) => JsonSerializer.SerializeToElement(
            new AgentLaunchGate(folder, () => now).CheckPolicy().Limits, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty(key).GetInt32();

        Assert.Equal(ceiling, Value(start));
        Assert.Equal(1, Value(start.AddHours(1)));
        Assert.Equal(1, Value(start.AddSeconds(-1)));
    }

    [Theory]
    [InlineData("""{"startedAt":"2026-11-16T12:00:00-08:00","expiresAt":"2026-11-16T16:00:01-08:00","limits":{"launchesPerOriginPerHour":3}}""")]
    [InlineData("""{"startedAt":"2026-11-16T12:00:00-08:00","expiresAt":"2026-11-16T12:00:00-08:00","limits":{"launchesPerOriginPerHour":3}}""")]
    [InlineData("""{"expiresAt":"2026-11-16T16:00:00-08:00","limits":{"launchesPerOriginPerHour":3}}""")]
    [InlineData("""{"startedAt":false,"expiresAt":42,"limits":{}}""")]
    [InlineData("""{"startedAt":"2026-11-16T12:00:00-08:00","expiresAt":"2026-11-16T16:00:00-08:00","limits":{"launchesPerOriginPerHour":4}}""")]
    [InlineData("""{"startedAt":"2026-11-16T12:00:00-08:00","expiresAt":"2026-11-16T16:00:00-08:00","limits":{"launchesPerOriginPerHour":0}}""")]
    [InlineData("""{"startedAt":"2026-11-16T12:00:00-08:00","expiresAt":"2026-11-16T16:00:00-08:00","limits":{"launchesPerOriginPerHour":1.5}}""")]
    [InlineData("""{"startedAt":"2026-11-16T12:00:00-08:00","expiresAt":"2026-11-16T16:00:00-08:00","limits":{"unknown":3}}""")]
    [InlineData("null")]
    public void An_invalid_temporary_raise_refuses_launch_instead_of_granting_a_fallback(string temporary)
    {
        Write(Config(DefaultLimits.Replace("}", ",\"temporaryRaise\":" + temporary + "}")));
        var result = new AgentLaunchGate(folder).CheckPolicy();
        Assert.False(result.Allowed);
        Assert.Contains("agents.limits.temporaryRaise", result.Reason);
    }

    [Fact]
    public async Task An_agent_session_cannot_change_a_launch_limit()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(AgentLaunchGate).Assembly.Location, "config", "set",
            "agents.limits.launchesPerOriginPerHour", "1", "--profile", ".." })
        { start.ArgumentList.Add(arg); }
        start.Environment["AGENT_BRIDGE_SESSION"] = "fixture-agent-session";
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(4, process.ExitCode);
        Assert.Empty(await output);
        Assert.Contains("person", await error);
    }

    [Fact]
    public void A_launch_over_the_hourly_cap_returns_the_named_refusal_and_an_audit_line()
    {
        Write(Config(DefaultLimits));
        Directory.CreateDirectory(Path.Combine(folder, AgentAuditLog.FolderName));
        var gate = new AgentLaunchGate(folder);
        const string principal = "agent:machine/parent";
        for (var i = 0; i < 3; i++) { Assert.True(gate.CheckLaunch(principal, Guid.NewGuid().ToString("D"), "hash").Allowed); }
        var request = Guid.NewGuid().ToString("D");

        var refusal = gate.CheckLaunch(principal, request, "hash");

        Assert.False(refusal.Allowed);
        Assert.Equal("rate-limited", refusal.Reason);
        Assert.InRange(refusal.RetryAfterSeconds, 1, 3600);
        var row = JsonSerializer.Deserialize<JsonElement>(Assert.Single(File.ReadAllLines(
            Assert.Single(Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl"))).Skip(1)));
        Assert.Equal(principal, row.GetProperty("principal").GetString());
        Assert.Equal(request, row.GetProperty("request").GetString());
        Assert.Equal("refused", row.GetProperty("outcome").GetString());
        Assert.Contains("rate-limited", row.GetProperty("reason").GetString());
    }

    [Fact]
    public void Rate_limited_launch_floods_have_bounded_audits_and_counted_minute_summaries()
    {
        Write(Config(DefaultLimits));
        var clock = new FakeTimeProvider();
        var gate = new AgentLaunchGate(folder, () => clock.GetUtcNow(), clock);
        for (var i = 0; i < 3; i++) { Assert.True(gate.CheckLaunch("agent:machine/parent", Guid.NewGuid().ToString("D"), "hash").Allowed); }
        void Refused() => Assert.Equal("rate-limited", gate.CheckLaunch("agent:machine/parent", Guid.NewGuid().ToString("D"), "hash").Reason);
        string[] Rows() => File.ReadAllLines(Assert.Single(Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl"))).Skip(1).ToArray();

        for (var i = 0; i < 100; i++) { Refused(); }

        Assert.Equal(60, Rows().Length);
        clock.Advance(TimeSpan.FromSeconds(59));
        Refused();
        Assert.Equal(60, Rows().Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        Refused();
        Assert.Equal(61, Rows().Length);
        Assert.Equal("42 additional launch or stop attempts were refused",
            JsonSerializer.Deserialize<JsonElement>(Rows()[^1]).GetProperty("reason").GetString());
    }

    [Fact]
    public void Attempt_cap_audits_include_the_attempt_limit_and_never_copy_an_invalid_request()
    {
        Write(Config(DefaultLimits.Replace("}", ""","launchAttemptsPerOriginPerHour":1}""")));
        var gate = new AgentLaunchGate(folder);
        Assert.True(gate.CheckLaunch("agent:machine/parent", Guid.NewGuid().ToString("D"), "hash").Allowed);

        Assert.Equal("rate-limited", gate.CheckLaunch("agent:machine/parent", @"C:\private\request", "hash").Reason);

        var row = JsonSerializer.Deserialize<JsonElement>(File.ReadAllLines(
            Assert.Single(Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl")))[^1]);
        Assert.Contains("attempts 1 per agent", row.GetProperty("reason").GetString());
        Assert.True(Guid.TryParse(row.GetProperty("request").GetString(), out _));
        Assert.DoesNotContain("private", row.GetRawText());
    }

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
