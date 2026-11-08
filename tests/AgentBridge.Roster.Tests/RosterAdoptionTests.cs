using AgentBridge.Roster;

// §10: a voided merge cannot undo a removal it adopted, and the evidence rule with a subtree.
public sealed class RosterAdoptionTests
{
    static int Index(World w, RosterVerifier v, string label) => v.Graph.Find(w[label].Hash)!.Value;

    // A far fork quarantines P's add; confirmed P and K remove stolen senior S there; W's merge
    // adopts that branch; S, on a head from before its removal, removes W.
    internal static World VoidedMerge()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Add("+K", "G", "K", "cS");
        w.Confirm("cK", "G", "+K", "+K");
        w.Add("+W", "G", "W", "cK");
        var line = "+W";
        for (var i = 0; i < RosterVerifier.Window + 1; i++) { w.Rename($"r{i}", "G", i % 2 == 0 ? "+K" : "+W", line, $"Desk {i}"); line = $"r{i}"; }
        w.Add("+P", "G", "P", "cK");
        w.Confirm("cP", "G", "+P", "+P");
        w.Remove("PK-S", "P", "+S", "cP", cosigner: "K");
        w.Merge("adopt", "W", [line, "PK-S"], adopt: true);
        w.Remove("S-W", "S", "+W", "+W");
        return w;
    }

    [Fact]
    public void A_merge_whose_signer_is_then_revoked_still_adopts_and_the_removal_it_adopted_stays_applied()
    {
        var w = VoidedMerge();
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
        var labels = w.Labels.ToList();
        foreach (var label in labels)
        {
            w.Feed(v, label);
            if (labels.IndexOf(label) >= labels.IndexOf("adopt"))
            {
                Assert.DoesNotContain("S", w.Active(v));
                Assert.False(v.IsQuarantined(Index(w, v, "PK-S")));
            }
        }
        Assert.True(v.State.IsRevoked(Index(w, v, "+W")));
        Assert.False(v.State.IsVoid(Index(w, v, "adopt")));
        Assert.Equal(["G", "K", "P"], w.Active(v));
    }

    // T adds J; J signs 40 adds on each of two branches. A host holding one branch receives the
    // other with G's removal of T on its head: J's entries over its budget are evidence for it.
    [Fact]
    public void A_removal_signed_on_a_branch_over_its_targets_subtrees_budget_brings_that_branch_as_evidence()
    {
        var w = new World();
        w.Genesis();
        w.Add("+T", "G", "T", "G");
        w.Add("+J", "T", "J", "+T");
        foreach (var side in new[] { "A", "B" })
        {
            var last = "+J";
            for (var i = 0; i < 40; i++) { w.Add($"+{side}{i}", "J", $"{side}{i}", last); last = $"+{side}{i}"; }
        }
        w.Remove("R", "G", "+T", "+B39");
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
        var offered = w.Feed(v, [.. w.Labels]);
        var at = w.Labels.ToList();
        Assert.Equal(Invalid.Budget, offered[at.IndexOf("+B24")].Reason);
        Assert.NotEqual(Verdict.Refused, offered[at.IndexOf("R")].Verdict);
        Assert.True(v.Graph[Index(w, v, "+B39")].Evidence);
        Assert.True(v.State.IsRevoked(Index(w, v, "+J")));
        Assert.Equal(["G"], w.Active(v));
    }

    // G adds S; S adds T; T adds J. A far-off add Q is quarantined by the window. J's adopt merge
    // over Q is past the entry cap, so only a candidate; S's removal of T on it makes it evidence.
    // Evidence is never accepted, so it adopts nothing: Q stays quarantined, as in a control that
    // removes T on the line instead.
    [Fact]
    public void A_merge_that_became_evidence_adopts_nothing()
    {
        World Build(bool viaMerge)
        {
            var w = new World();
            w.Genesis();
            w.Add("+S", "G", "S", "G");
            w.Add("+T", "S", "T", "+S");
            w.Add("+J", "T", "J", "+T");
            var last = "+J";
            for (var i = 0; i < RosterVerifier.Window + 1; i++) { w.Add($"+D{i}", "G", $"D{i}", last); last = $"+D{i}"; }
            w.Add("+Q", "G", "Q", "+S");
            if (viaMerge)
            {
                w.Merge("M", "J", [last, "+Q"], adopt: true);
                w.Remove("S-T", "S", "+T", "M");
            }
            else { w.Remove("S-T", "S", "+T", last); }
            return w;
        }

        var cap = 4 + RosterVerifier.Window + 1 + 1;
        var wm = Build(true);
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => wm.Now) { EntryCap = cap };
        wm.Feed(v, [.. wm.Labels]);
        var wc = Build(false);
        var control = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => wc.Now) { EntryCap = cap };
        wc.Feed(control, [.. wc.Labels]);

        Assert.True(v.Graph[Index(wm, v, "M")].Evidence);
        Assert.True(v.IsQuarantined(Index(wm, v, "+Q")));
        Assert.Equal(wc.Active(control), wm.Active(v));
        Assert.DoesNotContain("Q", wm.Active(v));
    }
}
