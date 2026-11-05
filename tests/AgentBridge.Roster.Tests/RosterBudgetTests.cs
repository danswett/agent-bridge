using AgentBridge.Roster;

// §10's budget cases: removals counted per signer across branches, and a fabricated copy's
// entries counted against the copy, never the real device.
public sealed class RosterBudgetTests
{
    [Fact]
    public void An_approver_signing_removals_on_one_head_and_more_on_each_stops_at_64()
    {
        var w = new World();
        w.Genesis();
        w.Add("+C", "G", "C", "G");
        w.Add("+B", "G", "B", "+C");
        for (var i = 0; i < 33; i++)
        {
            w.Remove($"R{i}", "G", "+C", "+B");
            w.Remove($"S{i}", "G", "+B", $"R{i}");
        }
        var offered = w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now), [.. w.Labels]);
        var removals = offered.Skip(3).ToList();
        Assert.All(removals.Take(RosterVerifier.SignerBudget), o => Assert.Equal(Verdict.Accepted, o.Verdict));
        Assert.Equal(Invalid.Budget, removals[RosterVerifier.SignerBudget].Reason);
    }

    [Fact]
    public void A_copys_removals_count_against_the_copy_and_the_real_device_keeps_its_own_64()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+A'", "H", "A", "+H", copy: true);
        w.Add("+Z", "A", "Z", "+A'", signAs: "H");
        for (var i = 0; i < RosterVerifier.SignerBudget + 1; i++) { w.Remove($"F{i}", "A", "+Z", "+Z", signAs: "H"); }
        w.Add("+A", "G", "A", "G");
        w.Add("+Y", "A", "Y", "+A");
        w.Remove("A-Y", "A", "+Y", "+Y");
        var offered = w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now), [.. w.Labels]);
        var at = w.Labels.ToList();
        Assert.Equal(Verdict.Accepted, offered[at.IndexOf($"F{RosterVerifier.SignerBudget - 1}")].Verdict);
        Assert.Equal(Invalid.Budget, offered[at.IndexOf($"F{RosterVerifier.SignerBudget}")].Reason);
        Assert.Null(offered[at.IndexOf("A-Y")].Reason);
    }
}
