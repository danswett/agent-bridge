namespace AgentBridge.Roster;

public enum Verdict
{
    // Accepted and applied (or held for probation, which only delays an effect).
    Accepted,
    // Accepted and kept, not applied: outside the window, a fifth head, or on a quarantined branch.
    Quarantined,
    // Already held.
    Duplicate,
    // Refused, with its code. One refused only for a cap or budget is kept aside, in case a later
    // removal brings it as evidence (§3.3); it is never applied.
    Refused,
}

public sealed record Offered(byte[] Hash, Verdict Verdict, string? Reason = null);

// Why an entry is refused by the acceptance rule. Codes, like the parser's, so the shared state
// vectors and every implementation agree on the reason.
public static class Invalid
{
    public const string Parent = "parent";
    public const string Account = "account";
    public const string Generation = "generation";
    public const string Genesis = "genesis";
    public const string Seq = "seq";
    public const string Signer = "signer";
    public const string Signature = "signature";
    public const string Presence = "presence";
    public const string Readded = "readded";
    public const string Target = "target";
    public const string Seniority = "seniority";
    public const string Cosign = "cosign";
    public const string Keep = "keep";
    public const string Confirm = "confirm";
    public const string Merge = "merge";
    public const string ActiveCap = "active-cap";
    public const string Cap = "cap";
    public const string Budget = "budget";
    public const string Evidence = "evidence";
}

// One verifier's copy of a generation (§3.5): the set S of accepted entries, which entries are
// quarantined, what has been adopted, and the state it applies. It decides each offered entry by
// the acceptance rule, in the order offered; nothing accepted is ever dropped.
//
// `clock` is the verifier's persisted monotonic running time (§3.4): it moves only while the
// verifier runs, never with the wall clock, and survives restarts. Probation is measured with it.
public sealed class RosterVerifier(string account, string generation, Func<TimeSpan> clock)
{
    public const int Window = 8;
    public const int MaxHeads = 4;
    public const int MaxAdds = 512;
    public const int MaxEntries = 4096;
    public const int MaxActive = 64;
    public const int SignerBudget = 64;
    public const int RenamesPerAdd = 8;
    public const int EvidencePerSigner = 256;
    public const int EvidenceTotal = 1024;
    public static readonly TimeSpan Probation = TimeSpan.FromHours(72);

    readonly RosterGraph _g = new();
    readonly Dictionary<int, RosterState> _closureStates = [];
    readonly List<int> _children = [];
    readonly Bits _candidate = new();
    readonly Bits _adopted = new();
    readonly HashSet<int> _adoptingMerges = [];
    readonly Dictionary<int, TimeSpan> _firstAccepted = [];
    readonly Dictionary<int, string> _refusedCandidates = [];
    readonly Dictionary<(int Signer, EntryOp Op), int> _budgets = [];
    readonly Dictionary<int, int> _renames = [];
    readonly Dictionary<int, int> _evidenceBySigner = [];
    int _evidence;
    int _adds;
    int _nonRemovals;
    Bits? _quarantined;
    RosterState? _applied;

    public string Account { get; } = account;
    public string Generation { get; } = generation;
    public RosterGraph Graph => _g;

    // The entries with no child, in head order: what rosterHead reports.
    public IReadOnlyList<byte[]> Heads => [.. HeadIndices().Select(i => _g[i].Hash).Order(Ids.HeadOrder)];

    IEnumerable<int> HeadIndices() => Enumerable.Range(0, _g.Count).Where(i => _children[i] == 0 && !_candidate[i]);

    public bool IsQuarantined(int index) => Quarantined()[index];

    public bool Holds(byte[] hash) => _g.Find(hash) is { } i && !_candidate[i];

    public IReadOnlyList<Offered> Offer(params Entry[] entries) => Offer((IEnumerable<Entry>)entries);

    // Entries in an order where each comes after its parents, as one offer: a removal in it may
    // bring ancestors over a cap or budget as evidence (§3.3).
    public IReadOnlyList<Offered> Offer(IEnumerable<Entry> entries)
    {
        var results = new List<Offered>();
        foreach (var e in entries) { results.Add(OfferOne(e)); }
        return results;
    }

