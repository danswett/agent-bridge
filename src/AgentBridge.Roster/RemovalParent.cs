namespace AgentBridge.Roster;

// Where a remover's client signs a `remove` (§3.3): the parent chosen, the target's grants it
// could not void by position (the devices those added are revoked unless kept), and which of the
// attested holders lack it (they receive its missing ancestors with the removal).
public sealed record RemovalParent(byte[] Parent, IReadOnlyList<byte[]> Unvoidable, IReadOnlyList<int> Lacking);

public static class RemovalParents
{
    // The target chooses how much evidence a removal signed on an arbitrary head needs, and a
    // stolen device's renames on a quarantined branch must not become ancestors of its own
    // removal, so the parent is chosen, never just the head: an unquarantined entry holding what
    // the removal needs and as little of the target's as possible, under every holder's heads.
    // `holders` are each attested host's last heads and the relay's head. Null when nothing the
    // remover holds unquarantined has what the removal needs.
    public static RemovalParent? Choose(RosterVerifier v, byte[] targetAdd, string by, string? cosigner,
        IReadOnlyCollection<byte[]> keep, IReadOnlyList<IReadOnlyCollection<byte[]>> holders)
    {
        var g = v.Graph;
        var state = v.State;
        if (g.Find(targetAdd) is not { } target || state.ActiveAdd(by) is not { } signer) { return null; }
        int? co = null;
        if (cosigner is not null)
        {
            if (state.ActiveAdd(cosigner) is not { } c) { return null; }
            co = c;
        }
        var kept = new List<int>();
        foreach (var k in keep)
        {
            if (g.Find(k) is not { } ki) { return null; }
            kept.Add(ki);
        }

        var required = new List<int> { target, signer };
        if (co is { } cs) { required.Add(cs); }
        required.AddRange(kept);
        var confirmsNeeded = co is { } c2 ? new[] { signer, c2 } : [];

        // A kept add and what it needs may stay under the parent; nothing else of the target's.
        var allowed = new Bits(g.Count);
        foreach (var k in kept) { allowed.Or(g.Closure(k)); }

        var held = Enumerable.Range(0, g.Count).Where(i => v.Holds(g[i].Hash) && !v.IsQuarantined(i)).ToList();
        var under = holders.Select(h => Under(v, h)).ToList();
        var common = new Bits(g.Count);
        foreach (var i in held)
        {
            if (under.All(u => u[i])) { common.Set(i); }
        }

        bool Holds(int c)
        {
            var closure = g.Closure(c);
            if (required.Any(r => !closure[r])) { return false; }
            foreach (var x in confirmsNeeded)
            {
                if (g[x].Op != EntryOp.Genesis && !closure.Members().Any(i => g[i].Op == EntryOp.Confirm && g[i].Target == x)) { return false; }
            }
            return true;
        }

        List<int> Unvoidable(int c)
        {
            var closure = g.Closure(c);
            var adds = closure.Members().Where(i => g[i].IsAdd).ToList();
            return [.. closure.Members().Where(i =>
            {
                var n = g[i];
                if (!n.IsGrant || n.Signer < 0 || (n.Signer != target && !g.InSubtree(n.Signer, target))) { return false; }
                // A copy of a key is never kept: one of two adds of the same id under the parent.
                var copy = n.IsAdd && adds.Any(a => a != i && g[a].DeviceId == n.DeviceId);
                return copy || !allowed[i];
            })];
        }

        var size = held.ToDictionary(i => i, i => g[i].Ancestors.Count());
        var inCommon = held.Where(i => common[i] && Holds(i))
            .OrderBy(i => size[i]).ThenBy(i => g[i].Hash, Ids.HeadOrder).ToList();

        int pick;
        if (inCommon.Count > 0)
        {
            pick = inCommon.FirstOrDefault(i => Unvoidable(i).Count == 0, inCommon[0]);
        }
        else
        {
            // A signer confirmed minutes ago, say: the entry with the fewest ancestors outside
            // what every holder has.
            var outside = held.Where(Holds)
                .Select(i => (i, missing: g.Closure(i).Members().Count(a => !common[a])))
                .OrderBy(x => x.missing).ThenBy(x => size[x.i]).ThenBy(x => g[x.i].Hash, Ids.HeadOrder).ToList();
            if (outside.Count == 0) { return null; }
            pick = outside[0].i;
        }
        return new RemovalParent(
            g[pick].Hash,
            [.. Unvoidable(pick).Select(i => g[i].Hash)],
            [.. Enumerable.Range(0, under.Count).Where(h => !under[h][pick])]);
    }

    // What a holder of these heads holds: the heads and their ancestors that this verifier has.
    static Bits Under(RosterVerifier v, IReadOnlyCollection<byte[]> heads)
    {
        var bits = new Bits(v.Graph.Count);
        foreach (var h in heads)
        {
            if (v.Graph.Find(h) is { } i) { bits.Or(v.Graph.Closure(i)); }
        }
        return bits;
    }
}
