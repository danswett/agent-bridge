using AgentBridge.Roster;

// The precedence, recovery and evidence scenarios of §10, each in every delivery order the
// property tests try.
public sealed class RosterRecoveryTests
{
    static RosterVerifier All(World w) => w.Verifier();

    // G enrolls and confirms D, which is stolen; D adds M, J and K and confirms J and K, which
    // cosign D's removal while the person's removal of D keeps nothing.
    internal static World FabricatedCosigners()
    {
        var w = new World();
        w.Genesis();
        w.Add("+D", "G", "D", "G");
        w.Confirm("cD", "G", "+D", "+D");
        w.Add("+M", "D", "M", "cD");
        w.Add("+J", "D", "J", "+M");
        w.Add("+K", "D", "K", "+J");
        w.Confirm("cJ", "D", "+J", "+K");
        w.Confirm("cK", "D", "+K", "cJ");
        w.Remove("G-D", "G", "+D", "cK");
        w.Remove("JK-D", "J", "+D", "cK", keep: ["+J", "+K"], cosigner: "K");
        return w;
    }

    [Fact]
    public void Fabricated_cosigners_of_a_stolen_device_go_with_it()
    {
        var w = FabricatedCosigners();
        Assert.Equal(["G"], w.Active(All(w)));
    }

