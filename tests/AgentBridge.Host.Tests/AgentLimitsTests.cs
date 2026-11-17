using System.Text.Json;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentLimitsTests
{
    DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    AgentLimits Make() => new(() => now);

    [Fact]
    public void The_seventh_live_agent_started_session_is_refused()
    {
        WithGate((gate, folder) =>
        {
            for (var i = 0; i < 6; i++)
            {
                Assert.True(gate.CheckLaunch($"o{i}", $"r{i}", "hash").Allowed);
                gate.LaunchStarted($"o{i}", $"r{i}", $"s{i}");
            }
            var request = Guid.NewGuid().ToString("D");
            var refused = gate.CheckLaunch("other", request, "hash");
            Assert.False(refused.Allowed);
            Assert.Contains("live-limit", refused.Reason);
            Assert.Contains("6", refused.Reason);
            var row = JsonSerializer.Deserialize<JsonElement>(File.ReadAllLines(
                Assert.Single(Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl")))[^1]);
            Assert.Equal(request, row.GetProperty("request").GetString());
            Assert.Equal("refused", row.GetProperty("outcome").GetString());
            Assert.Contains("live-limit", row.GetProperty("reason").GetString());
            gate.SessionEnded("s0");
            Assert.True(gate.CheckLaunch("other", "after-exit", "hash").Allowed);
        });
    }

    [Fact]
    public void An_agent_started_session_idle_two_hours_is_ended_unless_a_person_typed_into_it()
    {
        WithGate((gate, _) =>
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.True(gate.CheckLaunch($"o{i}", $"r{i}", "hash").Allowed);
                gate.LaunchStarted($"o{i}", $"r{i}", $"s{i}");
            }
            gate.ObserveSession("s0", now, personTyped: false);
            gate.ObserveSession("s1", now, personTyped: true);
            gate.ObserveSession("s1", now, personTyped: false);
            gate.ObserveSession("s2", null, personTyped: false);
            gate.ObserveSession("person-started", now, personTyped: false);
            now = now.AddHours(2).AddTicks(-1);
            Assert.Empty(gate.IdleSessions());
            now = now.AddTicks(1);
            Assert.Equal(["s0"], gate.IdleSessions());
            gate.ObserveSession("s0", null, personTyped: false);
            Assert.Empty(gate.IdleSessions());
            gate.ObserveSession("s0", now, personTyped: false);
            now = now.AddHours(2);
            Assert.Equal(["s0"], gate.IdleSessions());
            gate.SessionEnded("s0");
            Assert.Empty(gate.IdleSessions());
        });
    }

    [Fact]
    public void Pending_launches_reserve_live_slots_and_only_their_own_completion_releases_them()
    {
        WithGate((gate, _) =>
        {
            Assert.True(gate.CheckLaunch("a", "r", "hash").Allowed);
            Assert.True(gate.CheckLaunch("a", "r", "hash").Duplicate);
            Assert.Equal("requestReused", gate.CheckLaunch("a", "r", "changed").Reason);
            gate.LaunchFinished("another", "r");
            Assert.Contains("live-limit", gate.CheckLaunch("b", "blocked", "hash").Reason);
            Assert.Throws<InvalidOperationException>(() => gate.LaunchStarted("another", "r", "not-owned"));
            gate.LaunchFinished("a", "r");
            Assert.True(gate.CheckLaunch("b", "after-failure", "hash").Allowed);
            gate.LaunchStarted("b", "after-failure", "session");
            Assert.Throws<InvalidOperationException>(() => gate.LaunchStarted("b", "after-failure", "different"));
            Assert.Contains("live-limit", gate.CheckLaunch("c", "still-live", "hash").Reason);
            gate.SessionEnded("session");
            Assert.True(gate.CheckLaunch("c", "after-exit", "hash").Allowed);
        }, live: 1);
    }

    [Fact]
    public void Concurrent_launches_cannot_reserve_more_than_the_live_limit()
    {
        var limits = Make();
        var results = new AgentLimitVerdict[24];
        Parallel.For(0, results.Length, i =>
            results[i] = limits.ReserveLaunch($"o{i}", $"r{i}", "hash", AgentLaunchLimits.Default));
        Assert.Equal(6, results.Count(r => r.Allowed));
        Assert.Equal(18, results.Count(r => r.Reason == "live-limit"));
    }

    [Fact]
    public void Live_and_idle_limits_are_reread_and_an_unwritable_live_refusal_fails_closed()
    {
        WithGate((gate, folder) =>
        {
            Assert.True(gate.CheckLaunch("a", "r", "hash").Allowed);
            gate.LaunchStarted("a", "r", "session");
            gate.ObserveSession("session", now, personTyped: false);
            now = now.AddHours(1);
            Assert.Empty(gate.IdleSessions());
            WritePolicy(folder, live: 1, idle: 1);
            Assert.Equal(["session"], gate.IdleSessions());
            File.WriteAllText(Path.Combine(folder, AgentAuditLog.FolderName), "not a directory");
            var refused = gate.CheckLaunch("b", "blocked", "hash");
            Assert.False(refused.Allowed);
            Assert.Contains("auditUnwritable", refused.Reason);
        });
    }

    void WithGate(Action<AgentLaunchGate, string> test, int live = 6)
    {
        var folder = Directory.CreateTempSubdirectory("ab-live-limits-").FullName;
        try
        {
            WritePolicy(folder, live);
            test(new AgentLaunchGate(folder, () => now), folder);
        }
        finally { Directory.Delete(folder, true); }
    }

    static void WritePolicy(string folder, int live = 6, int idle = 2) =>
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName),
            JsonSerializer.Serialize(new
            {
                agents = new
                {
                    enabled = true, launch = new { enabled = true },
                    limits = new { launchesPerOriginPerHour = 3, launchesPerMachinePerHour = 10,
                        liveAgentLaunched = live, agentLaunchedIdleHours = idle },
                },
            }));

    [Fact]
    public void The_seventh_message_in_a_minute_between_one_pair_is_refused_with_the_seconds_to_wait_and_admitted_after_it()
    {
        var limits = Make();
        for (var i = 0; i < 6; i++) { Assert.True(limits.CheckMessage("a", "b").Allowed); now = now.AddSeconds(5); }

        var refused = limits.CheckMessage("a", "b");
        Assert.False(refused.Allowed);
        Assert.Equal("rate-limited", refused.Reason);
        Assert.Equal(30, refused.RetryAfterSeconds);

        now = now.AddSeconds(30);
        Assert.True(limits.CheckMessage("a", "b").Allowed);
    }

    [Fact]
    public void Twelve_messages_a_minute_to_one_target_from_different_origins_is_the_most_and_a_refusal_takes_no_slot()
    {
        var limits = Make();
        for (var i = 0; i < 7; i++) { Assert.True(limits.CheckMessage($"o{i}", "t").Allowed); }
        now = now.AddSeconds(30);
        for (var i = 0; i < 5; i++) { Assert.True(limits.CheckMessage("fresh", "t").Allowed); }
        Assert.False(limits.CheckMessage("fresh", "t").Allowed);

        // The seven early messages have left the target's window; the refused one would still sit
        // in the pair's if it had been recorded, so the pair has exactly one slot left, not none.
        now = now.AddSeconds(30);
        Assert.True(limits.CheckMessage("fresh", "t").Allowed);
        Assert.False(limits.CheckMessage("fresh", "t").Allowed);
    }

    [Fact]
    public void A_clock_stepped_back_never_holds_a_window_for_longer_than_one_span()
    {
        var limits = Make();
        for (var i = 0; i < 6; i++) { Assert.True(limits.CheckMessage("a", "b").Allowed); }
        now = now.AddHours(-1);

        var refused = limits.CheckMessage("a", "b");
        Assert.False(refused.Allowed);
        Assert.InRange(refused.RetryAfterSeconds, 1, 60);
        now = now.AddMinutes(1);
        Assert.True(limits.CheckMessage("a", "b").Allowed);
    }

    [Fact]
    public void Launches_stop_at_three_an_hour_for_an_origin_and_ten_an_hour_for_the_machine()
    {
        var limits = Make();
        for (var i = 0; i < 3; i++) { Assert.True(limits.CheckLaunch("a").Allowed); }
        var refused = limits.CheckLaunch("a");
        Assert.False(refused.Allowed);
        Assert.Equal(3600, refused.RetryAfterSeconds);

        for (var i = 0; i < 7; i++) { Assert.True(limits.CheckLaunch($"o{i}").Allowed); }
        Assert.False(limits.CheckLaunch("another").Allowed);

        now = now.AddHours(1);
        Assert.True(limits.CheckLaunch("a").Allowed);
    }

    [Fact]
    public void A_request_is_remembered_for_the_whole_hour_and_the_clock_stepping_back_does_not_clear_it()
    {
        var limits = Make();
        var settings = AgentLaunchLimits.Default;
        for (var i = 0; i < 3; i++) { Assert.True(limits.CheckLaunch("a", $"r{i}", "hash", settings).Allowed); }
        now = now.AddMinutes(16);
        Assert.True(limits.CheckLaunch("a", "r0", "hash", settings).Duplicate);
        now = now.AddHours(-2);
        Assert.True(limits.CheckLaunch("a", "r0", "hash", settings).Duplicate);
        now = now.AddHours(1);
        Assert.True(limits.CheckLaunch("a", "after-expiry", "hash", settings).Allowed);
        Assert.True(limits.CheckLaunch("a", "r0", "hash", settings).Allowed);
        Assert.True(Make().CheckLaunch("a", "r", "hash", settings).Allowed);
    }

    [Fact]
    public void Concurrent_distinct_requests_cannot_both_take_the_last_slot_and_identical_requests_admit_once()
    {
        var limits = Make();
        var settings = AgentLaunchLimits.Default with { LaunchesPerMachinePerHour = 1 };
        var results = new AgentLimitVerdict[32];
        Parallel.For(0, results.Length, i => results[i] = limits.CheckLaunch($"o{i}", $"r{i}", "hash", settings));
        Assert.Single(results, r => r.Allowed);
        limits = Make();
        Parallel.For(0, results.Length, i => results[i] = limits.CheckLaunch("a", "r", "hash", settings));
        Assert.Single(results, r => r.Allowed);
        Assert.Equal(31, results.Count(r => r.Duplicate && !r.Allowed));
    }

    [Fact]
    public void A_flood_of_new_origins_never_wipes_a_live_window_and_a_full_table_refuses_a_new_origin()
    {
        var limits = Make();
        for (var i = 0; i < 60; i++) { limits.CheckRead("busy"); }
        for (var i = 0; i < 1023; i++) { Assert.True(limits.CheckRead($"o{i}").Allowed); }

        var refused = limits.CheckRead("one-too-many");
        Assert.False(refused.Allowed);

        // A wiped "busy" would be refused as a new key with the full 60 s; the live window has 30 left.
        now = now.AddSeconds(30);
        var busy = limits.CheckRead("busy");
        Assert.False(busy.Allowed);
        Assert.Equal(30, busy.RetryAfterSeconds);

        now = now.AddSeconds(30);
        Assert.True(limits.CheckRead("one-too-many").Allowed);
    }

    [Fact]
    public void Reads_stop_at_sixty_a_minute_per_origin_and_a_new_instance_starts_empty()
    {
        var limits = Make();
        for (var i = 0; i < 60; i++) { Assert.True(limits.CheckRead("a").Allowed); }
        Assert.False(limits.CheckRead("a").Allowed);
        Assert.True(limits.CheckRead("b").Allowed);
        Assert.True(Make().CheckRead("a").Allowed);
    }
}
