using AgentBridge.Roster;

// More §10 scenarios: keep lists, wiping only when taken, and precedence at ranks 1 and 2.
public sealed class RosterPrecedenceTests
{
    // The person removes S keeping E, which S approved before the theft; E enrolls F after the
    // removal and F0 on a branch concurrent with it, and S adds Z concurrently.
    internal static World KeptOwnAdds()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Add("+E", "S", "E", "+S");
        w.Remove("G-S", "G", "+S", "+E", keep: ["+E"]);
        w.Add("+F", "E", "F", "G-S");
        w.Add("+F0", "E", "F0", "+E");
        w.Add("+Z", "S", "Z", "+E");
        return w;
    }

    [Fact]
    public void A_kept_devices_own_adds_stand_on_both_sides_of_the_removal_and_the_targets_do_not()
    {
        var w = KeptOwnAdds();
        Assert.Equal(["E", "F", "F0", "G"], w.Active(w.Verifier()));
    }

    // J and K, made and confirmed by stolen S on a branch, cosign a removal of S keeping
    // themselves and Y, which J added; C1 and C2, which G approved, remove S on the main line.
    internal static World ThiefKeepList()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Add("+C1", "G", "C1", "cS");
        w.Add("+C2", "G", "C2", "+C1");
        w.Confirm("cC1", "G", "+C1", "+C2");
        w.Confirm("cC2", "G", "+C2", "cC1");
        w.Remove("M", "C1", "+S", "cC2", cosigner: "C2");
        w.Add("+J", "S", "J", "cS");
        w.Add("+K", "S", "K", "+J");
        w.Confirm("cJ", "S", "+J", "+K");
        w.Confirm("cK", "S", "+K", "cJ");
        w.Add("+Y", "J", "Y", "cK");
        w.Remove("JK-S", "J", "+S", "+Y", keep: ["+J", "+K", "+Y"], cosigner: "K");
        return w;
    }

    [Fact]
    public void A_thiefs_keep_list_keeps_nothing()
    {
        var w = ThiefKeepList();
        var v = w.Verifier();
        Assert.Equal(Verdict.Accepted, w.Feed(w.Verifier([.. w.Labels.SkipLast(1)]), "JK-S")[0].Verdict);
        Assert.Equal(["C1", "C2", "G"], w.Active(v));
    }

    [Fact]
    public void A_device_cannot_cosign_its_own_removal()
    {
        var w = new World();
        w.Genesis();
        w.Add("+D", "G", "D", "G");
        w.Add("+J", "D", "J", "+D");
        w.Remove("JD-D", "J", "+D", "+J", cosigner: "D");
        var refused = Assert.Throws<RosterFormatException>(() => w.Entry("JD-D"));
        Assert.Equal(Refusal.Signer, refused.Code);
        w.Remove("G-D", "G", "+D", "+J");
        Assert.Equal(["G"], w.Active(w.Verifier([.. w.Labels.Where(l => l != "JD-D")])));
    }

    // B adds C, then removes itself on a head from before C's add; the person's removal of B
    // keeping C may follow.
    internal static World Wiping(bool personKeeps)
    {
        var w = new World();
        w.Genesis();
        w.Add("+B", "G", "B", "G");
        w.Add("+C", "B", "C", "+B");
        w.Remove("B-B", "B", "+B", "+B");
        if (personKeeps) { w.Remove("G-B", "G", "+B", "+C", keep: ["+C"]); }
        return w;
    }

    [Fact]
    public void A_self_removal_from_before_an_add_voids_it_without_taking_it_until_the_persons_removal_keeps_it()
    {
        var alone = Wiping(personKeeps: false);
        var v = alone.Verifier();
        var c = v.Graph.Find(alone["+C"].Hash)!.Value;
        Assert.Equal(["G"], alone.Active(v));
        Assert.True(v.State.IsVoid(c));
        Assert.False(v.State.IsRevoked(c));

        var kept = Wiping(personKeeps: true);
        Assert.Equal(["C", "G"], kept.Active(kept.Verifier()));
    }

    [Fact]
    public void A_removal_that_descends_from_an_add_and_does_not_keep_it_takes_it()
    {
        var w = new World();
        w.Genesis();
        w.Add("+B", "G", "B", "G");
        w.Add("+C", "B", "C", "+B");
        w.Remove("G-B", "G", "+B", "+C");
        var v = w.Verifier();
        Assert.True(v.State.IsRevoked(v.Graph.Find(w["+C"].Hash)!.Value));
        Assert.Equal(["G"], w.Active(v));
    }

    // H, revoked by C1's and C2's cosigned removal, had removed C1 on an old head and added Y;
    // C1 enrolled F after the cosigned removal.
    internal static World RankOneAgainstTwo()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Confirm("cH", "G", "+H", "+H");
        w.Add("+C1", "G", "C1", "cH");
        w.Add("+C2", "G", "C2", "+C1");
        w.Confirm("cC1", "G", "+C1", "+C2");
        w.Confirm("cC2", "G", "+C2", "cC1");
        w.Remove("M", "C1", "+H", "cC2", cosigner: "C2");
        w.Add("+F", "C1", "F", "M");
        w.Remove("H-C1", "H", "+C1", "cC2");
        w.Add("+Y", "H", "Y", "H-C1");
        return w;
    }

    [Fact]
    public void A_rank_one_removal_does_not_outrank_the_cosigned_removal_of_its_signer()
    {
        // C1 stays revoked, as every valid removal's target is; what the cosigned removal leads
        // to, F, is shielded, and what H did after its removal of C1 is void.
        var w = RankOneAgainstTwo();
        var v = w.Verifier();
        Assert.True(v.State.IsVoid(v.Graph.Find(w["+Y"].Hash)!.Value));
        Assert.Equal(["C2", "F", "G"], w.Active(v));
    }

    // G adds P, P adds Q, and G adds W after Q's add; Q removes W on a branch, G removes P
    // (taking Q), and W enrolls Z after G's removal.
    internal static World Yielding()
    {
        var w = new World();
        w.Genesis();
        w.Add("+P", "G", "P", "G");
        w.Add("+Q", "P", "Q", "+P");
        w.Add("+W", "G", "W", "+Q");
        w.Remove("Q-W", "Q", "+W", "+W");
        w.Remove("G-P", "G", "+P", "+W");
        w.Add("+Z", "W", "Z", "G-P");
        return w;
    }

    [Fact]
    public void A_rank_one_removal_by_a_device_in_the_others_targets_subtree_yields_so_the_other_shields()
    {
        var w = Yielding();
        var v = w.Verifier();
        Assert.True(v.State.IsRevoked(v.Graph.Find(w["+W"].Hash)!.Value));
        Assert.Equal(["G", "Z"], w.Active(v));
    }
}