    Offered OfferOne(Entry e)
    {
        if (_g.Find(e.Hash) is { } known)
        {
            if (!_candidate[known]) { return new(e.Hash, Verdict.Duplicate); }
            return new(e.Hash, Verdict.Refused, _refusedCandidates.GetValueOrDefault(known, Invalid.Budget));
        }
        var body = e.Body;
        if (body.Account != Account) { return new(e.Hash, Verdict.Refused, Invalid.Account); }
        if (body.Generation != Generation) { return new(e.Hash, Verdict.Refused, Invalid.Generation); }

        var parents = new int[body.Prev.Count];
        for (var i = 0; i < parents.Length; i++)
        {
            if (_g.Find(body.Prev[i]) is not { } p) { return new(e.Hash, Verdict.Refused, Invalid.Parent); }
            parents[i] = p;
        }
        if (body.Op == EntryOp.Genesis && _g.Count > 0) { return new(e.Hash, Verdict.Refused, Invalid.Genesis); }
        if (body.Op != EntryOp.Genesis && _g.Count == 0) { return new(e.Hash, Verdict.Refused, Invalid.Parent); }

        var ancestors = new Bits(_g.Count + 1);
        foreach (var p in parents)
        {
            ancestors.Set(p);
            ancestors.Or(_g[p].Ancestors);
        }
        var before = parents.Length == 0 ? RosterState.Empty : StateOf(parents);
        var reason = Validate(e, parents, ancestors, before, out var facts);
        var candidateParent = parents.Any(p => _candidate[p]);
        var overCap = reason is Invalid.Cap or Invalid.Budget;
        if (reason is not null && !overCap) { return new(e.Hash, Verdict.Refused, reason); }

        if (overCap || candidateParent)
        {
            // Kept aside, in case a removal in this or a later offer needs it as evidence.
            if (body.Op != EntryOp.Remove || overCap)
            {
                if (_refusedCandidates.Count >= 2 * EvidenceTotal) { return new(e.Hash, Verdict.Refused, reason ?? Invalid.Evidence); }
                var c = Commit(e, parents, before, facts);
                _candidate.Set(c.Index);
                _refusedCandidates[c.Index] = reason ?? Invalid.Evidence;
                return new(e.Hash, Verdict.Refused, reason ?? Invalid.Evidence);
            }
            if (!EvidenceFor(facts, ancestors, out var evidence)) { return new(e.Hash, Verdict.Refused, Invalid.Evidence); }
            foreach (var x in evidence)
            {
                _candidate.Clear(x);
                _refusedCandidates.Remove(x);
                _g[x].Evidence = true;
                _evidenceBySigner[_g[x].Signer] = _evidenceBySigner.GetValueOrDefault(_g[x].Signer) + 1;
                _evidence++;
                foreach (var p in _g[x].Parents) { _children[p]++; }
            }
        }

        // §3.5 rule 3: the window and the heads, for everything but removals and merges.
        var arrival = false;
        if (body.Op is not (EntryOp.Remove or EntryOp.Merge or EntryOp.Genesis))
        {
            var q = Quarantined();
            var unseen = 0;
            for (var i = 0; i < _g.Count; i++)
            {
                if (!q[i] && !_candidate[i] && !_g[i].Evidence && !ancestors[i]) { unseen++; }
            }
            var heads = HeadIndices().Count(h => !parents.Contains(h)) + 1;
            arrival = parents.Length != 1 || unseen > Window || heads > MaxHeads;
        }

        var node = Commit(e, parents, before, facts);
        node.QuarantinedOnArrival = arrival;
        if (node.IsAdd) { _firstAccepted.TryAdd(node.Index, clock()); }
        Count(node);
        return new(e.Hash, Quarantined()[node.Index] ? Verdict.Quarantined : Verdict.Accepted);
    }

