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
    public async Task The_card_this_host_would_publish_matches_the_daemons_and_what_it_leaves_out_is_said_to_be_by_design()
    {
        DaemonPublished();
        sessions.Set(FakeSessions.Copilot(A));

        var report = await Shadow();

        string Verdict(string topic) => report.Differences.FirstOrDefault(d => d.Topic == topic)?.Verdict ?? HaShadow.Same;
        foreach (var obj in new[] { "decision", "f1", "f12" }) { Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("select", Node, obj))); }
        Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("text", Node, "reply")));
        Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("sensor", Node, "replypayload")));
        Assert.Equal(HaShadow.Same, Verdict(HaContract.DiscoveryTopic("button", Node, "submit")));
        Assert.Equal(HaShadow.Same, Verdict(HaContract.Topics(Node).Availability));

        var stop = Assert.Single(report.Differences, d => d.Topic == HaContract.DiscoveryTopic("button", Node, "stop"));
        Assert.Equal(HaShadow.DaemonOnly, stop.Verdict);
        Assert.NotNull(stop.Note);
        // Every other entity of the card is the same: availability, the 18 configs and the list's.
        Assert.True(report.Same >= 19, $"{report.Same} the same");
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
