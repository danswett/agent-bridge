using AgentBridge.Roster;

// The §10 scenarios with two adds of one id, thief-built recoveries, senior removals on old
// heads, a partitioned relay, copies across ids, and a removal parent the target cannot choose.
public sealed class RosterCopyTests
{
    static int Index(World w, RosterVerifier v, string label) => v.Graph.Find(w[label].Hash)!.Value;

    // G approves S, C1 and C2; S's J and K each add X's key on concurrent branches, S joins them,
    // and C1 and C2 remove S on the join; S, on an old head, then removes K.
    internal static World TwoAddsConflicted()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Add("+C1", "G", "C1", "cS");
        w.Add("+C2", "G", "C2", "+C1");
        w.Confirm("cC1", "G", "+C1", "+C2");
        w.Confirm("cC2", "G", "+C2", "cC1");
        w.Add("+J", "S", "J", "cC2");
        w.Add("+K", "S", "K", "cC2");
        w.Add("+Xj", "J", "X", "+J");
        w.Add("+Xk", "K", "X", "+K");
        w.Merge("m", "S", ["+Xj", "+Xk"]);
        w.Remove("M", "C1", "+S", "m", cosigner: "C2");
        w.Add("+R", "C1", "R", "M");
        w.Remove("S-K", "S", "+K", "+K");
        return w;
    }

    [Fact]
    public void A_conflicted_ids_adds_both_go_with_the_removal_of_their_makers_approver()
    {
        var w = TwoAddsConflicted();
        var before = w.Verifier([.. w.Labels.TakeWhile(l => l != "M")]);
        Assert.Contains(w.Id("X"), before.State.Conflicted);
        var v = w.Verifier();
        Assert.True(v.State.IsRevoked(Index(w, v, "+Xj")));
        Assert.True(v.State.IsRevoked(Index(w, v, "+Xk")));
        Assert.Equal(["C1", "C2", "G", "R"], w.Active(v));
    }

    // J, made and confirmed by stolen S, removes S with C2 keeping J and Y; the person then
    // removes J with C1 and C2.
    internal static World ThiefInitiated(bool personRemovesJ)
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Add("+C1", "G", "C1", "cS");
        w.Add("+C2", "G", "C2", "+C1");
        w.Confirm("cC1", "G", "+C1", "+C2");
        w.Confirm("cC2", "G", "+C2", "cC1");
        w.Add("+J", "S", "J", "cC2");
        w.Confirm("cJ", "S", "+J", "+J");
        w.Add("+Y", "J", "Y", "cJ");
        w.Remove("JC2-S", "J", "+S", "+Y", keep: ["+J", "+Y"], cosigner: "C2");
        if (personRemovesJ) { w.Remove("C-J", "C1", "+J", "JC2-S", cosigner: "C2"); }
        return w;
    }

    [Fact]
    public void A_thief_built_recovery_cosigned_anyway_keeps_what_it_names_until_the_person_removes_it()
    {
        var w = ThiefInitiated(personRemovesJ: false);
        Assert.Equal(["C1", "C2", "G", "J", "Y"], w.Active(w.Verifier()));
        var removed = ThiefInitiated(personRemovesJ: true);
        Assert.Equal(["C1", "C2", "G"], removed.Active(removed.Verifier()));
    }

    // G approves P and C2, P approves C1; C1 and C2 remove G (keeping P, C1 and C2) and C1
    // enrolls N; G, on an old head, removes P, which takes C1.
    internal static World GenesisRemovesApprover()
    {
        var w = new World();
        w.Genesis();
        w.Add("+P", "G", "P", "G");
        w.Confirm("cP", "G", "+P", "+P");
        w.Add("+C2", "G", "C2", "cP");
        w.Confirm("cC2", "G", "+C2", "+C2");
        w.Add("+C1", "P", "C1", "cC2");
        w.Confirm("cC1", "P", "+C1", "+C1");
        w.Remove("M", "C1", "G", "cC1", keep: ["+P", "+C1", "+C2"], cosigner: "C2");
        w.Add("+N", "C1", "N", "M");
        w.Remove("G-P", "G", "+P", "cC1");
        return w;
    }

    [Fact]
    public void A_rank_two_removal_of_the_genesis_shields_what_its_signer_enrolls_after_it()
    {
        var w = GenesisRemovesApprover();
        var v = w.Verifier();
        Assert.True(v.State.IsRevoked(Index(w, v, "+C1")));
        Assert.Equal(["C2", "N"], w.Active(v));
    }

    // Stolen genesis S, on an old branch, adds and confirms J and K, which cosign removals of C1
    // and C2, and adds Y above them; C1 and C2 remove S and enroll replacements.
    internal static World StolenGenesisPair()
    {
        var w = new World();
        w.Genesis("S", "S");
        w.Add("+C1", "S", "C1", "S");
        w.Add("+C2", "S", "C2", "+C1");
        w.Confirm("cC1", "S", "+C1", "+C2");
        w.Confirm("cC2", "S", "+C2", "cC1");
        w.Remove("M", "C1", "S", "cC2", keep: ["+C1", "+C2"], cosigner: "C2");
        w.Add("+R1", "C1", "R1", "M");
        w.Add("+R2", "C2", "R2", "+R1");
        w.Add("+J", "S", "J", "cC2");
        w.Add("+K", "S", "K", "+J");
        w.Confirm("cJ", "S", "+J", "+K");
        w.Confirm("cK", "S", "+K", "cJ");
        w.Remove("JK-C1", "J", "+C1", "cK", cosigner: "K");
        w.Remove("JK-C2", "J", "+C2", "JK-C1", cosigner: "K");
        w.Add("+Y", "S", "Y", "JK-C2");
        return w;
    }

    [Fact]
    public void A_stolen_genesiss_pair_yields_to_the_recovery_and_the_replacements_stay()
    {
        var w = StolenGenesisPair();
        var v = w.Verifier();
        Assert.True(v.State.IsVoid(Index(w, v, "+Y")));
        Assert.True(v.State.IsRevoked(Index(w, v, "+C1")));
        Assert.Equal(["R1", "R2"], w.Active(v));
    }

    // Through a partitioning relay, A and B cosign the removal of genesis G while G removes both
    // on another branch; A adds X after the removal of G.
    internal static World Partitioned(bool confirmed)
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+B", "G", "B", "+A");
        var head = "+B";
        if (confirmed)
        {
            w.Confirm("cA", "G", "+A", "+B");
            w.Confirm("cB", "G", "+B", "cA");
            head = "cB";
        }
        w.Remove("AB-G", "A", "G", head, keep: ["+A", "+B"], cosigner: "B");
        w.Add("+X", "A", "X", "AB-G");
        w.Remove("G-A", "G", "+A", head);
        w.Remove("G-B", "G", "+B", "G-A");
        return w;
    }

    [Fact]
    public void Through_a_partitioning_relay_what_confirmed_removers_add_after_removing_the_genesis_stays()
    {
        var w = Partitioned(confirmed: true);
        Assert.Equal(["X"], w.Active(w.Verifier()));

        var unconfirmed = Partitioned(confirmed: false);
        var offered = unconfirmed.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => unconfirmed.Now), [.. unconfirmed.Labels]);
        Assert.Equal(Invalid.Cosign, offered[unconfirmed.Labels.ToList().IndexOf("AB-G")].Reason);
        Assert.Equal(["G"], unconfirmed.Active(unconfirmed.Verifier()));
    }

    // G adds H1 and H2; on one branch G adds X and H1 copies Y's key, on another G adds Y and H2
    // copies X's key; G joins them.
    internal static World CopiesAcrossIds()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H1", "G", "H1", "G");
        w.Add("+H2", "G", "H2", "+H1");
        w.Add("+X", "G", "X", "+H2");
        w.Add("+Y'", "H1", "Y", "+X", copy: true);
        w.Add("+Y", "G", "Y", "+H2");
        w.Add("+X'", "H2", "X", "+Y", copy: true);
        w.Merge("m", "G", ["+Y'", "+X'"]);
        return w;
    }

    [Fact]
    public void Copies_across_two_ids_resolve_both_conflict_sets_at_once()
    {
        var w = CopiesAcrossIds();
        var v = w.Verifier();
        Assert.Contains(w.Id("X"), v.State.Conflicted);
        Assert.Contains(w.Id("Y"), v.State.Conflicted);
        Assert.Equal(["G", "H1", "H2"], w.Active(v));
    }

    // Stolen S adds Y1 to Y5 and renames on a far fork that every verifier quarantines; C1 and
    // C2 remove S on the parent the client chooses, and a later merge adopts S's branch.
    internal static World TargetCannotChoose()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Confirm("cS", "G", "+S", "+S");
        w.Add("+C1", "G", "C1", "cS");
        w.Add("+C2", "G", "C2", "+C1");
        w.Confirm("cC1", "G", "+C1", "+C2");
        w.Confirm("cC2", "G", "+C2", "cC1");
        var line = "cC2";
        for (var i = 0; i < RosterVerifier.Window + 1; i++) { w.Rename($"r{i}", "G", $"+C{i % 2 + 1}", line, $"Desk {i}"); line = $"r{i}"; }
        var fork = "cS";
        for (var i = 1; i <= 5; i++) { w.Add($"+Y{i}", "S", $"Y{i}", fork); fork = $"+Y{i}"; }
        w.Rename("nS", "S", "+S", fork, "Phone");
        w.Remove("M", "C1", "+S", "cC2", cosigner: "C2");
        w.Merge("tidy", "G", [line, "M", "nS"], adopt: true);
        return w;
    }

    [Fact]
    public void The_target_cannot_choose_the_removals_parent_and_its_quarantined_adds_stay_void_once_adopted()
    {
        var w = TargetCannotChoose();
        var v = w.Verifier([.. w.Labels.TakeWhile(l => l != "M")]);
        Assert.True(v.IsQuarantined(Index(w, v, "+Y1")));
        var choice = RemovalParents.Choose(v, w["+S"].Hash, w.Id("C1"), w.Id("C2"), [], [v.Heads])!;
        Assert.Equal(w["cC2"].Hash, choice.Parent);
        Assert.Empty(choice.Unvoidable);
        w.Feed(v, "M", "tidy");
        Assert.All(Enumerable.Range(1, 5), i => Assert.True(v.State.IsVoid(Index(w, v, $"+Y{i}"))));
        Assert.Equal(["C1", "C2", "G"], w.Active(v));
    }

    // H, on a branch where A is absent, adds a copy of A's key and removes it there; G then joins
    // the branches. The removal names A's id but takes only the copy, so A's own add is never
    // revoked and A wipes nothing (§6.5). It bars A's add only if H was active where that add was
    // made (§3.5), and a bar acts through another add, so A then shows "not active".
    internal static World DuplicateRemoved(bool seenByRemover)
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Confirm("cH", "G", "+H", "+H");
        w.Add("+A", "G", "A", seenByRemover ? "cH" : "G");
        w.Confirm("cA", "G", "+A", "+A");
        w.Add("+A'", "H", "A", "cH", copy: true);
        w.Remove("H-A'", "H", "+A'", "+A'");
        w.Merge("m", "G", ["cA", "H-A'"]);
        return w;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_removal_naming_a_devices_id_that_takes_only_a_copy_never_revokes_its_own_add(bool seenByRemover)
    {
        var w = DuplicateRemoved(seenByRemover);
        var v = w.Verifier();
        Assert.True(v.State.IsRevoked(Index(w, v, "+A'")));
        Assert.False(v.State.IsRevoked(Index(w, v, "+A")));
        Assert.Equal(seenByRemover, v.State.IsBarred(Index(w, v, "+A")));
        Assert.Equal(seenByRemover ? ["G", "H"] : ["A", "G", "H"], w.Active(v));
        w.Remove("late", "G", "+A'", "m");
        Assert.Equal(Invalid.Target, w.Feed(v, "late")[0].Reason);
    }

    // §10's cycle of copies: concurrent approvers A, B and C each add X, over histories holding A
    // and C, A and B, and B and C, so A's add displaces B's, B's displaces C's and C's displaces A's.
    internal static World CopyCycle()
    {
        var w = new World();
        w.Genesis();
        foreach (var d in new[] { "A", "B", "C" })
        {
            w.Add($"+{d}", "G", d, "G");
            w.Confirm($"c{d}", "G", $"+{d}", $"+{d}");
        }
        w.Merge("mAC", "A", ["cA", "cC"]);
        w.Add("aA", "A", "X", "mAC");
        w.Merge("mAB", "B", ["cA", "cB"]);
        w.Add("aB", "B", "X", "mAB");
        w.Merge("mBC", "C", ["cB", "cC"]);
        w.Add("aC", "C", "X", "mBC");
        w.Merge("join", "G", ["aA", "aB", "aC"]);
        return w;
    }

    [Fact]
    public void In_a_cycle_of_copies_no_add_wins_and_with_two_of_them_the_pairwise_winner_is_active()
    {
        var w = CopyCycle();
        var v = w.Verifier();
        Assert.Contains(w.Id("X"), v.State.Conflicted);
        Assert.Null(v.State.ActiveAdd(w.Id("X")));
        Assert.Null(v.State.Device(w.Id("X")));
        var two = w.Verifier([.. w.Labels.Where(l => l is not ("mBC" or "aC" or "join"))]);
        Assert.Equal(Index(w, two, "aA"), two.State.ActiveAdd(w.Id("X")));
        Assert.True(two.State.IsVoid(Index(w, two, "aB")));
    }
}