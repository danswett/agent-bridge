namespace AgentBridge.Roster;

// A fixed-size set of entry indices. Entries are indexed in acceptance order, which is
// topological (an entry is accepted only after its parents), so an ancestor always has a lower
// index than its descendants.
internal sealed class Bits
{
    ulong[] _words;

    public Bits(int capacity = 64) => _words = new ulong[Math.Max(1, (capacity + 63) >> 6)];

    Bits(ulong[] words) => _words = words;

    public bool this[int i] => i >= 0 && (i >> 6) < _words.Length && (_words[i >> 6] & (1UL << (i & 63))) != 0;

    public void Set(int i)
    {
        if ((i >> 6) >= _words.Length) { Array.Resize(ref _words, Math.Max(_words.Length * 2, (i >> 6) + 1)); }
        _words[i >> 6] |= 1UL << (i & 63);
    }

    public void Clear(int i)
    {
        if ((i >> 6) < _words.Length) { _words[i >> 6] &= ~(1UL << (i & 63)); }
    }

    public void Or(Bits other)
    {
        if (other._words.Length > _words.Length) { Array.Resize(ref _words, other._words.Length); }
        for (var w = 0; w < other._words.Length; w++) { _words[w] |= other._words[w]; }
    }

    public Bits Clone() => new((ulong[])_words.Clone());

    public int Count()
    {
        var n = 0;
        foreach (var w in _words) { n += System.Numerics.BitOperations.PopCount(w); }
        return n;
    }

    public IEnumerable<int> Members()
    {
        for (var w = 0; w < _words.Length; w++)
        {
            var word = _words[w];
            while (word != 0)
            {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                yield return (w << 6) + bit;
                word &= word - 1;
            }
        }
    }
}

// One accepted entry and what its own history fixes about it (§3.3): who signed it (the add that
// made `by` active in its parents' state, never the bare id), what it targets, and, for a
// removal, its rank and what it takes. All of it is read from the entry's ancestors alone, so it
// never changes once the entry is accepted, and every verifier computes the same.
public sealed class RosterNode
{
    internal RosterNode(int index, Entry entry, int[] parents, Bits ancestors)
    {
        Index = index;
        Entry = entry;
        Parents = parents;
        Ancestors = ancestors;
    }

    public int Index { get; }
    public Entry Entry { get; }
    public EntryBody Body => Entry.Body;
    public EntryOp Op => Entry.Body.Op;
    public byte[] Hash => Entry.Hash;
    internal int[] Parents { get; }
    // Every ancestor's index, not the entry's own.
    internal Bits Ancestors { get; }

    // The add that made `by` active in the parents' state; a genesis is its own.
    public int Signer { get; internal set; } = -1;
    // The cosigner's add, on a cosigned removal.
    public int Cosigner { get; internal set; } = -1;
    // The add a remove, rename or confirm names.
    public int Target { get; internal set; } = -1;
    // A removal's kept adds, and what it takes: its target and every add of the target's approval
    // subtree that is its ancestor and not kept (only the target, for a self-removal).
    internal int[] Keep { get; set; } = [];
    internal Bits? Takes { get; set; }
    public int Rank { get; internal set; }
    public bool IsSelfRemoval => Op == EntryOp.Remove && Target == Signer;
    public bool IsAdd => Op is EntryOp.Add or EntryOp.Genesis;
    public bool IsGrant => Op is EntryOp.Add or EntryOp.Rename or EntryOp.Confirm;
    public string? DeviceId => Body.Device?.Id;

    // The state of the entry's parents (history, quarantine ignored), which bars and the
    // displacement rule read.
    internal RosterState? ParentState { get; set; }

    // Kept as evidence for a removal (§3.3): stored and gossiped, never applied.
    public bool Evidence { get; internal set; }
    // Quarantined when accepted (outside the window, or a fifth head; §3.5 rule 3).
    internal bool QuarantinedOnArrival { get; set; }
}

// The accepted entries of one generation as a DAG.
public sealed class RosterGraph
{
    readonly List<RosterNode> _nodes = [];
    readonly Dictionary<string, int> _byHash = new(StringComparer.Ordinal);

    public IReadOnlyList<RosterNode> Nodes => _nodes;
    public int Count => _nodes.Count;
    public RosterNode this[int index] => _nodes[index];

    public int? Find(byte[] hash) => _byHash.TryGetValue(Codec.Base64Url(hash), out var i) ? i : null;

    internal RosterNode Add(Entry entry, int[] parents)
    {
        var ancestors = new Bits(_nodes.Count + 1);
        foreach (var p in parents)
        {
            ancestors.Set(p);
            ancestors.Or(_nodes[p].Ancestors);
        }
        var node = new RosterNode(_nodes.Count, entry, parents, ancestors);
        _nodes.Add(node);
        _byHash[Codec.Base64Url(entry.Hash)] = node.Index;
        return node;
    }

    // Whether a is a strict ancestor of b.
    public bool IsAncestor(int a, int b) => a < b && _nodes[b].Ancestors[a];

    // An ancestor-closed set: the entry and its ancestors.
    internal Bits Closure(int index)
    {
        var c = _nodes[index].Ancestors.Clone();
        c.Set(index);
        return c;
    }

    // The add's approval lineage: the add, the add that signed it, and so on to the genesis.
    public IEnumerable<int> Lineage(int add)
    {
        for (var x = add; ; x = _nodes[x].Signer)
        {
            yield return x;
            if (_nodes[x].Signer == x || _nodes[x].Signer < 0) { yield break; }
        }
    }

    // Whether `add` is in t's approval subtree: t is in its lineage, other than as the add itself.
    public bool InSubtree(int add, int t)
    {
        if (add == t) { return false; }
        foreach (var x in Lineage(add))
        {
            if (x == t) { return true; }
        }
        return false;
    }
}
