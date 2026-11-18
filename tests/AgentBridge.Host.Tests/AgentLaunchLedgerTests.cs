using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json.Nodes;
using Xunit;

public sealed class AgentLaunchLedgerTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-launch-ledger-").FullName;
    readonly FakeTimeProvider time = new();
    string State => Path.Combine(folder, "launch-admissions", "state.json");
    AgentLaunchGate Gate() => new(folder, () => time.GetUtcNow(), time);
    public AgentLaunchLedgerTests() => File.WriteAllText(Path.Combine(folder, HostSettings.FileName), """
        {"agents":{"enabled":true,"launch":{"enabled":true},"limits":{
          "launchesPerOriginPerHour":2,"launchesPerMachinePerHour":2,
          "launchAttemptsPerOriginPerHour":3,"liveAgentLaunched":1,"agentLaunchedIdleHours":2}}}
        """);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void Restart_preserves_request_binding_live_capacity_and_session_closure()
    {
        Assert.True(Gate().CheckLaunch("a", "first", "hash").Allowed);
        Gate().LaunchStarted("a", "first", "session");
        Assert.True(Gate().CheckLaunch("a", "first", "hash").Duplicate);
        time.Advance(TimeSpan.FromHours(2));
        Assert.StartsWith("live-limit:", Gate().CheckLaunch("other", "second", "hash").Reason);
        Assert.False(Gate().SessionEnded("unknown"));
        Assert.True(Gate().SessionEnded("session"));
        Assert.False(Gate().SessionEnded("session"));
        Assert.True(Gate().CheckLaunch("other", "after-exit", "hash").Allowed);
    }

    [Fact]
    public void Proven_completion_keeps_hourly_charges_and_refund_removes_only_its_own_charge_after_restart()
    {
        Assert.True(Gate().CheckLaunch("a", "first", "hash").Allowed);
        Assert.True(Gate().LaunchFinished("a", "first"));
        Assert.True(Gate().CheckLaunch("a", "refused", "hash").Allowed);
        Assert.True(Gate().LaunchRefused("a", "refused"));
        Assert.False(Gate().LaunchRefused("a", "refused"));
        Assert.True(Gate().CheckLaunch("a", "refused", "hash").Duplicate);
        Assert.True(Gate().CheckLaunch("other", "second", "hash").Allowed);
        Assert.True(Gate().LaunchFinished("other", "second"));
        Assert.Equal("rate-limited", Gate().CheckLaunch("another", "third", "hash").Reason);
    }

    [Fact]
    public void Changed_request_attempts_remain_counted_after_restart()
    {
        Assert.True(Gate().CheckLaunch("a", "request", "original").Allowed);
        Assert.Equal("requestReused", Gate().CheckLaunch("a", "request", "changed").Reason);
        Assert.Equal("requestReused", Gate().CheckLaunch("a", "request", "changed").Reason);
        Assert.Equal("rate-limited", Gate().CheckLaunch("a", "request", "changed").Reason);
        Assert.True(Gate().CheckLaunch("a", "request", "original").Duplicate);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(150)]
    public void Rollback_cannot_resurrect_an_expired_debit_or_persist_an_unrestorable_live_ledger(int rollbackMinutes)
    {
        var now = time.GetUtcNow();
        AgentLaunchGate Current() => new(folder, () => now, time);
        Assert.True(Current().CheckLaunch("a", "first", "hash").Allowed);
        Current().LaunchStarted("a", "first", "session");
        now += TimeSpan.FromHours(2);
        Assert.True(Current().CheckLaunch("a", "first", "hash").Duplicate);
        now -= TimeSpan.FromMinutes(rollbackMinutes);
        Assert.True(Current().CheckLaunch("a", "first", "hash").Duplicate);
        Assert.True(Current().CheckLaunch("a", "first", "hash").Duplicate);
        Assert.StartsWith("live-limit:", Current().CheckLaunch("b", "held", "hash").Reason);
        Assert.True(Current().SessionEnded("session"));
        Assert.False(Current().SessionEnded("session"));
        Assert.True(Current().CheckLaunch("b", "after-exit", "hash").Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_damaged_or_missing_committed_ledger_fails_closed_instead_of_resetting_caps(bool missing)
    {
        Assert.True(Gate().CheckLaunch("a", "first", "hash").Allowed);
        Directory.CreateDirectory(Path.GetDirectoryName(State)!);
        if (missing) { File.Delete(State); }
        else { File.WriteAllText(State, "{"); }
        var refusal = Gate().CheckLaunch("other", "second", "hash");
        Assert.False(refusal.Allowed);
        Assert.Contains("needs-person", refusal.Reason);
        if (!missing) { Assert.Equal("{", File.ReadAllText(State)); }
    }

    [Fact]
    public void Removing_the_whole_initialized_ledger_cannot_reset_request_or_hourly_admission()
    {
        Assert.True(Gate().CheckLaunch("a", "first", "hash").Allowed);
        Directory.Delete(Path.GetDirectoryName(State)!, true);
        var refusal = Gate().CheckLaunch("a", "first", "hash");
        Assert.False(refusal.Allowed);
        Assert.StartsWith("capRebuilding:", refusal.Reason);
        Assert.False(File.Exists(State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UUID_aliases_share_one_durable_request_charge_and_lifecycle_binding(bool uppercaseFirst)
    {
        const string id = "12345678-9abc-4def-8abc-0123456789ab";
        Assert.True(Gate().CheckLaunch("a", uppercaseFirst ? id.ToUpperInvariant() : id, "hash").Allowed);
        var legacy = JsonNode.Parse(File.ReadAllText(State))!;
        legacy["requests"]![0]!["request"] = id.ToUpperInvariant();
        legacy["requests"]![0]!.AsObject().Remove("charge");
        File.WriteAllText(State, legacy.ToJsonString());
        Assert.True(Gate().CheckLaunch("a", uppercaseFirst ? id : id.ToUpperInvariant(), "hash").Duplicate);
        Gate().LaunchStarted("a", id.ToUpperInvariant(), "session");
        Assert.True(Gate().LaunchFinished("a", id));
        Assert.True(Gate().CheckLaunch("a", "second", "hash").Allowed);
        Assert.True(Gate().LaunchFinished("a", "second"));
        Assert.Equal("rate-limited", Gate().CheckLaunch("b", "third", "hash").Reason);
        Assert.Equal(id, JsonNode.Parse(File.ReadAllText(State))!["requests"]![0]!["request"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("origins")]
    [InlineData("machine")]
    [InlineData("requests")]
    [InlineData("duplicate-hit")]
    [InlineData("legacy-completed")]
    public void Restored_hourly_debits_must_match_the_admitted_requests_even_after_live_closure(string fault)
    {
        if (fault == "origins")
        {
            var policy = Path.Combine(folder, HostSettings.FileName);
            File.WriteAllText(policy, File.ReadAllText(policy).Replace("\"launchesPerMachinePerHour\":2", "\"launchesPerMachinePerHour\":10"));
        }
        Assert.True(Gate().CheckLaunch("a", "first", "hash").Allowed);
        Assert.True(Gate().LaunchFinished("a", "first"));
        Assert.True(Gate().CheckLaunch("a", "second", "hash").Allowed);
        Assert.True(Gate().LaunchFinished("a", "second"));
        var state = JsonNode.Parse(File.ReadAllText(State))!;
        if (fault == "duplicate-hit") { state["machine"]!.AsArray().Add(state["machine"]![0]!.DeepClone()); }
        else if (fault == "legacy-completed")
        {
            foreach (var request in state["requests"]!.AsArray()) { request!.AsObject().Remove("charge"); }
            foreach (var hit in state["machine"]!.AsArray().Concat(state["origins"]!.AsArray().SelectMany(w => w!["hits"]!.AsArray())))
            { hit!["charge"] = null; }
        }
        else { state[fault]!.AsArray().Clear(); }
        var damaged = state.ToJsonString();
        File.WriteAllText(State, damaged);
        var refusal = Gate().CheckLaunch(fault == "origins" ? "a" : "b", "third", "hash");
        Assert.False(refusal.Allowed);
        Assert.StartsWith("capRebuilding:", refusal.Reason);
        Assert.Equal(damaged, File.ReadAllText(State));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("null-state")]
    [InlineData("null-requests")]
    public void Unsupported_or_null_restored_state_is_a_cap_rebuilding_refusal(string fault)
    {
        Assert.True(Gate().CheckLaunch("a", "first", "hash").Allowed);
        var state = JsonNode.Parse(File.ReadAllText(State))!;
        if (fault == "version") { state["v"] = 2; }
        if (fault == "null-requests") { state["requests"] = null; }
        File.WriteAllText(State, fault == "null-state" ? "null" : state.ToJsonString());
        var refusal = Gate().CheckLaunch("b", "second", "hash");
        Assert.False(refusal.Allowed);
        Assert.StartsWith("capRebuilding:", refusal.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_gates_commit_only_one_live_admission_even_for_the_same_request(bool sameRequest)
    {
        var a = Gate();
        var b = Gate();
        var request = Guid.NewGuid().ToString("D");
        var results = await Task.WhenAll(Task.Run(() => a.CheckLaunch("a", request.ToUpperInvariant(), "hash")),
            Task.Run(() => b.CheckLaunch("a", sameRequest ? request : "another", "hash")));
        Assert.Single(results, r => r.Allowed);
        Assert.Single(results, r => sameRequest ? r.Duplicate : r.Reason?.StartsWith("live-limit:") == true);
    }

    [Fact]
    public void An_unresolved_launch_after_ten_minutes_is_held_and_surfaces_needs_person()
    {
        Assert.True(Gate().CheckLaunch("a", "unknown", "hash").Allowed);
        time.Advance(TimeSpan.FromMinutes(11));
        var refusal = Gate().CheckLaunch("b", "another", "hash");
        Assert.False(refusal.Allowed);
        Assert.Contains("needs-person", refusal.Reason);
    }

    [Fact]
    public void Missing_ledger_never_treats_existing_agent_launch_evidence_as_empty_capacity()
    {
        Assert.True(new AgentAuditLog(folder).Append(new("local", Guid.NewGuid().ToString(),
            "agent:machine/parent", [], "session.launch", "local", "reserved")));
        var refusal = Gate().CheckLaunch("other", "new", "hash");
        Assert.False(refusal.Allowed);
        Assert.Contains("needs-person", refusal.Reason);
    }
}
