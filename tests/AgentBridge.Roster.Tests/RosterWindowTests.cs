using AgentBridge.Roster;

// More §10 scenarios: the window cannot be refreshed, restoring past the active-device cap, a
// barred enrollment, revocation through a raw chain, and a cascade that voids another id's winner.
public sealed class RosterWindowTests
{
    static int Index(World w, RosterVerifier v, string label) => v.Graph.Find(w[label].Hash)!.Value;

    // H adds J (P); the line goes 30 entries on; H then removes J on P and adds a copy of D0's key
    // on its removal, or renames J on P first and adds on that; G removes D1 on P.
    internal static World Unrefreshed(bool removalFirst)
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+J", "H", "J", "+H");
        var last = "+J";
        for (var i = 0; i < 30; i++) { w.Add($"+D{i}", "G", $"D{i}", last); last = $"+D{i}"; }
        if (removalFirst) { w.Remove("H-J", "H", "+J", "+J"); }
        else { w.Rename("nJ", "H", "+J", "+J", "Spare"); }
        w.Add("+X", "H", "X", removalFirst ? "H-J" : "nJ");
        w.Remove("G-D1", "G", "+D1", "+D1");
        return w;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_entry_on_one_signed_on_an_old_parent_is_as_far_outside_the_window_as_that_parent(bool removalFirst)
    {
        var w = Unrefreshed(removalFirst);
        var offered = w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now), [.. w.Labels]);
        var at = w.Labels.ToList();
        Assert.Equal(removalFirst ? Verdict.Accepted : Verdict.Quarantined, offered[at.Count - 3].Verdict);
        Assert.Equal(Verdict.Quarantined, offered[at.IndexOf("+X")].Verdict);
        // A removal from as far back is applied.
        Assert.Equal(Verdict.Accepted, offered[at.IndexOf("G-D1")].Verdict);
        var v = w.Verifier();
        Assert.DoesNotContain("X", w.Active(v));
        Assert.DoesNotContain("D1", w.Active(v));
    }

    // 64 active: G, A, H and 61 more; A's ten adds conflict with H's copies of them on a sibling
    // branch, G approves ten replacements, then removes H.
    static World PastTheCap()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Add("+H", "G", "H", "G");
        var line = "+A";
        for (var i = 0; i < 10; i++) { w.Add($"+N{i}", "A", $"N{i}", line); line = $"+N{i}"; }
        for (var i = 0; i < 51; i++) { w.Add($"+M{i}", "G", $"M{i}", line); line = $"+M{i}"; }
        var copies = "+H";
        for (var i = 0; i < 10; i++) { w.Add($"+N{i}'", "H", $"N{i}", copies, copy: true); copies = $"+N{i}'"; }
        w.Merge("m", "G", [line, copies], adopt: true);
        line = "m";
        for (var i = 0; i < 10; i++) { w.Add($"+R{i}", "G", $"R{i}", line); line = $"+R{i}"; }
        w.Add("+Z", "G", "Z", line);
        w.Remove("G-H", "G", "+H", line);
        w.Add("+Z2", "G", "Z2", "G-H");
        w.Remove("G-M0", "G", "+M0", "G-H");
        return w;
    }

    [Fact]
    public void Removing_a_copier_restores_the_enrollments_its_copies_disabled_even_past_the_active_device_cap()
    {
        var w = PastTheCap();
        var order = w.Labels.ToList();
        // The copies first, as a host that saw H's branch first would have them, and in order.
        var copiesFirst = order.Where(l => l.EndsWith('\'')).Concat(order.Where(l => !l.EndsWith('\''))).ToList();
        copiesFirst.Remove("+H");
        copiesFirst.Remove("G");
        copiesFirst.InsertRange(0, ["G", "+H"]);
        foreach (var labels in new[] { order, copiesFirst })
        {
            var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
            var offered = w.Feed(v, [.. labels]);
            Assert.Equal(Invalid.ActiveCap, offered[labels.IndexOf("+Z")].Reason);
            Assert.Equal(Invalid.ActiveCap, offered[labels.IndexOf("+Z2")].Reason);
            Assert.Equal(Verdict.Accepted, offered[labels.IndexOf("G-M0")].Verdict);
            Assert.Equal(72, v.State.ActiveCount);
            Assert.Contains("N0", w.Active(v));
        }
        var before = w.Verifier([.. order.TakeWhile(l => l != "+Z")]);
        Assert.Equal(64, before.State.ActiveCount);
        Assert.DoesNotContain("N0", w.Active(before));
    }

    // The bar example: X, active only through B's add, signs add(Y) on B's branch.
    internal static World BarredGrants()
    {
        var w = RosterStateTests.Bars();
        w.Add("+Y", "X", "Y", "+Xb");
        return w;
    }

    [Fact]
    public void A_barred_enrollment_grants_nothing()
    {
        var w = BarredGrants();
        var v = w.Verifier();
        Assert.True(v.State.IsVoid(Index(w, v, "+Y")));
        Assert.DoesNotContain("Y", w.Active(v));
    }

    // G adds P; on a branch G's removal of P never saw, P adds Q and G adds W above it; Q removes
    // W; G joins the heads and W enrolls Z.
    internal static World RawChain()
    {
        var w = new World();
        w.Genesis();
        w.Add("+P", "G", "P", "G");
        w.Remove("G-P", "G", "+P", "+P");
        w.Add("+Q", "P", "Q", "+P");
        w.Add("+W", "G", "W", "+Q");
        w.Remove("Q-W", "Q", "+W", "+W");
        w.Merge("m", "G", ["G-P", "+W"]);
        w.Add("+Z", "W", "Z", "m");
        return w;
    }

    [Fact]
    public void A_device_in_a_removals_raw_chain_counts_as_revoked_by_it_so_its_removal_yields()
    {
        var w = RawChain();
        var v = w.Verifier();
        Assert.True(v.State.IsRevoked(Index(w, v, "+W")));
        Assert.Equal(["G", "Z"], w.Active(v));
    }

    // X adds A; H, senior to A, copies A's key where X is absent, so the copy wins and A's own add
    // is void in step 4. A's add Y1 had won Y over B's Y2, made on a branch from A's add. G then
    // removes H.
    internal static World CascadeVoidsWinner(bool removeCopier)
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+X", "G", "X", "+H");
        w.Add("+A", "X", "A", "+X");
        w.Add("+Y1", "A", "Y", "+A");
        w.Add("+B", "G", "B", "+A");
        w.Add("+Y2", "B", "Y", "+B");
        w.Add("+A'", "H", "A", "+H", copy: true);
        w.Merge("m", "G", ["+Y1", "+Y2", "+A'"]);
        if (removeCopier) { w.Remove("G-H", "G", "+H", "m"); }
        return w;
    }

    [Fact]
    public void A_cascade_that_voids_an_ids_winner_leaves_the_id_inactive_until_the_copier_is_removed()
    {
        var w = CascadeVoidsWinner(removeCopier: false);
        var v = w.Verifier();
        Assert.True(v.State.IsVoid(Index(w, v, "+Y1")));
        Assert.Equal(Index(w, v, "+A'"), v.State.ActiveAdd(w.Id("A")));
        Assert.DoesNotContain("Y", w.Active(v));

        var removed = CascadeVoidsWinner(removeCopier: true);
        var r = removed.Verifier();
        Assert.Equal(Index(removed, r, "+A"), r.State.ActiveAdd(removed.Id("A")));
        Assert.Equal(Index(removed, r, "+Y1"), r.State.ActiveAdd(removed.Id("Y")));
        Assert.Equal(["A", "B", "G", "X", "Y"], removed.Active(r));
    }
}