    RosterNode Commit(Entry e, int[] parents, RosterState before, Facts facts)
    {
        var node = _g.Add(e, parents);
        node.Signer = e.Body.Op == EntryOp.Genesis ? node.Index : facts.Signer;
        node.Cosigner = facts.Cosigner;
        node.Target = facts.Target;
        node.Keep = facts.Keep;
        node.Rank = facts.Rank;
        node.ParentState = before;
        if (e.Body.Op == EntryOp.Remove)
        {
            var takes = new Bits(_g.Count);
            takes.Set(facts.Target);
            if (!node.IsSelfRemoval)
            {
                foreach (var a in node.Ancestors.Members())
                {
                    if (_g[a].IsAdd && _g.InSubtree(a, facts.Target) && !facts.Keep.Contains(a)) { takes.Set(a); }
                }
            }
            node.Takes = takes;
        }
        _children.Add(0);
        if (!_candidate[node.Index] && !parents.Any(p => _candidate[p]))
        {
            foreach (var p in parents) { _children[p]++; }
        }
        _quarantined = null;
        _applied = null;
        return node;
    }

    void Count(RosterNode n)
    {
        var op = n.Op;
        if (op != EntryOp.Remove) { _nonRemovals++; }
        if (n.IsAdd) { _adds++; }
        if (op is EntryOp.Add or EntryOp.Confirm or EntryOp.Merge or EntryOp.Remove)
        {
            _budgets[(n.Signer, op)] = _budgets.GetValueOrDefault((n.Signer, op)) + 1;
        }
        if (op == EntryOp.Rename) { _renames[n.Target] = _renames.GetValueOrDefault(n.Target) + 1; }
    }

    // A removal's over-cap ancestors are admitted as evidence only when it is not a
    // self-removal, each is signed by its target or a device of the target's subtree it does not
    // keep, and the verifier's limits hold (§3.3).
    bool EvidenceFor(Facts r, Bits ancestors, out List<int> evidence)
    {
        evidence = [.. ancestors.Members().Where(i => _candidate[i])];
        if (r.Target == r.Signer) { return false; }
        var bySigner = new Dictionary<int, int>();
        foreach (var x in evidence)
        {
            var s = _g[x].Signer;
            if (s != r.Target && !_g.InSubtree(s, r.Target)) { return false; }
            if (r.Keep.Contains(s)) { return false; }
            bySigner[s] = bySigner.GetValueOrDefault(s) + 1;
        }
        foreach (var (s, n) in bySigner)
        {
            if (n > EvidencePerSigner || _evidenceBySigner.GetValueOrDefault(s) + n > EvidencePerSigner) { return false; }
        }
        return _evidence + evidence.Count <= EvidenceTotal;
    }

    // The history state of a set of parents: computed from all their ancestors, quarantine ignored.
    internal RosterState StateOf(int[] parents)
    {
        if (parents.Length == 1) { return ClosureState(parents[0]); }
        var members = new Bits(_g.Count);
        foreach (var p in parents) { members.Or(_g.Closure(p)); }
        return new RosterState(_g, members);
    }

    RosterState ClosureState(int index)
    {
        if (!_closureStates.TryGetValue(index, out var s)) { _closureStates[index] = s = new RosterState(_g, _g.Closure(index)); }
        return s;
    }

    // The history state an entry was judged in.
    public RosterState HistoryState(byte[] hash) => _g.Find(hash) is { } i ? ClosureState(i) : RosterState.Empty;

    // ---- validity (§3.3) ----

    record struct Facts(int Signer, int Cosigner, int Target, int[] Keep, int Rank);

