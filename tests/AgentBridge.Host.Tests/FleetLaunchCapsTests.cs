using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Core.Load;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Admission = System.Func<AgentBridge.Protocol.LaunchRequest, string, System.Func<System.Func<string?>, System.Threading.Tasks.Task<AgentBridge.Protocol.LaunchResult>>, System.Threading.Tasks.Task<AgentBridge.Protocol.LaunchResult>>;

public sealed class FleetLaunchCapsTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-fleet-caps-").FullName;
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero));
    int? queue = 7;
    bool? alive = true;
    DateTimeOffset? sampledAt;
    int starts;
    AgentAuditLog Audit => new(folder, time);
    public FleetLaunchCapsTests() => Configure();
    public void Dispose() => Directory.Delete(folder, recursive: true);
    void Configure(bool enabled = true, string limits = "") => File.WriteAllText(Path.Combine(folder, HostSettings.FileName),
        JsonSerializer.Serialize(new { agents = new { enabled, fleet = JsonNode.Parse("{" + limits + "}") } }));
    Admission CreateAdmission()
    {
        // Keep the tests-only H2 parent compilable before H3's implementation exists.
        var factory = typeof(SessionService).Assembly.GetType("AgentBridge.Host.FleetLaunchCaps")
            ?.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        Func<ProcessLoadReport?> load = () => new(sampledAt ?? time.GetUtcNow(), new(1, null, null, RunQueue: queue),
            new Dictionary<string, AgentBridge.Host.Core.Load.SessionLoad>());
        Func<string, bool?> live = _ => alive;
        return Assert.IsType<Admission>(factory.Invoke(null, [folder, Audit, load, live, time]));
    }
    Task<LaunchResult> Launch(Admission? admission = null, Func<Func<string?>, Task<LaunchResult>>? dispatch = null) =>
        (admission ?? CreateAdmission())(new() { Request = Guid.NewGuid().ToString("D") }, "fleet-launcher", dispatch ?? (check =>
        {
            if (check() is { } why) { return Task.FromResult(new LaunchResult(LaunchOutcome.Refused, why)); }
            starts++;
            return Task.FromResult(new LaunchResult(LaunchOutcome.Started) { Session = Guid.NewGuid().ToString("D") });
        }));
    void Audited(string reason)
    {
        var text = string.Join("\n", Directory.GetFiles(Path.Combine(folder, "audit"), "*.jsonl").SelectMany(File.ReadLines));
        Assert.Contains(reason, text);
        Assert.True(Audit.Verify().Intact);
        Assert.True(Audit.Verify(launch: true).Intact);
    }

    [Fact]
    public async Task The_ninth_hourly_launch_is_refused_across_restart_and_ended_sessions_free_live_slots()
    {
        alive = false;
        for (var i = 0; i < 8; i++) { Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome); }
        Assert.Contains("hourly-limit", (await Launch()).Reason);
        Assert.Equal(8, starts);
        Audited("hourly-limit");
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
    }

    [Fact]
    public async Task Six_live_fleet_sessions_refuse_the_next_and_unknown_pending_never_expires()
    {
        for (var i = 0; i < 6; i++) { Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome); }
        alive = null;
        time.Advance(TimeSpan.FromHours(1));
        Assert.Contains("live-limit", (await Launch()).Reason);
        Audited("live-limit");
        alive = false;
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Launch(dispatch: _ => Task.FromResult(new LaunchResult(LaunchOutcome.Unconfirmed)))).Outcome);
        Configure(limits: "\"liveSessions\":1");
        time.Advance(TimeSpan.FromHours(2));
        Assert.Contains("live-limit", (await Launch()).Reason);
    }

    [Fact]
    public async Task Owner_caps_and_a_queue_increase_are_checked_again_before_start()
    {
        Configure(limits: "\"launchesPerHour\":2");
        alive = false;
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
        Assert.Contains("hourly-limit", (await Launch()).Reason);
        time.Advance(TimeSpan.FromHours(1));
        var result = await Launch(dispatch: check =>
        {
            queue = 8;
            return Task.FromResult(new LaunchResult(LaunchOutcome.Refused, check()));
        });
        Assert.Contains("run-queue", result.Reason);
        Assert.Equal(2, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_crash_or_refusal_after_prestart_never_refunds_an_unknown_process(bool refused)
    {
        Configure(limits: "\"liveSessions\":1");
        if (refused)
        {
            await Launch(dispatch: check =>
            {
                Assert.Null(check());
                return Task.FromResult(new LaunchResult(LaunchOutcome.Refused));
            });
        }
        else { await Assert.ThrowsAsync<InvalidOperationException>(() => Launch(dispatch: _ => throw new InvalidOperationException("fixture crash"))); }
        time.Advance(TimeSpan.FromHours(2));
        alive = false;
        Assert.Contains("live-limit", (await Launch()).Reason);
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(null)]
    [InlineData(-1)]
    public async Task A_full_or_unreadable_run_queue_refuses_without_starting(int? value)
    {
        queue = value;
        Assert.Contains("run-queue", (await Launch()).Reason);
        Assert.Equal(0, starts);
        Audited("run-queue");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_kill_switch_is_read_again_inside_a_pending_dispatch(bool pending)
    {
        if (!pending) { Configure(enabled: false); }
        var result = await Launch(dispatch: check =>
        {
            Configure(enabled: false);
            return Task.FromResult(new LaunchResult(LaunchOutcome.Refused, check()));
        });
        Assert.Contains("agents.enabled", result.Reason);
        Assert.Equal(0, starts);
        Audited("agents.enabled");
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"agents":{"enabled":true,"fleet":{"launchesPerHour":0}}}""")]
    [InlineData("""{"agents":{"enabled":true,"fleet":{"liveSessions":"6"}}}""")]
    public async Task Unreadable_policy_never_falls_back_to_an_allowing_cap(string config)
    {
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), config);
        Assert.Contains("cap-state", (await Launch()).Reason);
        Assert.Equal(0, starts);
        Audited("cap-state");
    }

    [Fact]
    public async Task Broken_audit_evidence_is_never_treated_as_an_empty_hour()
    {
        await Launch();
        File.AppendAllText(Assert.Single(Directory.GetFiles(Path.Combine(folder, "audit"), "launches-*.jsonl")), "{broken\n");
        Assert.Contains("cap-state", (await Launch()).Reason);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task A_stale_run_queue_cannot_authorize_a_launch()
    {
        sampledAt = time.GetUtcNow().AddMinutes(-1);
        Assert.Contains("run-queue", (await Launch()).Reason);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Separate_admission_instances_cannot_take_the_same_last_live_slot()
    {
        Configure(limits: "\"liveSessions\":1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Launch(CreateAdmission(), async check =>
        {
            entered.SetResult();
            await release.Task;
            Assert.Null(check());
            return new(LaunchOutcome.Unconfirmed);
        });
        await entered.Task;
        var second = Launch(CreateAdmission());
        release.SetResult();
        await first;
        Assert.Contains("live-limit", (await second).Reason);
    }

    [Fact]
    public async Task An_agent_cannot_change_caps_through_host_settings()
    {
        var before = File.ReadAllText(Path.Combine(folder, HostSettings.FileName));
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")));
        var channel = new Channel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, HostOps.Settings,
            JsonSerializer.SerializeToElement(new { agents = new { fleet = new { launchesPerHour = 99 } } })));
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, HostSettings.FileName)));
    }

    sealed class Channel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "Fixture";
        public string PeerKind => "agent";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
