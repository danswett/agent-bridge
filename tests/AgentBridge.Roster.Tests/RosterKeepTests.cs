using AgentBridge.Roster;

// The §10 scenarios where copies meet keep lists: a copy cannot be kept through its subtree, a
// copy kept where the original can't be seen conflicts with it, and an outside copy wins against
// a kept add until the person's removal voids it.
public sealed class RosterKeepTests
{
    static int Index(World w, RosterVerifier v, string label) => v.Graph.Find(w[label].Hash)!.Value;

    // S approves J and E1 on concurrent branches, and E1 approves E2. On J's branch J adds E2's
    // own key (its dh and sign), and E2's device enrolls E3 through that add. G joins the heads.
    internal static World CopyThroughSubtree()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Add("+J", "S", "J", "+S");
        w.Add("+E1", "S", "E1", "+S");
        w.Add("+E2", "E1", "E2", "+E1");
        w.Add("+E2'", "J", "E2", "+J");
        w.Add("+E3", "E2", "E3", "+E2'");
        w.Merge("m", "G", ["+E2", "+E3"]);
        w.Remove("keep-E3", "G", "+S", "m", keep: ["+E3"]);
        w.Remove("keep-copy", "G", "+S", "m", keep: ["+J", "+E2'"]);
        w.Remove("keep-E2", "G", "+S", "m", keep: ["+E1", "+E2"]);
        w.Remove("G-S", "G", "+S", "m", keep: ["+E1"]);
        return w;
    }

    [Fact]
    public void A_copy_cannot_be_kept_through_its_subtree_and_the_removal_keeping_its_approver_takes_it()
    {
        var w = CopyThroughSubtree();
        var v = w.Verifier([.. w.Labels.TakeWhile(l => l != "keep-E3")]);
        Assert.Contains(w.Id("E2"), v.State.Conflicted);
        var offered = w.Feed(v, "keep-E3", "keep-copy", "keep-E2", "G-S");
        Assert.Equal([Invalid.Keep, Invalid.Keep, Invalid.Keep, null], offered.Select(o => o.Reason));
        Assert.Equal(["E1", "G"], w.Active(v));
    }

    // G adds H, then A; A enrolls X. On a branch from before A's add, H copies X's key, and G
    // removes H there keeping the copy, which that history shows as X's only add. G joins them.
    internal static World KeptCopy()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+A", "G", "A", "+H");
        w.Add("+X", "A", "X", "+A");
        w.Add("+X'", "H", "X", "+H", copy: true);
        w.Remove("G-H", "G", "+H", "+X'", keep: ["+X'"]);
        w.Merge("m", "G", ["+X", "G-H"]);
        return w;
    }

    [Fact]
    public void A_copy_kept_where_the_original_cannot_be_seen_conflicts_with_it_once_the_branches_join()
    {
        var w = KeptCopy();
        var v = w.Verifier();
        Assert.Contains(w.Id("X"), v.State.Conflicted);
        Assert.Equal(["A", "G"], w.Active(v));
    }

    // G approves C2, S and then P, and P approves C1; C1 and C2 remove P keeping C1 (R). S copies
    // C1's key on a parent after its own add and before P's, and adopts it with a merge. The
    // person then removes S on a parent from before the copy.
    internal static World OutsideCopy(bool personRemovesS)
    {
        var w = new World();
        w.Genesis();
        w.Add("+C2", "G", "C2", "G");
        w.Add("+S", "G", "S", "+C2");
        w.Add("+P", "G", "P", "+S");
        w.Confirm("cP", "G", "+P", "+P");
        w.Add("+C1", "P", "C1", "cP");
        w.Confirm("cC1", "P", "+C1", "+C1");
        w.Confirm("cC2", "G", "+C2", "cC1");
        w.Remove("R", "C1", "+P", "cC2", keep: ["+C1"], cosigner: "C2");
        w.Add("+C1'", "S", "C1", "+S", copy: true);
        w.Merge("adopt", "S", ["R", "+C1'"], adopt: true);
        if (personRemovesS) { w.Remove("M", "C1", "+S", "R", cosigner: "C2"); }
        return w;
    }

    [Fact]
    public void An_outside_copy_wins_against_a_kept_add_until_the_persons_removal_of_its_maker_voids_it()
    {
        var w = OutsideCopy(personRemovesS: false);
        var v = w.Verifier();
        Assert.Equal(Index(w, v, "+C1'"), v.State.ActiveAdd(w.Id("C1")));

        var removed = OutsideCopy(personRemovesS: true);
        var r = removed.Verifier();
        Assert.True(r.State.IsVoid(Index(removed, r, "+C1'")));
        Assert.Equal(Index(removed, r, "+C1"), r.State.ActiveAdd(removed.Id("C1")));
        Assert.Equal(["C1", "C2", "G"], removed.Active(r));
    }
}
