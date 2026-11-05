using AgentBridge.Roster;

// The properties §3.5 promises and the review used: the state is the same in every delivery
// order of the same entries (commutativity, convergence), and every entry is judged in its
// parents' state alone, so what a verifier holding more decides about it is what one holding only
// its ancestors decides (prefix reduction).
public sealed class RosterPropertyTests
{
    public static TheoryData<string> Scenarios => new(Builders.Keys);

    internal static readonly Dictionary<string, Func<World>> Builders = new()
    {
        ["line"] = () =>
        {
            var w = RosterStateTests.Line();
            w.Rename("nB", "A", "+B", "cB", "Kitchen laptop");
            return w;
        },
        ["cascade"] = RosterStateTests.Cascade,
        ["cycle"] = RosterStateTests.Cycle,
        ["copies"] = RosterStateTests.Copies,
        ["displacement"] = RosterStateTests.Displacement,
        ["bars"] = RosterStateTests.Bars,
        ["self-removal"] = RosterStateTests.SelfRemoval,
        ["rank-one"] = RosterStateTests.RankOne,
        ["fabricated-cosigners"] = RosterRecoveryTests.FabricatedCosigners,
        ["fork-before-recovery"] = () => RosterRecoveryTests.ForkBeforeRecovery(),
        ["fork-from-recovery"] = () => RosterRecoveryTests.ForkBeforeRecovery(fromRecovery: true),
        ["one-device-recovery"] = () => RosterRecoveryTests.ForkBeforeRecovery(cosigned: false),
        ["kept-cannot-shield"] = RosterRecoveryTests.KeptCannotShield,
        ["reciprocal"] = RosterRecoveryTests.Reciprocal,
        ["tie"] = RosterRecoveryTests.Tie,
        ["mutual"] = RosterRecoveryTests.Mutual,
        ["kept-own-adds"] = RosterPrecedenceTests.KeptOwnAdds,
        ["thief-keep-list"] = RosterPrecedenceTests.ThiefKeepList,
        ["wiping"] = () => RosterPrecedenceTests.Wiping(personKeeps: false),
        ["wiping-kept"] = () => RosterPrecedenceTests.Wiping(personKeeps: true),
        ["rank-one-against-two"] = RosterPrecedenceTests.RankOneAgainstTwo,
        ["yielding"] = RosterPrecedenceTests.Yielding,
        ["monotone"] = RosterForkTests.Monotone,
        ["old-head-removal"] = RosterForkTests.OldHeadRemoval,
        ["person-removes-cosigners"] = RosterForkTests.PersonRemovesCosigners,
        ["genesis-recovery"] = RosterForkTests.GenesisRecovery,
        ["hidden-heads"] = RosterForkTests.HiddenHeads,
        ["concurrent-renames"] = () => RosterForkTests.ConcurrentRenames(supersede: true),
        ["copy-renames"] = RosterForkTests.CopyRenames,
        ["two-fork-removals"] = RosterForkTests.TwoForkRemovals,
        ["replacement-key"] = RosterForkTests.ReplacementKey,
        ["held-add-taken"] = () => RosterForkTests.HeldAdd(keep: false),
        ["held-add-kept"] = () => RosterForkTests.HeldAdd(keep: true),
        ["genesis-self-removal"] = () => RosterForkTests.SelfRemovalAfterRecovery(genesis: true),
        ["senior-self-removal"] = () => RosterForkTests.SelfRemovalAfterRecovery(genesis: false),
        ["void-confirm"] = RosterForkTests.VoidConfirm,
        ["fabricated-outrank"] = RosterFabricationTests.FabricatedOutrank,
        ["fabricated-old-head"] = RosterFabricationTests.FabricatedOnOldHead,
        ["remove-p-after-sync"] = () => RosterFabricationTests.RemoveP(afterSync: true),
        ["tie-through-p"] = () => RosterFabricationTests.RemoveP(afterSync: false),
        ["partial-cascade"] = RosterFabricationTests.PartialCascade,
        ["quarantined-descendant"] = RosterFabricationTests.QuarantinedDescendant,
        ["two-adds-conflicted"] = RosterCopyTests.TwoAddsConflicted,
        ["thief-initiated"] = () => RosterCopyTests.ThiefInitiated(personRemovesJ: true),
        ["genesis-removes-approver"] = RosterCopyTests.GenesisRemovesApprover,
        ["stolen-genesis-pair"] = RosterCopyTests.StolenGenesisPair,
        ["partitioned"] = () => RosterCopyTests.Partitioned(confirmed: true),
        ["partitioned-unconfirmed"] = () => RosterCopyTests.Partitioned(confirmed: false),
        ["copies-across-ids"] = RosterCopyTests.CopiesAcrossIds,
        ["target-cannot-choose"] = RosterCopyTests.TargetCannotChoose,
        ["barred-grants"] = RosterWindowTests.BarredGrants,
        ["raw-chain"] = RosterWindowTests.RawChain,
        ["cascade-voids-winner"] = () => RosterWindowTests.CascadeVoidsWinner(removeCopier: true),
        ["copy-through-subtree"] = RosterKeepTests.CopyThroughSubtree,
        ["kept-copy"] = RosterKeepTests.KeptCopy,
        ["outside-copy"] = () => RosterKeepTests.OutsideCopy(personRemovesS: true),
        ["no-feedback"] = () => RosterStepFourTests.NoFeedback(removeCopier: true),
        ["removed-subordinate-barred"] = () => RosterStepFourTests.RemovedSubordinate(cSawCopy: true),
        ["removed-subordinate"] = () => RosterStepFourTests.RemovedSubordinate(cSawCopy: false),
        ["voided-merge"] = RosterAdoptionTests.VoidedMerge,
        ["copy-cannot-unkeep"] = RosterKeepTests.CopyCannotUnkeep,
        ["removed-devices-copy"] = RosterKeepTests.RemovedDevicesCopy,
    };

