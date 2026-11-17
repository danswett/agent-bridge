using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_gates_commit_only_one_live_admission_even_for_the_same_request(bool sameRequest)
    {
        var a = Gate();
        var b = Gate();
        var results = await Task.WhenAll(Task.Run(() => a.CheckLaunch("a", "request", "hash")),
            Task.Run(() => b.CheckLaunch("a", sameRequest ? "request" : "another", "hash")));
        Assert.Single(results, r => r.Allowed);
        Assert.Single(results, r => sameRequest ? r.Duplicate : r.Reason?.StartsWith("live-limit:") == true);
    }
}
