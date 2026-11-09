using AgentBridge.Roster;

// The §10 recoveries from fabricated devices: removals signed by what a stolen device made,
// partial cascades, and a recovery with a quarantined descendant.
public sealed class RosterFabricationTests
{
    static int Index(World w, RosterVerifier v, string label) => v.Graph.Find(w[label].Hash)!.Value;

    // The person, on G, removes stolen S (rank 1); from before that, S enrolls and confirms J, K
    // and Y, and J and K cosign a removal of S (rank 2).
    internal static World FabricatedOutrank()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Remove("G-S", "G", "+S", "cS");
        w.Add("+J", "S", "J", "cS");
        w.Add("+K", "S", "K", "+J");
        w.Add("+Y", "S", "Y", "+K");
        w.Confirm("cJ", "S", "+J", "+Y");
        w.Confirm("cK", "S", "+K", "cJ");
        w.Remove("JK-S", "J", "+S", "cK", keep: ["+J", "+K"], cosigner: "K");
        return w;
    }

    [Fact]
    public void A_removal_by_fabricated_devices_cannot_outrank_the_persons()
    {
        var w = FabricatedOutrank();
        Assert.Equal(["G"], w.Active(w.Verifier()));
    }

    // Stolen D, on a head from before G's removal of it, adds M, J and K, confirms J and K, and
    // J and K cosign D's removal.
    internal static World FabricatedOnOldHead()
    {
        var w = new World();
        w.Genesis();
        w.Add("+D", "G", "D", "G");
        w.Confirm("cD", "G", "+D", "+D");
        w.Remove("G-D", "G", "+D", "cD");
        w.Add("+M", "D", "M", "cD");
        w.Add("+J", "D", "J", "+M");
        w.Add("+K", "D", "K", "+J");
        w.Confirm("cJ", "D", "+J", "+K");
        w.Confirm("cK", "D", "+K", "cJ");
        w.Remove("JK-D", "J", "+D", "cK", keep: ["+J", "+K"], cosigner: "K");
        w.Add("+Z", "J", "Z", "JK-D");
        return w;
    }

    [Fact]
    public void Fabricated_cosigners_made_on_an_old_head_are_void_with_everything_they_sign()
    {
        var w = FabricatedOnOldHead();
        var v = w.Verifier();
        Assert.All(new[] { "+M", "+J", "+K", "+Z" }, l => Assert.True(v.State.IsVoid(Index(w, v, l)), l));
        Assert.Equal(["G"], w.Active(v));
    }

    // S approved P, P approved C1, and G approved C2; S's J and K are ancestors of M, C1's and
    // C2's removal of S keeping P and C1. J and K then remove P, after syncing M or on a fork
    // from before it.
    internal static World RemoveP(bool afterSync)
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Add("+P", "S", "P", "cS");
        w.Confirm("cP", "S", "+P", "+P");
        w.Add("+C1", "P", "C1", "cP");
        w.Confirm("cC1", "P", "+C1", "+C1");
        w.Add("+C2", "G", "C2", "cC1");
        w.Confirm("cC2", "G", "+C2", "+C2");
        w.Add("+J", "S", "J", "cC2");
        w.Add("+K", "S", "K", "+J");
        w.Confirm("cJ", "S", "+J", "+K");
        w.Confirm("cK", "S", "+K", "cJ");
        w.Remove("M", "C1", "+S", "cK", keep: ["+P", "+C1"], cosigner: "C2");
        w.Add("+F", "C1", "F", "M");
        w.Add("+F2", "C2", "F2", "+F");
        w.Remove("JK-P", "J", "+P", afterSync ? "+F2" : "cK", cosigner: "K");
        w.Add("+Y", "J", "Y", "JK-P");
        return w;
    }

    [Fact]
    public void A_removal_of_P_by_devices_the_recovery_took_is_invalid_after_syncing_it()
    {
        var w = RemoveP(afterSync: true);
        var v = w.Verifier([.. w.Labels.SkipLast(2)]);
        Assert.Equal(Invalid.Signer, w.Feed(v, "JK-P")[0].Reason);
        Assert.Equal(["C1", "C2", "F", "F2", "G", "P"], w.Active(v));
    }

    [Fact]
    public void A_removal_of_P_from_before_the_recovery_is_the_tie_through_P()
    {
        var w = RemoveP(afterSync: false);
        var v = w.Verifier();
        Assert.True(v.State.IsVoid(Index(w, v, "+F")));
        Assert.True(v.State.IsVoid(Index(w, v, "+Y")));
        Assert.Equal(["C2", "F2", "G"], w.Active(v));
    }

    // Stolen D adds J and K on the main line; E, which D enrolled before the theft on another
    // head, joins the heads and enrolls E2 above them. The person removes D keeping E and E2; on
    // a fork from before that, D confirms J and K and adds Y, and J and K cosign a removal of D,
    // which E's add is not under.
    internal static World PartialCascade()
    {
        var w = new World();
        w.Genesis();
        w.Add("+D", "G", "D", "G");
        w.Confirm("cD", "G", "+D", "+D");
        w.Add("+E", "D", "E", "cD");
        w.Add("+J", "D", "J", "cD");
        w.Add("+K", "D", "K", "+J");
        w.Merge("join", "E", ["+E", "+K"]);
        w.Add("+E2", "E", "E2", "join");
        w.Remove("M", "G", "+D", "+E2", keep: ["+E", "+E2"]);
        w.Confirm("cJ", "D", "+J", "+K");
        w.Confirm("cK", "D", "+K", "cJ");
        w.Add("+Y", "D", "Y", "cK");
        w.Remove("JK-D", "J", "+D", "+Y", keep: ["+J", "+K"], cosigner: "K");
        return w;
    }

    // §10 says E and E2 stay active here. §3.5's rules, which this library follows, void them: J's
    // and K's removal revokes D, E's add is not its ancestor, so E is void because of it, and a
    // shield protects only what descends from the person's removal (rank 1 here, so §3.5's own
    // guarantee says a rank-2 pair on D's side can reach a device junior to D). Raised with the
    // Trust lead, who decided (2026-11-04) that §3.5 governs and the bullet is an erratum (devices.md).
    [Fact]
    public void A_partial_cascade_by_one_device_does_not_save_its_kept_devices_from_a_fabricated_pair()
    {
        var w = PartialCascade();
        var v = w.Verifier();
        Assert.True(v.State.IsVoid(Index(w, v, "+Y")));
        Assert.True(v.State.IsVoid(Index(w, v, "+E")));
        Assert.False(v.State.IsRevoked(Index(w, v, "+E")));
        Assert.Equal(["G"], w.Active(v));
    }

    // C1 and C2, approved and confirmed by G, remove S after the honest line advanced nine
    // entries past the head P where S then submits add(J), which is quarantined; a later merge
    // adopts J's branch.
    internal static World QuarantinedDescendant()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Add("+C1", "G", "C1", "cS");
        w.Add("+C2", "G", "C2", "+C1");
        w.Confirm("cC1", "G", "+C1", "+C2");
        w.Confirm("cC2", "G", "+C2", "cC1");
        var last = "cC2";
        for (var i = 0; i < RosterVerifier.Window + 1; i++) { w.Rename($"r{i}", "G", $"+C{i % 2 + 1}", last, $"Desk {i}"); last = $"r{i}"; }
        w.Add("+J", "S", "J", "cC2");
        w.Remove("M", "C1", "+S", last, cosigner: "C2");
        w.Merge("tidy", "G", ["M", "+J"], adopt: true);
        return w;
    }

    [Fact]
    public void A_quarantined_descendant_does_not_block_the_recovery_and_stays_void_once_adopted()
    {
        var w = QuarantinedDescendant();
        var v = w.Verifier([.. w.Labels.TakeWhile(l => l != "tidy")]);
        Assert.True(v.IsQuarantined(Index(w, v, "+J")));
        Assert.Equal(["C1", "C2", "G"], w.Active(v));
        Assert.Equal(Verdict.Accepted, w.Feed(v, "tidy")[0].Verdict);
        Assert.False(v.IsQuarantined(Index(w, v, "+J")));
        Assert.True(v.State.IsVoid(Index(w, v, "+J")));
        Assert.Equal(["C1", "C2", "G"], w.Active(v));
    }
}