    // The labels in a random order in which every entry still follows its parents.
    static string[] Shuffled(World w, Random random)
    {
        var parents = w.Labels.ToDictionary(l => l, l => w.Entry(l).Body.Prev.Select(h => w.Labels.First(x => w[x].Hash.AsSpan().SequenceEqual(h))).ToArray());
        var left = w.Labels.ToList();
        var done = new HashSet<string>();
        var order = new List<string>();
        while (left.Count > 0)
        {
            var ready = left.Where(l => parents[l].All(done.Contains)).ToList();
            var pick = ready[random.Next(ready.Count)];
            order.Add(pick);
            done.Add(pick);
            left.Remove(pick);
        }
        return [.. order];
    }

    static string Summary(World w, RosterVerifier v) =>
        string.Join(",", w.Active(v)) + "|" + string.Join(",", v.State.Conflicted.Select(w.NameOf).Order(StringComparer.Ordinal))
        + "|" + string.Join(",", v.State.Active.Keys.Select(id => $"{w.NameOf(id)}={v.State.Name(id)}").Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Every_delivery_order_reaches_the_same_state_and_the_same_verdicts(string scenario)
    {
        var w = Builders[scenario]();
        var reference = w.Verifier();
        var expected = Summary(w, reference);
        var verdicts = w.Labels.ToDictionary(l => l, l => w.Feed(w.Verifier([.. w.Labels.TakeWhile(x => x != l)]), l)[0]);
        var random = new Random(scenario.GetHashCode(StringComparison.Ordinal) ^ 0x5eed);
        for (var round = 0; round < 12; round++)
        {
            var order = Shuffled(w, random);
            var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
            var offered = w.Feed(v, order);
            Assert.Equal(expected, Summary(w, v));
            for (var i = 0; i < order.Length; i++)
            {
                Assert.True(verdicts[order[i]].Reason == offered[i].Reason, $"{scenario}: {order[i]} in [{string.Join(" ", order)}]");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void An_entry_is_judged_in_the_state_of_its_ancestors_alone(string scenario)
    {
        var w = Builders[scenario]();
        var whole = w.Verifier();
        foreach (var label in w.Labels)
        {
            if (whole.Graph.Find(w[label].Hash) is not { } index) { continue; }
            var ancestors = w.Labels.Where(l => whole.Graph.Find(w[l].Hash) is { } i && whole.Graph.IsAncestor(i, index)).Append(label).ToArray();
            var alone = w.Verifier(ancestors);
            Assert.Equal(Summary(w, whole, whole.HistoryState(w[label].Hash)), Summary(w, alone, alone.HistoryState(w[label].Hash)));
            // With nothing quarantined, what a verifier holding only those applies is that state.
            Assert.Equal(Summary(w, alone, alone.HistoryState(w[label].Hash)), Summary(w, alone, alone.State));
        }
    }

    static string Summary(World w, RosterVerifier v, RosterState s) =>
        string.Join(",", s.Active.Keys.Select(w.NameOf).Order(StringComparer.Ordinal)) + "|"
        + string.Join(",", s.Active.Keys.Where(id => s.IsConfirmed(s.Active[id])).Select(w.NameOf).Order(StringComparer.Ordinal));
}
