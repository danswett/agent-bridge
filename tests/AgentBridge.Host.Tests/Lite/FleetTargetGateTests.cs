using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using AgentBridge.Roster;
using Xunit;

namespace AgentBridge.Host.Tests;

// Relay A (owner, Nov 20 12:37 PT): a host-launched fleet session is a reply target only for a
// sender the pinned, verified device roster lists as a serving host, by its exact session id.
public sealed class FleetTargetGateTests
{
    const string Target = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    readonly World world = new();
    readonly RosterVerifier signed;
    readonly LiteLaunchLog launches;
    SessionProcess? current = new(42, DateTimeOffset.UnixEpoch); bool unreadable;

    public FleetTargetGateTests()
    {
        launches = new(id => id == Target ? current : null, (started, pid) => unreadable ? null : current?.Id == pid && current.StartedAt == started.StartedAt);
        world.Genesis("P"); world.Add("H", "P", "H", "G", kind: "host"); world.Add("Q", "P", "Q", "H");
        signed = new RosterVerifier(world.AccountId, world.GenerationId, () => world.Now);
        Assert.All(world.Feed(signed, "G", "H", "Q"), r => Assert.Equal(Verdict.Accepted, r.Verdict));
    }

    void Launch(string permissions = LaunchProtocol.AllowAll, bool fleet = true)
    {
        launches.Launched(Target, new LaunchedWith { Agent = "copilot", Permissions = permissions });
        if (fleet) { launches.FleetLaunched(Target, new(42, DateTimeOffset.UnixEpoch)); }
    }

    static VerifiedEnvelope From(string machine) => new(new EnvelopeBody("22222222-2222-2222-2222-222222222222", "acct", "g", [],
        new AgentPrincipal(machine, "add", "sess-9", "inst", "cred", "copilot", "launcher"), new AgentChain("root", 0, null, 0, 1, []),
        new AgentProvenance(null, default, null), "message", new AgentTarget("acct", "HOST", Target, null), "sha", 0, 0), []);

    async Task<bool> Allowed(Func<RosterVerifier?> roster, string target = Target, string? sender = null) =>
        (await new FleetTargetGate(launches, roster).CheckAsync(From(sender ?? world.Id("H")), target)).Allowed;

    [Fact]
    public async Task An_allow_all_session_launched_here_without_the_fleet_flag_is_refused()
    {
        Launch(fleet: false);
        var decision = await new FleetTargetGate(launches, () => signed).CheckAsync(From(world.Id("H")), Target);
        Assert.Equal((false, LiteCodes.NotReplyTarget, FleetTargetGate.Reason), (decision.Allowed, decision.Code, decision.Reason));
    }

    [Fact]
    public async Task A_fleet_session_is_refused_with_no_verified_roster_an_unverified_pin_or_a_sender_the_roster_does_not_serve_as_a_host()
    {
        Launch();
        Assert.False(await Allowed(() => null));
        Assert.False(await Allowed(() => throw new InvalidOperationException("refused: not yet verified")));
        Assert.False(await Allowed(() => signed, sender: world.Id("Q")));
        Assert.False(await Allowed(() => signed, sender: world.Id("X")));
        Assert.False(await Allowed(() => new RosterVerifier(world.AccountId, world.GenerationId, () => world.Now))); // present, the serving host not verified
        var genesisOnly = new RosterVerifier(world.AccountId, world.GenerationId, () => world.Now);
        Assert.All(world.Feed(genesisOnly, "G"), r => Assert.Equal(Verdict.Accepted, r.Verdict));
        Assert.False(await Allowed(() => genesisOnly));
    }

    [Fact]
    public async Task A_session_launched_before_a_restart_is_refused_because_the_registry_starts_empty() =>
        Assert.False((await new FleetTargetGate(new LiteLaunchLog(), () => signed).CheckAsync(From(world.Id("H")), Target)).Allowed);

    [Theory, InlineData("aaaaaaaa"), InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa"), InlineData("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"), InlineData("agent_bridge_aaaaaaaaaaaaaaaa")]
    public async Task Only_the_exact_session_id_matches_never_a_prefix_case_change_or_node_id(string guess)
    {
        Launch();
        Assert.False(await Allowed(() => signed, guess));
        Assert.True(await Allowed(() => signed));
    }

    [Fact]
    public async Task The_fleet_flag_dies_with_the_session_and_a_later_process_on_the_same_id_inherits_nothing()
    {
        Launch();
        Assert.True(await Allowed(() => signed));
        current = null;
        Assert.False(launches.IsFleetTarget(Target)); // as the session source's change notification does
        current = new(42, DateTimeOffset.UnixEpoch);
        Assert.False(await Allowed(() => signed));
        Assert.Equal(LaunchProtocol.AllowAll, launches.PermissionsOf(Target)); // what the forced-ask gate reads is untouched
        Launch();
        Assert.True(await Allowed(() => signed));
        current = new(42, DateTimeOffset.UnixEpoch.AddMinutes(1)); // the same id and pid, another process
        Assert.False(await Allowed(() => signed));
        current = new(42, DateTimeOffset.UnixEpoch);
        Assert.False(await Allowed(() => signed));
    }

    [Fact]
    public async Task A_launch_whose_process_identity_cannot_be_verified_is_never_a_fleet_target()
    {
        Launch();
        unreadable = true; // the started process gone, or its tree unreadable
        Assert.False(await Allowed(() => signed));
        unreadable = false;
        Assert.True(await Allowed(() => signed));
        current = new(42, null);
        Assert.False(await Allowed(() => signed));
        current = new(42, DateTimeOffset.UnixEpoch);
        launches.FleetLaunched(Target, new(42, DateTimeOffset.UnixEpoch, Unverified: true)); // a launch whose creation time could not be read
        Assert.False(await Allowed(() => signed));
    }
}