    string? Validate(Entry e, int[] parents, Bits ancestors, RosterState p, out Facts facts)
    {
        facts = new Facts(-1, -1, -1, [], 0);
        var body = e.Body;
        var seq = parents.Length == 0 ? 0 : parents.Max(x => _g[x].Body.Seq) + 1;
        if (body.Seq != seq) { return Invalid.Seq; }

        if (body.Op == EntryOp.Genesis)
        {
            var d = body.Device!;
            if (!Verify(d.Sign!, Entry.SignedBy(e.BodyBytes), e.Sig)) { return Invalid.Signature; }
            return PresenceOf(e, d, Roster.Presence.RoleBy, e.UvBy);
        }

        if (p.ActiveAdd(body.By) is not { } signer || !_g[signer].Body.Device!.IsPerson) { return Invalid.Signer; }
        var signerDevice = _g[signer].Body.Device!;
        if (!Verify(signerDevice.Sign!, Entry.SignedBy(e.BodyBytes), e.Sig)) { return Invalid.Signature; }
        if (PresenceOf(e, signerDevice, Roster.Presence.RoleBy, e.UvBy) is { } noPresence) { return noPresence; }
        var cosigner = -1;
        if (body.Cosigner is { } co)
        {
            if (p.ActiveAdd(co) is not { } c || !_g[c].Body.Device!.IsPerson) { return Invalid.Signer; }
            cosigner = c;
            var cd = _g[c].Body.Device!;
            if (!Verify(cd.Sign!, Entry.SignedByCosigner(e.BodyBytes), e.Cosig!)) { return Invalid.Signature; }
            if (PresenceOf(e, cd, Roster.Presence.RoleCosigner, e.UvCosigner) is { } noCoPresence) { return noCoPresence; }
        }
        facts = facts with { Signer = signer, Cosigner = cosigner };

        switch (body.Op)
        {
            case EntryOp.Add:
                {
                    foreach (var a in ancestors.Members())
                    {
                        if (_g[a].IsAdd && _g[a].DeviceId == body.Device!.Id) { return Invalid.Readded; }
                    }
                    if (p.ActiveCount >= MaxActive) { return Invalid.ActiveCap; }
                    if (_adds >= MaxAdds || _nonRemovals >= MaxEntries) { return Invalid.Cap; }
                    return OverBudget(signer, EntryOp.Add);
                }
            case EntryOp.Remove:
                {
                    if (TargetOf(body, ancestors, p) is not { } t) { return Invalid.Target; }
                    facts = facts with { Target = t, Rank = cosigner >= 0 ? 2 : 1 };
                    var self = body.Target!.Id == body.By;
                    if (!self && cosigner < 0 && !_g.IsAncestor(signer, t)) { return Invalid.Seniority; }
                    if (cosigner >= 0 && !CosignTest(signer, cosigner, t, p)) { return Invalid.Cosign; }
                    var keep = new List<int>();
                    foreach (var k in body.Keep)
                    {
                        if (_g.Find(k) is not { } ki || !ancestors[ki] || !_g[ki].IsAdd || !_g.InSubtree(ki, t)) { return Invalid.Keep; }
                        keep.Add(ki);
                    }
                    foreach (var k in keep)
                    {
                        var s = _g[k].Signer;
                        if (s != t && !keep.Contains(s)) { return Invalid.Keep; }
                        // Not a copy: no other add of its id in the removal's parents' history.
                        foreach (var a in ancestors.Members())
                        {
                            if (a != k && _g[a].IsAdd && _g[a].DeviceId == _g[k].DeviceId) { return Invalid.Keep; }
                        }
                    }
                    facts = facts with { Keep = [.. keep] };
                    if (!self)
                    {
                        foreach (var s in cosigner >= 0 ? new[] { signer, cosigner } : [signer])
                        {
                            if (s == t || (_g.InSubtree(s, t) && !keep.Contains(s))) { return Invalid.Signer; }
                        }
                    }
                    return OverBudget(signer, EntryOp.Remove);
                }
            case EntryOp.Rename:
                {
                    if (TargetOf(body, ancestors, p) is not { } t) { return Invalid.Target; }
                    facts = facts with { Target = t };
                    if (t != signer && !_g.IsAncestor(signer, t)) { return Invalid.Seniority; }
                    if (_renames.GetValueOrDefault(t) >= RenamesPerAdd) { return Invalid.Budget; }
                    return _nonRemovals >= MaxEntries ? Invalid.Cap : null;
                }
            case EntryOp.Confirm:
                {
                    if (TargetOf(body, ancestors, p) is not { } t) { return Invalid.Target; }
                    facts = facts with { Target = t };
                    if (!p.IsConfirmed(signer) || _g[t].Signer != signer || p.IsConfirmed(t)) { return Invalid.Confirm; }
                    if (_nonRemovals >= MaxEntries) { return Invalid.Cap; }
                    return OverBudget(signer, EntryOp.Confirm);
                }
            case EntryOp.Merge:
                {
                    // Two to four distinct heads: none the ancestor of another.
                    foreach (var a in parents)
                    {
                        if (parents.Any(b => _g.IsAncestor(a, b))) { return Invalid.Merge; }
                    }
                    if (_nonRemovals >= MaxEntries) { return Invalid.Cap; }
                    return OverBudget(signer, EntryOp.Merge);
                }
        }
        return Invalid.Target;
    }