    // G approves and confirms S, then C1 and C2; stolen S adds and confirms J and K. On a branch
    // withheld from before the recovery, J and K remove C1 and C2 and J enrolls Y; the recovery M
    // (C1 and C2 removing S) takes J and K, and C1 enrolls F after it.
    internal static World ForkBeforeRecovery(bool cosigned = true, bool fromRecovery = false)
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Add("+C1", "G", "C1", "cS");
        w.Add("+C2", "G", "C2", "+C1");
        w.Add("+J", "S", "J", "+C2");
        w.Add("+K", "S", "K", "+J");
        w.Confirm("cJ", "S", "+J", "+K");
        w.Confirm("cK", "S", "+K", "cJ");
        w.Confirm("cC1", "G", "+C1", "cK");
        w.Confirm("cC2", "G", "+C2", "cC1");
        if (cosigned) { w.Remove("M", "C1", "+S", "cC2", cosigner: "C2"); }
        else { w.Remove("M", "G", "+S", "cC2"); }
        w.Add("+F", "C1", "F", "M");
        var fork = fromRecovery ? "M" : "cC2";
        w.Remove("JK-C1", "J", "+C1", fork, cosigner: "K");
        w.Remove("JK-C2", "J", "+C2", "JK-C1", cosigner: "K");
        w.Add("+Y", "J", "Y", "JK-C2");
        return w;
    }

    [Fact]
    public void A_recovery_that_takes_the_thiefs_pair_shields_what_follows_it_from_their_withheld_removals()
    {
        var w = ForkBeforeRecovery();
        Assert.Equal(["F", "G"], w.Active(All(w)));
    }

    [Fact]
    public void Removals_signed_on_a_fork_from_the_recovery_by_devices_it_took_are_invalid()
    {
        var w = ForkBeforeRecovery(fromRecovery: true);
        var v = w.Verifier([.. w.Labels.Where(l => l is not ("JK-C1" or "JK-C2" or "+Y"))]);
        var offered = v.Offer(w.Entry("JK-C1"));
        Assert.Equal(Verdict.Refused, offered[0].Verdict);
        Assert.Equal(Invalid.Signer, offered[0].Reason);
        Assert.Equal(["C1", "C2", "F", "G"], w.Active(v));
    }

    [Fact]
    public void A_recovery_by_one_device_is_outranked_by_the_thiefs_cosigned_pair()
    {
        var w = ForkBeforeRecovery(cosigned: false);
        Assert.Equal(["G"], w.Active(All(w)));
    }

    // G approves S, then P; S approves E1 and E2, P approves C1 and G approves C2, all confirmed.
    // On a branch from before C1's add, E1 and E2 remove P; the person's removal of S keeps E1
    // and E2, and C1 enrolls F; the thief adds Y through S on top of the removal of P.
    internal static World KeptCannotShield()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Add("+P", "G", "P", "+S");
        w.Add("+E1", "S", "E1", "+P");
        w.Add("+E2", "S", "E2", "+E1");
        w.Confirm("cS", "G", "+S", "+E2");
        w.Confirm("cP", "G", "+P", "cS");
        w.Confirm("cE1", "S", "+E1", "cP");
        w.Confirm("cE2", "S", "+E2", "cE1");
        w.Add("+C1", "P", "C1", "cE2");
        w.Add("+C2", "G", "C2", "+C1");
        w.Confirm("cC1", "P", "+C1", "+C2");
        w.Confirm("cC2", "G", "+C2", "cC1");
        w.Remove("E-P", "E1", "+P", "cE2", cosigner: "E2");
        w.Add("+Y", "S", "Y", "E-P");
        w.Remove("M", "C1", "+S", "cC2", keep: ["+E1", "+E2"], cosigner: "C2");
        w.Add("+F", "C1", "F", "M");
        return w;
    }

    [Fact]
    public void Devices_kept_from_a_thiefs_subtree_cannot_shield_what_the_thief_adds()
    {
        var w = KeptCannotShield();
        Assert.Equal(["C2", "E1", "E2", "G"], w.Active(All(w)));
    }

    // Confirmed C1 and C2 cosign the removal of stolen senior H; H, on a head before it, removes
    // them both and adds Y; C1 and C2 enroll replacements after removing H.
    internal static World Reciprocal()
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
        w.Add("+R1", "C1", "R1", "M");
        w.Add("+R2", "C2", "R2", "+R1");
        w.Remove("H-C1", "H", "+C1", "cC2");
        w.Remove("H-C2", "H", "+C2", "H-C1");
        w.Add("+Y", "H", "Y", "H-C2");
        return w;
    }

    [Fact]
    public void A_cosigned_removal_outranks_its_targets_removals_of_its_signers()
    {
        var w = Reciprocal();
        Assert.Equal(["G", "R1", "R2"], w.Active(All(w)));
    }

    // Stolen genesis S made C1 and C2 on the main line, and J and K on an old branch; each pair
    // removes S, keeping itself. Neither shields: everyone goes.
    internal static World Tie()
    {
        var w = new World();
        w.Genesis("S", "S");
        w.Add("+J", "S", "J", "S");
        w.Add("+K", "S", "K", "+J");
        w.Confirm("cJ", "S", "+J", "+K");
        w.Confirm("cK", "S", "+K", "cJ");
        w.Remove("JK-S", "J", "S", "cK", keep: ["+J", "+K"], cosigner: "K");
        w.Add("+Y", "J", "Y", "JK-S");
        w.Add("+C1", "S", "C1", "S");
        w.Add("+C2", "S", "C2", "+C1");
        w.Confirm("cC1", "S", "+C1", "+C2");
        w.Confirm("cC2", "S", "+C2", "cC1");
        w.Remove("C-S", "C1", "S", "cC2", keep: ["+C1", "+C2"], cosigner: "C2");
        w.Add("+R1", "C1", "R1", "C-S");
        return w;
    }

    [Fact]
    public void In_the_tie_through_a_stolen_genesis_every_device_goes_and_the_generation_needs_a_reset()
    {
        var w = Tie();
        var v = All(w);
        Assert.Empty(w.Active(v));
        Assert.Empty(v.State.PersonDevices);
    }

    // A and B remove C while C and D remove A, all four added concurrently by G and confirmed.
    internal static World Mutual()
    {
        var w = new World();
        w.Genesis();
        foreach (var d in new[] { "A", "B", "C", "D" }) { w.Add($"+{d}", "G", d, "G"); }
        w.Merge("m", "G", ["+A", "+B", "+C", "+D"]);
        w.Confirm("cA", "G", "+A", "m");
        w.Confirm("cB", "G", "+B", "cA");
        w.Confirm("cC", "G", "+C", "cB");
        w.Confirm("cD", "G", "+D", "cC");
        w.Remove("AB-C", "A", "+C", "cD", cosigner: "B");
        w.Add("+X", "A", "X", "AB-C");
        w.Remove("CD-A", "C", "+A", "cD", cosigner: "D");
        w.Add("+Z", "D", "Z", "CD-A");
        return w;
    }

    [Fact]
    public void Mutual_equal_rank_removals_revoke_both_targets_and_void_their_later_work()
    {
        var w = Mutual();
        Assert.Equal(["B", "D", "G", "Z"], w.Active(All(w)));
    }

    [Fact]
    public void A_removed_devices_add_on_a_head_from_before_its_removal_is_void_with_all_it_led_to()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Remove("G-H", "G", "+H", "+H");
        w.Add("+N", "H", "N", "+H");
        w.Add("+N2", "N", "N2", "+N");
        Assert.Equal(["G"], w.Active(All(w)));
    }

    [Fact]
    public void A_device_removing_itself_on_two_heads_voids_every_grant_not_under_both()
    {
        var w = new World();
        w.Genesis();
        w.Add("+D", "G", "D", "G");
        w.Add("+E", "D", "E", "+D");
        w.Add("+F", "D", "F", "+E");
        w.Remove("D1", "D", "+D", "+E");
        w.Remove("D2", "D", "+D", "+D");
        Assert.Equal(["G"], w.Active(All(w)));
    }

    // H's 64 sibling adds, then a 65th over its budget; G removes H on the 65th. The siblings pass
    // the four-head limit, so all but the first four are quarantined, as a hostile approver's are.
    static World Siblings(bool self = false)
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        for (var i = 0; i < RosterVerifier.SignerBudget + 1; i++) { w.Add($"+N{i}", "H", $"N{i}", "+H"); }
        if (self) { w.Remove("R", "H", "+H", $"+N{RosterVerifier.SignerBudget}"); }
        else { w.Remove("R", "G", "+H", $"+N{RosterVerifier.SignerBudget}"); }
        return w;
    }

    [Fact]
    public void A_removal_brings_its_targets_over_budget_entry_as_evidence_and_applies()
    {
        var w = Siblings();
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
        var offered = w.Feed(v, [.. w.Labels]);
        Assert.Equal(Invalid.Budget, offered[^2].Reason);
        // On a quarantined branch (the siblings pass four heads), and applied as a removal is there.
        Assert.Equal(Verdict.Quarantined, offered[^1].Verdict);
        Assert.True(v.Graph[v.Graph.Find(w[$"+N{RosterVerifier.SignerBudget}"].Hash)!.Value].Evidence);
        Assert.DoesNotContain("H", w.Active(v));
        Assert.DoesNotContain($"N{RosterVerifier.SignerBudget}", w.Active(v));
    }

    [Fact]
    public void A_self_removal_brings_no_evidence()
    {
        var w = Siblings(self: true);
        var offered = w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now), [.. w.Labels]);
        Assert.Equal(Invalid.Evidence, offered[^1].Reason);
    }

    [Fact]
    public void An_add_in_a_state_with_64_active_devices_is_refused_and_a_removal_is_not()
    {
        var w = new World();
        w.Genesis();
        var last = "G";
        for (var i = 0; i < RosterVerifier.MaxActive; i++) { w.Add($"+N{i}", "G", $"N{i}", last); last = $"+N{i}"; }
        w.Remove("R", "G", "+N0", last);
        var offered = w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now), [.. w.Labels]);
        Assert.Equal(Invalid.ActiveCap, offered[^2].Reason);
        Assert.Equal(Verdict.Accepted, offered[^3].Verdict);
    }
}
