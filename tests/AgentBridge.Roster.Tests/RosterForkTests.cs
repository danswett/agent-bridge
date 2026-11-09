using AgentBridge.Roster;

// The §10 removal and fork vectors: monotone removals, old-head removals, recovery through the
// genesis, hidden heads, concurrent renames, removals forking from incomparable points, and
// self-removals after a recovery.
public sealed class RosterForkTests
{
    static readonly TimeSpan Later = TimeSpan.FromHours(73);

    static int Index(World w, RosterVerifier v, string label) => v.Graph.Find(w[label].Hash)!.Value;

    // A host that applied B removing C receives a concurrent A removing B.
    internal static World Monotone()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+B", "A", "B", "+A");
        w.Add("+C", "B", "C", "+B");
        w.Remove("B-C", "B", "+C", "+C");
        w.Remove("A-B", "A", "+B", "+C");
        return w;
    }

    [Fact]
    public void A_removal_stays_applied_when_a_concurrent_removal_of_its_signer_arrives()
    {
        var w = Monotone();
        Assert.Equal(["A", "B", "G"], w.Active(w.Verifier([.. w.Labels.SkipLast(1)])));
        Assert.Equal(["A", "G"], w.Active(w.Verifier()));
    }

    // G removes H keeping J; H, on an old head, removes J; G then adds K.
    internal static World OldHeadRemoval()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+J", "H", "J", "+H");
        w.Remove("G-H", "G", "+H", "+J", keep: ["+J"]);
        w.Remove("H-J", "H", "+J", "+J");
        w.Add("+K", "G", "K", "G-H");
        return w;
    }

    [Fact]
    public void A_removed_devices_old_head_removal_applies_to_its_junior_and_cannot_touch_a_device_added_later()
    {
        var w = OldHeadRemoval();
        Assert.Equal(["G", "K"], w.Active(w.Verifier()));
    }

    // Confirmed C1 and C2 cosign the removal of senior S, C1 adds Y after it, and the person
    // removes C1 and C2 on a head before it.
    internal static World PersonRemovesCosigners()
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
        w.Add("+Y", "C1", "Y", "M");
        w.Remove("G-C1", "G", "+C1", "cC2");
        w.Remove("G-C2", "G", "+C2", "G-C1");
        return w;
    }

    [Fact]
    public void A_cosigned_removal_stays_applied_when_the_person_removes_both_its_signers()
    {
        var w = PersonRemovesCosigners();
        var v = w.Verifier();
        Assert.True(v.State.IsRevoked(Index(w, v, "+S")));
        Assert.True(v.State.IsVoid(Index(w, v, "+Y")));
        Assert.Equal(["G"], w.Active(v));
    }

    // C1 and C2, made and confirmed by genesis S, remove S keeping themselves and enroll R1 and
    // R2; S, on an old head, removes C1 and C2.
    internal static World GenesisRecovery()
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
        w.Remove("S-C1", "S", "+C1", "cC2");
        w.Remove("S-C2", "S", "+C2", "S-C1");
        return w;
    }

    [Fact]
    public void With_the_genesis_removed_the_generation_continues_through_the_replacements()
    {
        var w = GenesisRecovery();
        Assert.Equal(["R1", "R2"], w.Active(w.Verifier()));
    }

    // G enrolls H, and N on a branch that hides H; G merges them, and N removes H alone.
    internal static World HiddenHeads()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+H", "G", "H", "+A");
        w.Add("+N", "G", "N", "+A");
        w.Merge("m", "G", ["+H", "+N"]);
        w.Remove("N-H", "N", "+H", "m");
        return w;
    }

    [Fact]
    public void A_device_enrolled_on_a_branch_that_hid_another_is_not_senior_to_it_even_after_a_merge()
    {
        var w = HiddenHeads();
        w.Now = Later;
        var v = w.Verifier([.. w.Labels.SkipLast(1)]);
        Assert.Equal(Invalid.Seniority, w.Feed(v, "N-H")[0].Reason);
    }

    // Established A and B rename junior J on sibling branches, then G merges them and renames J.
    internal static World ConcurrentRenames(bool supersede)
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+B", "G", "B", "+A");
        w.Add("+J", "G", "J", "+B");
        w.Confirm("cA", "G", "+A", "+J");
        w.Confirm("cB", "G", "+B", "cA");
        w.Rename("nA", "A", "+J", "cB", "Alpha");
        w.Rename("nB", "B", "+J", "cB", "Beta");
        w.Merge("m", "G", ["nA", "nB"]);
        if (supersede) { w.Rename("nG", "G", "+J", "m", "Gamma"); }
        return w;
    }

    [Fact]
    public void Of_two_concurrent_renames_the_smaller_hash_wins_until_a_rename_descending_from_both()
    {
        var w = ConcurrentRenames(supersede: false);
        var v = w.Verifier();
        var smaller = Ids.HeadOrder.Compare(w["nA"].Hash, w["nB"].Hash) < 0 ? "Alpha" : "Beta";
        Assert.Equal(smaller, v.Name(w.Id("J")));
        Assert.Contains(w.Id("J"), v.State.RenamedConcurrently);
        Assert.Equal(smaller, w.Verifier("G", "+A", "+B", "+J", "cA", "cB", "nB", "nA", "m").Name(w.Id("J")));

        var later = ConcurrentRenames(supersede: true);
        var after = later.Verifier();
        Assert.Equal("Gamma", after.Name(later.Id("J")));
        Assert.DoesNotContain(later.Id("J"), after.State.RenamedConcurrently);
    }

    // H, concurrent with A, adds A's key on its own branch and renames that copy eight times.
    internal static World CopyRenames()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+H", "G", "H", "G");
        w.Add("+A'", "H", "A", "+H", copy: true);
        var last = "+A'";
        for (var i = 0; i < RosterVerifier.RenamesPerAdd; i++) { w.Rename($"h{i}", "H", "+A'", last, $"Copy {i}"); last = $"h{i}"; }
        w.Merge("m", "G", ["+A", last]);
        last = "m";
        for (var i = 0; i < RosterVerifier.RenamesPerAdd; i++) { w.Rename($"g{i}", "G", "+A", last, $"Laptop {i}"); last = $"g{i}"; }
        return w;
    }

    [Fact]
    public void A_copys_renames_neither_rename_the_device_nor_spend_its_renames()
    {
        var w = CopyRenames();
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
        var offered = w.Feed(v, [.. w.Labels.TakeWhile(l => l != "g0")]);
        Assert.Equal("A", v.Name(w.Id("A")));
        Assert.Equal(Index(w, v, "+A"), v.State.ActiveAdd(w.Id("A")));
        Assert.All(w.Feed(v, [.. w.Labels.SkipWhile(l => l != "g0")]), o => Assert.Equal(Verdict.Accepted, o.Verdict));
        Assert.Equal("Laptop 7", v.Name(w.Id("A")));
    }

    // Removals of T forking from two incomparable points, each concurrent with a different add
    // by T.
    internal static World TwoForkRemovals()
    {
        var w = new World();
        w.Genesis();
        w.Add("+T", "G", "T", "G");
        w.Add("+A", "G", "A", "+T");
        w.Add("+B", "G", "B", "+T");
        w.Add("+X", "T", "X", "+A");
        w.Add("+Y", "T", "Y", "+B");
        w.Remove("R1", "G", "+T", "+A");
        w.Remove("R2", "G", "+T", "+B");
        return w;
    }

    [Fact]
    public void Removals_forking_from_incomparable_points_are_both_kept_and_void_both_adds()
    {
        var w = TwoForkRemovals();
        var v = w.Verifier();
        Assert.True(v.State.IsVoid(Index(w, v, "+X")));
        Assert.True(v.State.IsVoid(Index(w, v, "+Y")));
        Assert.Equal(["A", "B", "G"], w.Active(v));
    }

    // Removed H adds a replacement's real key on an old head and removes that add.
    internal static World ReplacementKey()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Remove("G-H", "G", "+H", "+H");
        w.Add("+N", "G", "N", "G-H");
        w.Add("+N'", "H", "N", "+H");
        w.Remove("H-N'", "H", "+N'", "+N'");
        return w;
    }

    [Fact]
    public void A_removed_devices_add_of_a_replacements_key_and_its_removal_are_inert()
    {
        var w = ReplacementKey();
        var v = w.Verifier();
        Assert.Equal(["G", "N"], w.Active(v));
        Assert.Empty(v.State.Conflicted);
        Assert.Equal(Index(w, v, "+N"), v.State.ActiveAdd(w.Id("N")));
    }

    // Probationary B makes a held add of C, which adds D; the remover has synced C's add.
    internal static World HeldAdd(bool keep)
    {
        var w = new World();
        w.Genesis();
        w.Add("+B", "G", "B", "G");
        w.Add("+C", "B", "C", "+B");
        w.Add("+D", "C", "D", "+C");
        w.Remove("G-B", "G", "+B", "+C", keep: keep ? ["+C"] : null);
        return w;
    }

    [Fact]
    public void A_removal_that_does_not_keep_a_held_add_it_holds_takes_it_and_one_that_keeps_it_leaves_it_held()
    {
        var taken = HeldAdd(keep: false);
        var v = taken.Verifier();
        Assert.True(v.State.IsRevoked(Index(taken, v, "+C")));
        Assert.True(v.State.IsVoid(Index(taken, v, "+D")));
        Assert.Equal(["G"], taken.Active(v));

        var kept = HeldAdd(keep: true);
        var k = kept.Verifier();
        Assert.Equal(["C", "D", "G"], kept.Active(k));
        Assert.Equal(["G"], kept.Serving(k));
        kept.Now = Later;
        Assert.Equal(["C", "D", "G"], kept.Serving(k));
    }

    // C1 and C2 remove senior S (the genesis, or a device it approved) and enroll R1 and R2; S
    // then removes itself on the entry right after its own add.
    internal static World SelfRemovalAfterRecovery(bool genesis)
    {
        var w = new World();
        string s;
        if (genesis)
        {
            w.Genesis();
            s = "G";
        }
        else
        {
            w.Genesis();
            w.Add("+S", "G", "S", "G");
            w.Confirm("cS", "G", "+S", "+S");
            s = "+S";
        }
        var by = genesis ? "G" : "S";
        var at = genesis ? "G" : "cS";
        w.Add("+C1", by, "C1", at);
        w.Add("+C2", by, "C2", "+C1");
        w.Confirm("cC1", by, "+C1", "+C2");
        w.Confirm("cC2", by, "+C2", "cC1");
        w.Remove("M", "C1", s, "cC2", keep: ["+C1", "+C2"], cosigner: "C2");
        w.Add("+R1", "C1", "R1", "M");
        w.Add("+R2", "C2", "R2", "+R1");
        w.Remove("self", by, s, "+C1");
        return w;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_senior_devices_self_removal_does_not_void_the_adds_of_those_who_removed_it(bool genesis)
    {
        var w = SelfRemovalAfterRecovery(genesis);
        var v = w.Verifier();
        string[] expected = genesis ? ["C1", "C2", "R1", "R2"] : ["C1", "C2", "G", "R1", "R2"];
        Assert.Equal(expected, w.Active(v));
    }

    // P confirms B while G removes P keeping B, concurrently.
    internal static World VoidConfirm()
    {
        var w = new World();
        w.Genesis();
        w.Add("+P", "G", "P", "G");
        w.Confirm("cP", "G", "+P", "+P");
        w.Add("+B", "P", "B", "cP");
        w.Remove("G-P", "G", "+P", "+B", keep: ["+B"]);
        w.Confirm("cB", "P", "+B", "+B");
        return w;
    }

    [Fact]
    public void A_confirm_concurrent_with_the_removal_of_its_signer_confirms_nothing()
    {
        var w = VoidConfirm();
        var v = w.Verifier();
        Assert.Equal(["B", "G"], w.Active(v));
        Assert.False(v.State.IsConfirmed(Index(w, v, "+B")));
    }
}