    string? OverBudget(int signer, EntryOp op) => _budgets.GetValueOrDefault((signer, op)) >= SignerBudget ? Invalid.Budget : null;

    // The add a remove, rename or confirm names: an ancestor that adds that id, active in the
    // parents' state.
    int? TargetOf(EntryBody body, Bits ancestors, RosterState p)
    {
        if (_g.Find(body.Target!.Add) is not { } t || !ancestors[t] || !_g[t].IsAdd || _g[t].DeviceId != body.Target.Id) { return null; }
        return p.IsActiveAdd(t) ? t : null;
    }

    // Both signers confirmed in the parents' state, and their lineages, cut at the target (its
    // add and every add senior to it left out), disjoint (§3.4).
    bool CosignTest(int a, int b, int target, RosterState p)
    {
        if (!p.IsConfirmed(a) || !p.IsConfirmed(b)) { return false; }
        bool Kept(int x) => x != target && !_g.IsAncestor(x, target);
        var cutA = _g.Lineage(a).Where(Kept).ToHashSet();
        return !_g.Lineage(b).Where(Kept).Any(cutA.Contains);
    }

    static bool Verify(byte[] key, byte[] message, byte[] der) => P256.Verify(key, message, der);

    // Every entry needs each signer's presence: `key`, or `webauthn` with that signer's own
    // assertion; `none` signs nothing (decision 3(a)).
    static string? PresenceOf(Entry e, DeviceDescriptor d, string role, Assertion? uv)
    {
        switch (d.Presence)
        {
            case Roster.Presence.Key:
                return uv is null ? null : Invalid.Presence;
            case Roster.Presence.WebAuthn when uv is not null:
                try { WebAuthnAssertion.Check(uv, d.WebAuthn!, Roster.Presence.Challenge(role, e.BodyBytes)); }
                catch (RosterFormatException) { return Invalid.Presence; }
                return null;
            default:
                return Invalid.Presence;
        }
    }

    // ---- quarantine, adoption and the applied state (§3.5) ----

    Bits Quarantined()
    {
        if (_quarantined is not null) { return _quarantined; }
        while (true)
        {
            var q = new Bits(_g.Count);
            for (var i = 0; i < _g.Count; i++)
            {
                var n = _g[i];
                if (_candidate[i] || n.Evidence) { q.Set(i); continue; }
                if (_adopted[i]) { continue; }
                if (n.QuarantinedOnArrival || n.Parents.Any(x => q[x])) { q.Set(i); }
            }
            // An adopting merge adopts once its own signer is outside the quarantine; adoption
            // is irrevocable, and may let another merge adopt in turn.
            var more = false;
            for (var i = 0; i < _g.Count; i++)
            {
                var n = _g[i];
                if (n.Op != EntryOp.Merge || !n.Body.Adopt || _adoptingMerges.Contains(i) || _candidate[i] || q[n.Signer]) { continue; }
                _adoptingMerges.Add(i);
                AdoptClosure(i);
                more = true;
            }
            if (!more) { return _quarantined = q; }
        }
    }

    void AdoptClosure(int index)
    {
        foreach (var a in _g.Closure(index).Members())
        {
            if (!_g[a].Evidence && !_candidate[a]) { _adopted.Set(a); }
        }
    }

