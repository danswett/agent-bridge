using AgentBridge.HomeAssistant;
using Xunit;

// The read-only shadow: what this host would publish beside what agent-ha-bridge's daemon has
// published for the machine, with nothing written to either.
public sealed class ShadowTests
{
    const string Machine = "DSWETT-DEV-VM1";
    const string A = "6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20";
    static readonly string Node = HaContract.NodeId(A);

    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();

    // The broker as the daemon left it: its session, its machine's list (which also names a
    // Claude session this host does not see), and that session's availability.
    void DaemonPublished()
    {
        foreach (var p in Contract.Publications("sessionPublish").Concat(Contract.Publications("globalStatus")))
        {
            ha.Seed(p.Topic, p.Payload);
        }
        ha.Seed(HaContract.Topics(HaContract.NodeId("c1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f607")).Availability, "online");
    }

    Task<ShadowReport> Shadow(bool answers = true) => HaShadow.CompareAsync(ha, sessions,
        new HaConnectorOptions { MachineName = Machine, Version = "0.1.0", Dev = true }, answers, TimeSpan.Zero);

    [Fact]
    public async Task The_shadow_writes_nothing_at_all()
    {
        DaemonPublished();
        sessions.Set(FakeSessions.Copilot(A));
        var published = ha.Log.Count;
        var registry = new Dictionary<string, string>(ha.Registry);

        await Shadow();

        Assert.Equal(published, ha.Log.Count);
        Assert.Empty(ha.Services);
        Assert.Empty(ha.Renames);
        Assert.Equal(registry, ha.Registry);
    }

    [Fact]
    public async Task The_card_this_host_would_publish_matches_the_daemons_end_session_included()
    {
        DaemonPublished();
        sessions.Set(FakeSessions.Copilot(A));

        var report = await Shadow();

        string Verdict(string topic) => report.Differences.FirstOrDefault(d => d.Topic == topic)?.Verdict ?? HaShadow.Same;
        foreach (var obj in new[] { "decision", "f1", "f12" }) { Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("select", Node, obj))); }
        Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("text", Node, "reply")));
        Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("sensor", Node, "replypayload")));
        Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("button", Node, "submit")));
        Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("button", Node, "stop")));
        Assert.Equal(HaShadow.Same, Verdict(HaContract.Topics(Node).Availability));

        // Every entity of the card is the same: availability, the 19 configs and the list's.
        Assert.True(report.Same >= 20, $"{report.Same} the same");
    }

    [Fact]
    public async Task A_session_only_the_daemon_lists_is_reported_and_not_excused()
    {
        DaemonPublished();
        sessions.Set(FakeSessions.Copilot(A));

        var report = await Shadow();

        var claude = HaContract.NodeId("c1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f607");
        Assert.Contains(claude, report.DaemonNodes);
        Assert.DoesNotContain(claude, report.HostNodes);
        var availability = Assert.Single(report.Differences, d => d.Topic == HaContract.Topics(claude).Availability);
        Assert.Equal(HaShadow.DaemonOnly, availability.Verdict);
        Assert.Null(availability.Note);
        // And the machine's list itself differs, naming which keys.
        var list = Assert.Single(report.Differences, d => d.Topic == HaContract.GlobalAttributesTopic(HaContract.MachineSlug(Machine)));
        Assert.Equal(HaShadow.Differs, list.Verdict);
        Assert.Contains("sessions", list.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_update_check_launch_outcomes_and_further_logins_the_host_is_not_configured_with_are_said_to_be_by_design()
    {
        DaemonPublished();
        var slug = HaContract.MachineSlug(Machine);
        var node = HaContract.MachineNode(slug);
        ha.Seed(HaContract.DiscoveryTopic("button", node, "check_updates"), $$"""{"unique_id":"agent_bridge_{{slug}}_check_updates","name":"Check for Bridge Update"}""");
        ha.Seed($"{HaContract.MachineTopicRoot(slug)}/newsession/outcome", """{"recent":[]}""");
        ha.Seed(HaContract.DiscoveryTopic("sensor", node, "usage_copilot_2"), $$"""{"unique_id":"agent_bridge_{{slug}}_usage_copilot_2","name":"Copilot usage (other)"}""");
        ha.Seed(HaContract.UsageStateTopic(slug, "copilot_2"), "12");
        sessions.Set(FakeSessions.Copilot(A));

        var report = await HaShadow.CompareAsync(ha, sessions, new HaConnectorOptions { MachineName = Machine, Version = "0.1.0", Dev = true }, true,
            TimeSpan.Zero, () => (new AgentBridge.Protocol.UsageReport(ContractTests.UsageReadings), 1));
        Assert.Equal("not served yet (a later stage)", report.Differences.Single(d => d.Topic == HaContract.DiscoveryTopic("button", node, "check_updates")).Note);
        Assert.Equal("launch card not compared (the shadow takes no launch options)", report.Differences.Single(d => d.Topic.EndsWith("/newsession/outcome", StringComparison.Ordinal)).Note);
        foreach (var topic in new[] { HaContract.DiscoveryTopic("sensor", node, "usage_copilot_2"), HaContract.UsageStateTopic(slug, "copilot_2") })
        {
            Assert.Equal("a further Copilot login this host is not configured to report (usage.copilotAccounts)", report.Differences.Single(d => d.Topic == topic).Note);
        }
    }

    [Fact]
    public async Task Usage_sensors_are_compared_with_a_reading_and_said_to_be_not_compared_without_one()
    {
        DaemonPublished();
        foreach (var p in Contract.Publications("usage")) { ha.Seed(p.Topic, p.Payload); }
        sessions.Set(FakeSessions.Copilot(A));
        var slug = HaContract.MachineSlug(Machine);
        var config = HaContract.DiscoveryTopic("sensor", HaContract.MachineNode(slug), "usage_copilot");

        var without = await Shadow();
        foreach (var topic in new[] { config, HaContract.UsageStateTopic(slug, "copilot") })
        {
            var entry = Assert.Single(without.Differences, d => d.Topic == topic);
            Assert.Equal(HaShadow.DaemonOnly, entry.Verdict);
            Assert.Equal("usage not compared (no reading was taken)", entry.Note);
        }

        var with = await HaShadow.CompareAsync(ha, sessions, new HaConnectorOptions { MachineName = Machine, Version = "0.1.0", Dev = true, UsageAccountSensors = ["copilot_2", "copilot_4"] }, true,
            TimeSpan.Zero, () => (new AgentBridge.Protocol.UsageReport(ContractTests.UsageReadings), 1));
        Assert.DoesNotContain(with.Differences, d => d.Topic == config);
        Assert.DoesNotContain(with.Differences, d => d.Topic == HaContract.UsageStateTopic(slug, "copilot"));
        // Further Copilot logins the host reports are compared like the rest, and agree.
        Assert.DoesNotContain(with.Differences, d => d.Topic.Contains("copilot_2", StringComparison.Ordinal) || d.Topic.Contains("copilot_4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_answers_the_answer_controls_are_said_to_be_left_out_by_design()
    {
        DaemonPublished();
        sessions.Set(FakeSessions.Copilot(A));

        var report = await Shadow(answers: false);

        var decision = Assert.Single(report.Differences, d => d.Topic == HaContract.DiscoveryTopic("select", Node, "decision"));
        Assert.Equal(HaShadow.DaemonOnly, decision.Verdict);
        Assert.Equal("not served without answers", decision.Note);
        Assert.Equal("not served without answers", report.Differences.Single(d => d.Topic == HaContract.DiscoveryTopic("select", Node, "f3")).Note);
    }
}
