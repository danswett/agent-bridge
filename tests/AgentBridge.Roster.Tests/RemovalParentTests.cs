using AgentBridge.Roster;

// Where the remover's client signs a removal (§3.3): as little of the target's as possible under
// it, everything it needs, and only what every attested holder has, when that is enough.
public sealed class RemovalParentTests
{
    static string LabelOf(World w, byte[] hash) => w.Labels.First(l => w[l].Hash.AsSpan().SequenceEqual(hash));

    static RemovalParent Choose(World w, RosterVerifier v, string targetAdd, string by, string? cosigner = null, string[]? keep = null, params string[][] holders) =>
        RemovalParents.Choose(v, w[targetAdd].Hash, w.Id(by), cosigner is null ? null : w.Id(cosigner),
            [.. (keep ?? []).Select(k => w[k].Hash)], [.. holders.Select(h => (IReadOnlyCollection<byte[]>)[.. h.Select(x => w[x].Hash)])])!;

    [Fact]
    public void A_removal_is_signed_below_everything_its_target_granted_which_it_then_voids()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Add("+Y", "S", "Y", "+S");
        w.Rename("nY", "S", "+Y", "+Y", "Spare");
        var v = w.Verifier();
        var choice = Choose(w, v, "+S", "G", holders: [["nY"]]);
        Assert.Equal("+S", LabelOf(w, choice.Parent));
        Assert.Empty(choice.Unvoidable);
        Assert.Empty(choice.Lacking);
        w.Remove("G-S", "G", "+S", "+S");
        Assert.Equal(["G"], w.Active(w.Verifier()));
    }

    [Fact]
    public void A_kept_add_and_what_it_needs_stay_under_the_removal_and_the_rest_does_not()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Add("+E", "S", "E", "+S");
        w.Add("+Y", "S", "Y", "+E");
        var v = w.Verifier();
        var choice = Choose(w, v, "+S", "G", keep: ["+E"], holders: [["+Y"]]);
        Assert.Equal("+E", LabelOf(w, choice.Parent));
        Assert.Empty(choice.Unvoidable);
        w.Remove("G-S", "G", "+S", "+E", keep: ["+E"]);
        Assert.Equal(["E", "G"], w.Active(w.Verifier()));
    }

    [Fact]
    public void A_cosigned_removal_needs_its_signers_confirms_and_says_which_grants_it_cannot_void()
    {
        var w = RosterRecoveryTests.ForkBeforeRecovery();
        var v = w.Verifier([.. w.Labels.TakeWhile(l => l != "M")]);
        var choice = Choose(w, v, "+S", "C1", "C2", holders: [["cC2"]]);
        // The confirms of C1 and C2 came after the thief's grants on the honest line.
        Assert.Equal("cC2", LabelOf(w, choice.Parent));
        Assert.Equal(["+J", "+K", "cJ", "cK"], choice.Unvoidable.Select(h => LabelOf(w, h)).Order(StringComparer.Ordinal));
        Assert.Empty(choice.Lacking);
    }

    [Fact]
    public void A_signer_confirmed_after_a_hosts_last_attestation_is_sent_to_it_with_the_removal()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        w.Add("+C", "G", "C", "+S");
        w.Confirm("cC", "G", "+C", "+C");
        w.Confirm("cS", "G", "+S", "cC");
        var v = w.Verifier();
        // The first host last attested +C; the relay holds everything.
        var choice = Choose(w, v, "+S", "C", "G", holders: [["+C"], ["cS"]]);
        Assert.Equal("cC", LabelOf(w, choice.Parent));
        Assert.Equal([0], choice.Lacking);
    }

    [Fact]
    public void Nothing_is_chosen_for_a_target_the_remover_does_not_hold()
    {
        var w = new World();
        w.Genesis();
        w.Add("+S", "G", "S", "G");
        var v = w.Verifier("G");
        Assert.Null(RemovalParents.Choose(v, w["+S"].Hash, w.Id("G"), null, [], []));
    }
}