    // Local adoption (§3.5): the person at the machine adopts a quarantined branch. Irrevocable.
    public void Adopt(byte[] head)
    {
        if (_g.Find(head) is not { } i || _candidate[i]) { return; }
        AdoptClosure(i);
        _quarantined = null;
        _applied = null;
    }

    // The state this verifier applies: unquarantined entries, and quarantined removals valid in
    // their parents' state computed from unquarantined entries alone. Probation is not applied
    // here (Serving does).
    public RosterState State
    {
        get
        {
            if (_applied is not null) { return _applied; }
            var q = Quarantined();
            var members = new Bits(_g.Count);
            for (var i = 0; i < _g.Count; i++)
            {
                if (!q[i]) { members.Set(i); }
            }
            for (var i = 0; i < _g.Count; i++)
            {
                if (q[i] && _g[i].Op == EntryOp.Remove && !_g[i].Evidence && !_candidate[i] && AppliesUnquarantined(i, members)) { members.Set(i); }
            }
            return _applied = new RosterState(_g, members);
        }
    }

    bool AppliesUnquarantined(int r, Bits unquarantined)
    {
        var n = _g[r];
        var only = new Bits(_g.Count);
        foreach (var a in n.Ancestors.Members())
        {
            if (unquarantined[a]) { only.Set(a); }
        }
        var p = new RosterState(_g, only);
        if (!p.IsActiveAdd(n.Signer) || (n.Cosigner >= 0 && !p.IsActiveAdd(n.Cosigner)) || !p.IsActiveAdd(n.Target)) { return false; }
        if (n.Cosigner >= 0) { return CosignTest(n.Signer, n.Cosigner, n.Target, p); }
        return true;
    }

    // ---- probation (§3.4) ----

    // When this verifier first accepted an add, by its own monotonic clock; an earlier time an
    // approver reported at enrollment or the machine's app reported over the pipe is adopted.
    public void AdoptFirstAccepted(byte[] add, TimeSpan at)
    {
        if (_g.Find(add) is { } i && _g[i].IsAdd && (!_firstAccepted.TryGetValue(i, out var t) || at < t)) { _firstAccepted[i] = at; }
    }

    public bool OnProbation(int add)
    {
        if (_g[add].Op == EntryOp.Genesis || State.IsConfirmed(add)) { return false; }
        return !_firstAccepted.TryGetValue(add, out var t) || clock() - t < Probation;
    }

    // Active ids whose add is held: signed by a device on probation, or by a held device.
    public IReadOnlySet<string> Held
    {
        get
        {
            var state = State;
            var held = new HashSet<int>();
            foreach (var a in state.Active.Values.Order())
            {
                if (IsHeld(a, held)) { held.Add(a); }
            }
            return held.Select(a => _g[a].DeviceId!).ToHashSet(StringComparer.Ordinal);
        }
    }

    bool IsHeld(int add, HashSet<int> known)
    {
        for (var x = add; _g[x].Signer != x; x = _g[x].Signer)
        {
            var s = _g[x].Signer;
            if (known.Contains(s) || OnProbation(s)) { return true; }
        }
        return false;
    }

    // A device's name as this verifier shows it. A rename signed by a device on probation, or by
    // a held device, stays pending until that hold ends (decided by A, Trust lead, 2026-11-04): a
    // device not yet trusted must not change how approvers see it, by renaming itself to look
    // like a device they already know, say. Kept even where the name rules would catch that, as
    // defence in depth.
    public string? Name(string id)
    {
        var held = Held;
        return State.Name(id, r => OnProbation(_g[r].Signer) || held.Contains(_g[_g[r].Signer].DeviceId!));
    }

    // The name a pending rename would give the device once its signer's hold ends, if any.
    public string? PendingName(string id) => State.Name(id) is { } named && named != Name(id) ? named : null;

    // The devices a host serves: active and not held.
    public IReadOnlyDictionary<string, int> Serving
    {
        get
        {
            var held = Held;
            return State.Active.Where(p => !held.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }
    }
}
