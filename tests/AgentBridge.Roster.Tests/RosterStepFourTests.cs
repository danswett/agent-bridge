using AgentBridge.Roster;

// §10's step-4 scenarios: an add made subordinate by a senior device's copy, what that does to
// an id the add's device had a say in, and a removal of such an add.
public sealed class RosterStepFourTests
{
    static int Index(World w, RosterVerifier v, string label) => v.Graph.Find(w[label].Hash)!.Value;

    // X has adds X1 by A and X2 by K, neither senior to the other's, so X is conflicted. H, senior
    // to A, copies A's key where Q (A's approver) is absent, so A's add is subordinate in step 4.
    internal static World NoFeedback(bool removeCopier)
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+Q", "G", "Q", "+H");
        w.Add("+A", "Q", "A", "+Q");
        w.Add("+X1", "A", "X", "+A");
        w.Add("+K", "G", "K", "G");
        w.Add("+X2", "K", "X", "+K");
        w.Add("+A'", "H", "A", "+H", copy: true);
        w.Merge("m", "G", ["+X1", "+X2", "+A'"]);
        if (removeCopier) { w.Remove("G-H", "G", "+H", "m"); }
        return w;
    }

    [Fact]
    public void Step_four_does_not_feed_back_into_a_conflict_its_void_add_was_part_of()
    {
        var w = NoFeedback(removeCopier: false);
        var v = w.Verifier();
        Assert.True(v.State.IsVoid(Index(w, v, "+X1")));
        Assert.Contains(w.Id("X"), v.State.Conflicted);
        Assert.DoesNotContain("X", w.Active(v));

        var removed = NoFeedback(removeCopier: true);
        var r = removed.Verifier();
        Assert.False(r.State.IsVoid(Index(removed, r, "+X1")));
        Assert.Contains(removed.Id("X"), r.State.Conflicted);
        Assert.DoesNotContain("X", removed.Active(r));
    }

    // C removes A's add of X, which H's copy of X's key makes subordinate in step 4: with C
    // active where H made the copy, or not.
    internal static World RemovedSubordinate(bool cSawCopy)
    {
        var w = new World();
        w.Genesis();
        if (cSawCopy)
        {
            w.Add("+C", "G", "C", "G");
            w.Add("+H", "G", "H", "+C");
            w.Add("+A", "G", "A", "+H");
            w.Add("+X", "A", "X", "+A");
        }
        else
        {
            w.Add("+H", "G", "H", "G");
            w.Add("+A", "G", "A", "+H");
            w.Add("+C", "G", "C", "+A");
            w.Add("+X", "A", "X", "+C");
        }
        w.Add("+X'", "H", "X", "+H", copy: true);
        w.Remove("C-X", "C", "+X", "+X");
        w.Merge("m", "G", ["C-X", "+X'"]);
        return w;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_removal_of_an_add_step_four_subordinates_still_revokes_it_and_bars_the_copy_only_where_its_signer_was_active(bool cSawCopy)
    {
        var w = RemovedSubordinate(cSawCopy);
        var v = w.Verifier();
        Assert.True(v.State.IsRevoked(Index(w, v, "+X")));
        Assert.Equal(cSawCopy, v.State.IsBarred(Index(w, v, "+X'")));
        Assert.Equal(cSawCopy ? null : Index(w, v, "+X'"), v.State.ActiveAdd(w.Id("X")));
    }
}
