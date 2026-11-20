using System.Security.Cryptography;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Lite;
using AgentBridge.Roster;
using Xunit;

// The relay A-roster seam (#740): a verified lite sender counts only when security.roster is on and
// its machine is a serving host on this host's pinned roster, bound by the same device key.
public sealed class LiteSenderRosterTests
{
    readonly World world = new();
    readonly RosterVerifier roster;

    public LiteSenderRosterTests()
    {
        world.Genesis("P");
        world.Add("H", "P", "H", "G", kind: "host");
        roster = new RosterVerifier(world.AccountId, world.GenerationId, () => world.Now);
        Assert.All(world.Feed(roster, "G", "H"), r => Assert.Equal(Verdict.Accepted, r.Verdict));
    }

    static string AddOf(string device) => Codec.Base64Url(SHA256.HashData(World.Keys(device).Dh.Public));

    VerifiedEnvelope From(string machine, string add, string? account = null)
    {
        var body = new EnvelopeBody("req", account ?? world.AccountId, LiteProfile.Generation, [LiteProfile.Head],
            new AgentPrincipal(machine, add, "sess", "inst", "cred", "copilot", "launcher"),
            new AgentChain("req", 0, null, 1, 1, []), new AgentProvenance(null, OEligible.No, null), LiteProfile.Action,
            new AgentTarget(world.AccountId, "target", "s", "i"), "", 0, 0);
        return new VerifiedEnvelope(body, []);
    }

    LiteSenderRoster On(Func<RosterVerifier?>? pinned = null) => new(() => true, pinned ?? (() => roster));

    [Fact]
    public void A_serving_host_on_the_pinned_roster_is_verified_when_the_roster_is_on()
    {
        Assert.True(On().IsRosterVerified(From(world.Id("H"), AddOf("H"))));
    }

    [Fact]
    public void A_sender_that_is_not_a_serving_host_on_the_roster_is_refused()
    {
        Assert.False(On().IsRosterVerified(From(world.Id("X"), AddOf("X"))));
        // A person's device is on the roster but is not a host.
        Assert.False(On().IsRosterVerified(From(world.Id("P"), AddOf("P"))));
        Assert.False(On().IsRosterVerified(From(world.Id("H"), AddOf("H"), account: "another-account")));
    }

    [Fact]
    public void A_member_is_refused_while_the_roster_is_off_or_its_switch_cannot_be_read()
    {
        var member = From(world.Id("H"), AddOf("H"));
        Assert.False(new LiteSenderRoster(() => false, () => roster).IsRosterVerified(member));
        Assert.False(new LiteSenderRoster(() => throw new IOException(), () => roster).IsRosterVerified(member));
    }

    [Fact]
    public void An_empty_missing_or_unreadable_roster_refuses_every_sender()
    {
        var member = From(world.Id("H"), AddOf("H"));
        Assert.False(On(() => new RosterVerifier(world.AccountId, world.GenerationId, () => world.Now)).IsRosterVerified(member));
        Assert.False(On(() => null).IsRosterVerified(member));
        Assert.False(On(() => throw new InvalidOperationException("refused: pin unreadable")).IsRosterVerified(member));
    }

    [Fact]
    public void Case_whitespace_and_key_tricks_on_the_principal_are_refused()
    {
        var id = world.Id("H");
        var add = AddOf("H");
        world.Add("J", "P", "J", "H", kind: "host");
        Assert.Equal(Verdict.Accepted, Assert.Single(world.Feed(roster, "J")).Verdict);
        foreach (var (machine, key) in new[]
        {
            (id.ToUpperInvariant(), add), (" " + id, add), (id + " ", add), (id + "\n", add), ("", add),
            (id, add.ToUpperInvariant()), (id, add + " "), (id, " " + add), (id, ""), (id, AddOf("J")), (world.Id("J"), add),
        })
        {
            Assert.False(On().IsRosterVerified(From(machine, key)), $"{machine}|{key}");
        }
    }
}
