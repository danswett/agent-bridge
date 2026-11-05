namespace AgentBridge.Roster;

// The state of a set of accepted entries (§3.5, "The state", in the order of computation it
// gives): which removals revoke and bar which adds, which grants are void and why, which adds of
// one id conflict and which wins, and then which devices are active, confirmed and named what.
// It reads only the entries in the set and what each entry's own history fixed (RosterNode), so
// it is the same in every delivery order for the same set.
public sealed class RosterState
{
    readonly RosterGraph _g;
    readonly Bits _in;
    readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);
    readonly HashSet<int> _confirmed = [];
    readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<int>> _renames = new(StringComparer.Ordinal);
    readonly HashSet<string> _renamedConcurrently = new(StringComparer.Ordinal);
    readonly HashSet<string> _conflicted = new(StringComparer.Ordinal);
    readonly Dictionary<int, int> _subordinateTo = [];
    readonly Bits _void1;
    readonly Bits _void4 = new();
    readonly Bits _revoked = new();
    readonly Bits _barred = new();

    public static readonly RosterState Empty = new(new RosterGraph(), new Bits());

    // Active devices by id, with the add that made each active.
    public IReadOnlyDictionary<string, int> Active => _active;
    public int ActiveCount => _active.Count;
    // Ids whose adds conflict, so that none is active (§3.3).
    public IReadOnlySet<string> Conflicted => _conflicted;
    // Subordinate adds, and the add that displaced each.
    public IReadOnlyDictionary<int, int> SubordinateTo => _subordinateTo;
    public IReadOnlySet<string> RenamedConcurrently => _renamedConcurrently;

    public int? ActiveAdd(string id) => _active.TryGetValue(id, out var a) ? a : null;
    public bool IsActiveAdd(int add) => add >= 0 && add < _g.Count && _g[add].DeviceId is { } id && _active.TryGetValue(id, out var a) && a == add;
    public bool IsConfirmed(int add) => _confirmed.Contains(add);
    public bool IsVoid(int index) => _void1[index] || _void4[index];
    public bool IsRevoked(int add) => _revoked[add];
    public bool IsBarred(int add) => _barred[add];
    public bool Contains(int index) => _in[index];
    public DeviceDescriptor? Device(string id) => ActiveAdd(id) is { } a ? _g[a].Body.Device : null;
    public string? Name(string id) => _names.TryGetValue(id, out var n) ? n : Device(id)?.Name;
    public IEnumerable<string> PersonDevices => _active.Where(p => _g[p.Value].Body.Device!.IsPerson).Select(p => p.Key);

    internal RosterState(RosterGraph g, Bits members)
    {
        _g = g;
        _in = members;
        _void1 = new Bits(g.Count);
        if (g.Count == 0) { return; }
        var entries = members.Members().ToList();
        var removals = entries.Where(i => g[i].Op == EntryOp.Remove).ToList();
        var adds = entries.Where(i => g[i].IsAdd).ToList();

        // Step 1: what each applying removal takes and bars.
        var takenBy = new Dictionary<int, List<int>>();
        var barredBy = new Dictionary<int, List<int>>();
        foreach (var r in removals)
        {
            var node = g[r];
            foreach (var a in node.Takes!.Members())
            {
                _revoked.Set(a);
                Append(takenBy, a, r);
            }
            var targetId = g[node.Target].DeviceId;
            foreach (var b in adds)
            {
                if (b == node.Target || g[b].DeviceId != targetId || g.IsAncestor(b, r)) { continue; }
                var before = g[b].ParentState;
                if (before is null || !before.IsActiveAdd(node.Signer)) { continue; }
                if (node.Cosigner >= 0 && !before.IsActiveAdd(node.Cosigner)) { continue; }
                _barred.Set(b);
                Append(barredBy, b, r);
            }
        }

        // The raw chains: why each grant would be void if no removal shielded anything.
        var raw = Causes(entries, takenBy, barredBy, shields: null);

        // Precedence (§3.5): which removals shield their descendants against which.
        bool RevokedBy(int m, int add) => g[m].Takes![add] || (raw.TryGetValue(add, out var c) && c.Contains(m));
        IEnumerable<int> SignersOf(int r) => g[r].Cosigner >= 0 ? [g[r].Signer, g[r].Cosigner] : [g[r].Signer];
        bool AnyIn(int r, int target) => SignersOf(r).Any(s => g.InSubtree(s, target));
        var shieldedBy = new Dictionary<int, List<int>>();
        foreach (var r in removals)
        {
            foreach (var m in removals)
            {
                if (m == r || g.IsAncestor(r, m) || !SignersOf(r).Any(s => RevokedBy(m, s))) { continue; }
                var (rm, rr) = (g[m].Rank, g[r].Rank);
                var shields = rm > rr
                    || (rm == rr && AnyIn(r, g[m].Target) && !AnyIn(m, g[r].Target))
                    || (rm == rr && !AnyIn(r, g[m].Target) && !AnyIn(m, g[r].Target) && !SignersOf(m).Any(s => RevokedBy(r, s)));
                if (shields) { Append(shieldedBy, r, m); }
            }
        }
        var causes = Causes(entries, takenBy, barredBy, shieldedBy);
        foreach (var (i, c) in causes)
        {
            if (c.Count > 0) { _void1.Set(i); }
        }

        // Step 2: each id's adds that are neither void nor revoked, for every id at once.
        var sets = adds.Where(a => !_void1[a] && !_revoked[a]).GroupBy(a => g[a].DeviceId!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);

        // Step 3: winners. An add kept by an applying removal of t does not displace an add of
        // its id made outside t's subtree.
        var keptUnder = new Dictionary<int, List<int>>();
        foreach (var r in removals)
        {
            foreach (var k in g[r].Keep) { Append(keptUnder, k, g[r].Target); }
        }
        bool Displaces(int a2, int a1) =>
            g.IsAncestor(g[a2].Signer, a1)
            && !(keptUnder.TryGetValue(a2, out var ts) && ts.Any(t => !g.InSubtree(a1, t)));
        var chosen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, set) in sets)
        {
            if (set.Count == 1) { chosen[id] = set[0]; continue; }
            var winners = set.Where(w => set.All(o => o == w || (Displaces(w, o) && !Displaces(o, w)))).ToList();
            if (winners.Count == 1)
            {
                chosen[id] = winners[0];
                foreach (var o in set.Where(o => o != winners[0])) { _subordinateTo[o] = winners[0]; }
            }
            else { _conflicted.Add(id); }
        }

        // Step 4: subordinate adds are void, and what they granted, transitively.
        foreach (var i in entries)
        {
            var n = g[i];
            if (_subordinateTo.ContainsKey(i)) { _void4.Set(i); continue; }
            if (n.IsGrant && n.Signer != i && (_void4[n.Signer] || _subordinateTo.ContainsKey(n.Signer))) { _void4.Set(i); }
        }

        // Step 5: bars, then which ids are active.
        foreach (var (id, add) in chosen)
        {
            if (!IsVoid(add) && !_barred[add]) { _active[id] = add; }
        }

        foreach (var i in entries)
        {
            var n = g[i];
            if (n.Op == EntryOp.Genesis) { _confirmed.Add(i); }
            if (n.Op == EntryOp.Confirm && !IsVoid(i)) { _confirmed.Add(n.Target); }
        }

        // Names: the latest non-void renames of each active add; the smallest hash among several.
        var renames = entries.Where(i => g[i].Op == EntryOp.Rename && !IsVoid(i)).ToList();
        foreach (var group in renames.GroupBy(i => g[i].Target))
        {
            var target = group.Key;
            if (g[target].DeviceId is not { } id || !_active.TryGetValue(id, out var a) || a != target) { continue; }
            _renames[id] = [.. group];
            if (Latest(group.ToList(), _ => false) is { } named)
            {
                _names[id] = g[named.Pick].Body.Name!;
                if (named.Concurrent) { _renamedConcurrently.Add(id); }
            }
        }
    }

    (int Pick, bool Concurrent)? Latest(List<int> renames, Func<int, bool> pending)
    {
        var applied = renames.Where(x => !pending(x)).ToList();
        var latest = applied.Where(x => !applied.Any(y => y != x && _g.IsAncestor(x, y))).ToList();
        if (latest.Count == 0) { return null; }
        return (latest.OrderBy(x => _g[x].Hash, Ids.HeadOrder).First(), latest.Count > 1);
    }

    // An active device's name with some renames left pending: the latest of the others, or the
    // name it was enrolled with.
    internal string? Name(string id, Func<int, bool> pending)
    {
        if (!_renames.TryGetValue(id, out var renames)) { return Device(id)?.Name; }
        return Latest(renames, pending) is { } named ? _g[named.Pick].Body.Name : Device(id)?.Name;
    }

    static void Append(Dictionary<int, List<int>> map, int key, int value)
    {
        if (!map.TryGetValue(key, out var list)) { map[key] = list = []; }
        list.Add(value);
    }

    // For each grant, the applying removals it is void because of (§3.5, "Voiding"): directly,
    // where it was signed through an add a removal revokes or bars and is not that removal's
    // ancestor, or through the void add that signed it. A self-removal of D does not void D's
    // grants that are ancestors of another applying removal of D that D did not sign. With
    // shields, a cause R is dropped for every entry descending from a removal that shields
    // against R; without, these are the raw chains.
    Dictionary<int, HashSet<int>> Causes(List<int> entries, Dictionary<int, List<int>> takenBy, Dictionary<int, List<int>> barredBy, Dictionary<int, List<int>>? shields)
    {
        var g = _g;
        var causes = new Dictionary<int, HashSet<int>>();
        foreach (var i in entries)
        {
            var n = g[i];
            if (!n.IsGrant || n.Signer < 0 || n.Signer == i) { continue; }
            var signer = n.Signer;
            var c = new HashSet<int>();
            foreach (var r in Of(takenBy, signer).Concat(Of(barredBy, signer)))
            {
                if (g.IsAncestor(i, r)) { continue; }
                if (g[r].IsSelfRemoval && g[r].Target == signer && OtherRemovalKeeps(r, i)) { continue; }
                c.Add(r);
            }
            if (causes.TryGetValue(signer, out var inherited)) { c.UnionWith(inherited); }
            if (shields is not null && c.Count > 0)
            {
                c.RemoveWhere(r => shields.TryGetValue(r, out var ms) && ms.Any(m => g.IsAncestor(m, i)));
            }
            if (c.Count > 0) { causes[i] = c; }
        }
        return causes;

        bool OtherRemovalKeeps(int self, int grant)
        {
            var target = g[self].Target;
            foreach (var r in entries)
            {
                if (r == self || g[r].Op != EntryOp.Remove || g[r].Target != target) { continue; }
                if (g[r].Signer == target || g[r].Cosigner == target) { continue; }
                if (g.IsAncestor(grant, r)) { return true; }
            }
            return false;
        }
    }

    static IEnumerable<int> Of(Dictionary<int, List<int>> map, int key) => map.TryGetValue(key, out var l) ? l : [];
}
