using AgentBridge.Roster;

// The acceptance rule and the state (§3.3 to §3.5), scenario by scenario from §10.
public sealed class RosterStateTests
{
    static readonly TimeSpan Later = TimeSpan.FromHours(73);

    static void Verdicts(IReadOnlyList<Offered> offered, params Verdict[] expected) =>
        Assert.Equal(expected, offered.Select(o => o.Verdict));

    // G, then A and B added by G in a line, both confirmed.
    internal static World Line()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+B", "G", "B", "+A");
        w.Confirm("cA", "G", "+A", "+B");
        w.Confirm("cB", "G", "+B", "cA");
        return w;
    }

    [Fact]
    public void A_line_of_adds_confirms_and_renames_makes_its_devices_active_and_named()
    {
        var w = Line();
        w.Rename("nB", "A", "+B", "cB", "Kitchen laptop");
        var v = w.Verifier();
        Assert.All(w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now), [.. w.Labels]), o => Assert.Equal(Verdict.Accepted, o.Verdict));
        Assert.Equal(["A", "B", "G"], w.Active(v));
        Assert.Equal("Kitchen laptop", v.State.Name(w.Id("B")));
        Assert.True(v.State.IsConfirmed(v.Graph.Find(w["+A"].Hash)!.Value));
    }

    [Fact]
    public void A_probationary_devices_add_is_held_for_72_hours_of_running_time_or_until_it_is_confirmed()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+C", "A", "C", "+A");
        var v = w.Verifier();
        Assert.Equal(["A", "C", "G"], w.Active(v));
        Assert.Equal(["A", "G"], w.Serving(v));
        w.Now = TimeSpan.FromHours(71);
        Assert.Equal(["A", "G"], w.Serving(v));
        w.Now = Later;
        Assert.Equal(["A", "C", "G"], w.Serving(v));

        w.Now = TimeSpan.Zero;
        w.Confirm("cA", "G", "+A", "+C");
        var confirmed = w.Verifier();
        Assert.Equal(["A", "C", "G"], w.Serving(confirmed));
    }

    [Fact]
    public void A_probationary_devices_removal_of_a_junior_device_applies_at_once()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+C", "A", "C", "+A");
        w.Remove("rC", "A", "+C", "+C");
        var v = w.Verifier();
        Assert.Equal(["A", "G"], w.Active(v));
    }

    [Fact]
    public void Entries_signed_wrongly_by_inactive_devices_or_re_adding_an_id_are_refused()
    {
        var w = Line();
        w.Add("forged", "A", "C", "cB", signAs: "B");
        w.Add("stranger", "Z", "C", "cB");
        w.Add("again", "G", "A", "cB");
        w.Genesis("H", "G2");
        var v = w.Verifier("G", "+A", "+B", "cA", "cB");
        var offered = w.Feed(v, "forged", "stranger", "again", "G2");
        Assert.Equal([Invalid.Signature, Invalid.Signer, Invalid.Readded, Invalid.Genesis], offered.Select(o => o.Reason));

        var orphan = new World();
        orphan.Genesis();
        orphan.Add("+A", "G", "A", "G");
        Assert.Equal(Invalid.Parent, orphan.Feed(orphan.Verifier("+A"), "+A")[0].Reason);
    }

    [Fact]
    public void A_removal_of_a_device_not_junior_to_its_signer_needs_a_confirmed_independent_cosigner()
    {
        var w = Line();
        w.Remove("B-G", "B", "G", "cB");
        w.Remove("A-B", "A", "+B", "cB");
        w.Remove("AB-G", "A", "G", "cB", keep: ["+A", "+B"], cosigner: "B");
        var v = w.Verifier("G", "+A", "+B", "cA", "cB");
        Assert.Equal(Invalid.Seniority, w.Feed(v, "B-G")[0].Reason);
        Verdicts(w.Feed(v, "A-B", "AB-G"), Verdict.Accepted, Verdict.Accepted);
        Assert.Equal(["A"], w.Active(v));
    }

    [Fact]
    public void Unconfirmed_devices_cannot_cosign_a_takeover_in_any_order()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+B", "G", "B", "+A");
        w.Remove("AB-G", "A", "G", "+B", keep: ["+A", "+B"], cosigner: "B");
        Assert.Equal(Invalid.Cosign, w.Verifier("G", "+A", "+B").Offer(w.Entry("AB-G"))[0].Reason);
    }

    [Fact]
    public void Cosigners_that_are_parent_and_child_or_siblings_fail_the_lineage_test()
    {
        var w = Line();
        w.Add("+C", "A", "C", "cB");
        w.Add("+D", "A", "D", "+C");
        w.Confirm("cC", "A", "+C", "+D");
        w.Confirm("cD", "A", "+D", "cC");
        w.Remove("AC-G", "A", "G", "cD", keep: ["+A", "+C"], cosigner: "C");
        w.Remove("CD-G", "C", "G", "cD", keep: ["+A", "+C", "+D"], cosigner: "D");
        w.Remove("CB-G", "C", "G", "cD", keep: ["+A", "+B", "+C"], cosigner: "B");
        var v = w.Verifier("G", "+A", "+B", "cA", "cB", "+C", "+D", "cC", "cD");
        var offered = w.Feed(v, "AC-G", "CD-G", "CB-G");
        Assert.Equal([Invalid.Cosign, Invalid.Cosign, null], offered.Select(o => o.Reason));
    }

    [Fact]
    public void Only_a_devices_own_approver_can_confirm_it()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Confirm("cH", "G", "+H", "+H");
        w.Add("+B", "G", "B", "cH");
        w.Confirm("H-cB", "H", "+B", "+B");
        w.Confirm("cH-again", "G", "+H", "+B");
        var v = w.Verifier("G", "+H", "cH", "+B");
        Assert.Equal([Invalid.Confirm, Invalid.Confirm], w.Feed(v, "H-cB", "cH-again").Select(o => o.Reason));
    }

    // §10: a host that applied B removing C, then receives a concurrent A removing B.
    internal static World Cascade()
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
    public void Concurrent_removals_all_apply_whatever_order_they_arrive_in()
    {
        var w = Cascade();
        Assert.Equal(["A", "G"], w.Active(w.Verifier("G", "+A", "+B", "+C", "B-C", "A-B")));
        Assert.Equal(["A", "G"], w.Active(w.Verifier("G", "+A", "+B", "+C", "A-B", "B-C")));
    }

    internal static World Cycle()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+B", "G", "B", "+A");
        w.Add("+C", "G", "C", "+B");
        w.Add("+D", "G", "D", "+C");
        w.Confirm("cC", "G", "+C", "+D");
        w.Confirm("cD", "G", "+D", "cC");
        w.Remove("A-B", "A", "+B", "cD");
        w.Remove("B-C", "B", "+C", "cD");
        w.Remove("CD-A", "C", "+A", "cD", cosigner: "D");
        return w;
    }

    [Fact]
    public void In_the_three_branch_cycle_A_B_and_C_go_and_D_stays()
    {
        var w = Cycle();
        Assert.Equal(["D", "G"], w.Active(w.Verifier()));
    }

    [Fact]
    public void The_last_two_person_devices_removing_themselves_leave_no_approver()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Remove("G-G", "G", "G", "+A");
        w.Remove("A-A", "A", "+A", "+A");
        var v = w.Verifier();
        Assert.Empty(w.Active(v));
        Assert.Empty(v.State.PersonDevices);
    }

    internal static World Copies()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+H", "G", "H", "G");
        w.Add("+X", "A", "X", "+A");
        w.Add("+X'", "H", "X", "+H", copy: true);
        w.Merge("m", "G", ["+X", "+X'"]);
        w.Remove("G-H", "G", "+H", "m");
        return w;
    }

    [Fact]
    public void A_copy_by_a_device_not_senior_to_the_original_conflicts_and_removing_its_maker_restores_it()
    {
        var w = Copies();
        var conflicted = w.Verifier("G", "+A", "+H", "+X", "+X'");
        Assert.Contains(w.Id("X"), conflicted.State.Conflicted);
        Assert.Equal(["A", "G", "H"], w.Active(conflicted));
        Assert.Equal(["A", "G", "X"], w.Active(w.Verifier()));
    }

    [Fact]
    public void A_copy_of_a_device_its_maker_is_not_senior_to_is_subordinate()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+H", "G", "H", "G");
        w.Add("+A'", "H", "A", "+H", copy: true);
        var v = w.Verifier();
        var genuine = v.Graph.Find(w["+A"].Hash)!.Value;
        Assert.Equal(genuine, v.State.ActiveAdd(w.Id("A")));
        Assert.Equal(genuine, v.State.SubordinateTo[v.Graph.Find(w["+A'"].Hash)!.Value]);
    }

    internal static World Displacement()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+A", "G", "A", "+H");
        w.Add("+X", "A", "X", "+A");
        w.Add("+X'", "H", "X", "+H", copy: true);
        w.Merge("m", "G", ["+X", "+X'"]);
        w.Remove("G-H", "G", "+H", "m");
        return w;
    }

    [Fact]
    public void A_senior_devices_copy_displaces_a_junior_enrollment_until_its_maker_is_removed()
    {
        var w = Displacement();
        var displaced = w.Verifier("G", "+H", "+A", "+X", "+X'", "m");
        var copy = displaced.Graph.Find(w["+X'"].Hash)!.Value;
        Assert.Equal(copy, displaced.State.ActiveAdd(w.Id("X")));
        var v = w.Verifier();
        Assert.Equal(v.Graph.Find(w["+X"].Hash), v.State.ActiveAdd(w.Id("X")));
    }

    internal static World Bars()
    {
        var w = new World();
        w.Genesis();
        w.Add("+C", "G", "C", "G");
        w.Add("+A", "G", "A", "+C");
        w.Add("+B", "G", "B", "+A");
        w.Add("+Xa", "A", "X", "+B");
        w.Add("+Xb", "B", "X", "+B");
        w.Remove("C-Xa", "C", "+Xa", "+Xa");
        return w;
    }

    [Fact]
    public void A_removal_bars_another_add_of_its_targets_key_made_where_its_signer_was_active()
    {
        var w = Bars();
        var v = w.Verifier();
        Assert.DoesNotContain("X", w.Active(v));
        Assert.True(v.State.IsBarred(v.Graph.Find(w["+Xb"].Hash)!.Value));
    }

    internal static World SelfRemoval()
    {
        var w = new World();
        w.Genesis();
        w.Add("+B", "G", "B", "G");
        w.Add("+C", "B", "C", "+B");
        w.Add("+D", "B", "D", "+B");
        w.Remove("G-B", "G", "+B", "+C", keep: ["+C"]);
        w.Remove("B-B", "B", "+B", "+B");
        return w;
    }

    [Fact]
    public void A_self_removal_cannot_void_what_another_removal_of_its_signer_kept()
    {
        var w = SelfRemoval();
        var v = w.Verifier("G", "+B", "+C", "G-B", "B-B");
        Assert.Equal(["C", "G"], w.Active(v));
        var both = w.Verifier("G", "+B", "+C", "+D", "G-B", "B-B");
        Assert.Equal(["C", "G"], w.Active(both));
    }

    internal static World RankOne()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+J", "H", "J", "+H");
        w.Remove("G-H", "G", "+H", "+J", keep: ["+J"]);
        w.Remove("H-J", "H", "+J", "+J");
        w.Add("+K", "J", "K", "G-H");
        return w;
    }

    [Fact]
    public void Rank_one_against_rank_one_the_removal_that_revokes_the_others_signer_shields_its_descendants()
    {
        var w = RankOne();
        var v = w.Verifier();
        Assert.Equal(["G", "K"], w.Active(v));
    }

    internal static World Window(int unseen)
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        var last = "+H";
        for (var i = 0; i < unseen; i++) { w.Add($"+D{i}", "G", $"D{i}", last); last = $"+D{i}"; }
        w.Add("+Y", "H", "Y", "+H");
        return w;
    }

    [Fact]
    public void An_add_whose_parent_has_8_unseen_entries_is_within_the_window_and_one_with_9_is_quarantined()
    {
        var eight = Window(8);
        Assert.Equal(Verdict.Accepted, eight.Feed(eight.Verifier([.. eight.Labels.SkipLast(1)]), "+Y")[0].Verdict);
        var nine = Window(9);
        var v = nine.Verifier([.. nine.Labels.SkipLast(1)]);
        Assert.Equal(Verdict.Quarantined, nine.Feed(v, "+Y")[0].Verdict);
        Assert.DoesNotContain("Y", nine.Active(v));
    }

    [Fact]
    public void Quarantine_is_inherited_until_an_adopting_merge_adopts_the_branch()
    {
        var w = Window(9);
        w.Rename("nY", "H", "+Y", "+Y", "Yonder");
        w.Merge("tidy", "G", ["+D8", "nY"], adopt: true);
        var v = w.Verifier([.. w.Labels.Take(w.Labels.Count - 3)]);
        Verdicts(w.Feed(v, "+Y", "nY"), Verdict.Quarantined, Verdict.Quarantined);
        Assert.DoesNotContain("Y", w.Active(v));
        Assert.Equal(Verdict.Accepted, w.Feed(v, "tidy")[0].Verdict);
        Assert.Contains("Y", w.Active(v));
        Assert.Equal("Yonder", v.State.Name(w.Id("Y")));
    }

    [Fact]
    public void A_removal_on_a_quarantined_branch_applies_when_it_is_valid_without_it()
    {
        var w = Window(9);
        w.Remove("G-H", "G", "+H", "+Y");
        var v = w.Verifier([.. w.Labels.SkipLast(1)]);
        Assert.Equal(Verdict.Quarantined, w.Feed(v, "G-H")[0].Verdict);
        Assert.DoesNotContain("H", w.Active(v));
        Assert.DoesNotContain("Y", w.Active(v));
        Assert.Contains("D8", w.Active(v));
    }

    [Fact]
    public void The_ninth_rename_of_one_add_is_refused()
    {
        var w = Line();
        var last = "cB";
        for (var i = 0; i < 9; i++) { w.Rename($"n{i}", "G", "+B", last, $"Name {i}"); last = $"n{i}"; }
        var offered = w.Verifier("G", "+A", "+B", "cA", "cB").Offer([.. Enumerable.Range(0, 9).Select(i => w.Entry($"n{i}"))]);
        Assert.Equal(Invalid.Budget, offered[^1].Reason);
        Assert.All(offered.SkipLast(1), o => Assert.Equal(Verdict.Accepted, o.Verdict));
    }
}
