// The state of a set of accepted entries (§3.5, "The state", in its order of computation): the
// PWA's port of src/AgentBridge.Roster/RosterState.cs. Which removals revoke and bar which adds,
// which grants are void and why, which adds of one id conflict and which wins, and then which
// devices are active, confirmed and named what. It reads only the entries in the set and what
// each entry's own history fixed, so it is the same in every delivery order for the same set.
import { compare } from './codec.js';
import { isPerson } from './entry.js';
import { Bits, RosterGraph } from './graph.js';

const append = (map, key, value) => {
  const list = map.get(key);
  if (list) list.push(value);
  else map.set(key, [value]);
};

export class RosterState {
  #g;
  #in;
  #active = new Map();
  #confirmed = new Set();
  #names = new Map();
  #renames = new Map();
  #void1;
  #void4 = new Bits();
  #revoked = new Bits();
  #barred = new Bits();

  constructor(g, members) {
    this.#g = g;
    this.#in = members;
    this.#void1 = new Bits(g.count);
    // Ids whose adds conflict, so that none is active (§3.3).
    this.conflicted = new Set();
    // Subordinate adds, and the add that displaced each.
    this.subordinateTo = new Map();
    this.renamedConcurrently = new Set();
    if (g.count === 0) return;
    const n = (i) => g.at(i);
    const entries = members.members();
    const removals = entries.filter((i) => n(i).op === 'remove');
    const adds = entries.filter((i) => n(i).isAdd);

    // Step 1: what each applying removal takes and bars.
    const takenBy = new Map();
    const barredBy = new Map();
    for (const r of removals) {
      const node = n(r);
      for (const a of node.takes.members()) {
        this.#revoked.set(a);
        append(takenBy, a, r);
      }
      const targetId = n(node.target).deviceId;
      for (const b of adds) {
        if (b === node.target || n(b).deviceId !== targetId || g.isAncestor(b, r)) continue;
        const before = n(b).parentState;
        if (!before || !before.isActiveAdd(node.signer)) continue;
        if (node.cosigner >= 0 && !before.isActiveAdd(node.cosigner)) continue;
        this.#barred.set(b);
        append(barredBy, b, r);
      }
    }

    // The raw chains: why each grant would be void if no removal shielded anything.
    const raw = this.#causes(entries, takenBy, barredBy, null);

    // Precedence (§3.5): which removals shield their descendants against which.
    const revokedBy = (m, add) => n(m).takes.has(add) || (raw.get(add)?.has(m) ?? false);
    const signersOf = (r) => (n(r).cosigner >= 0 ? [n(r).signer, n(r).cosigner] : [n(r).signer]);
    const anyIn = (r, target) => signersOf(r).some((s) => g.inSubtree(s, target));
    const shieldedBy = new Map();
    for (const r of removals) {
      for (const m of removals) {
        if (m === r || g.isAncestor(r, m) || !signersOf(r).some((s) => revokedBy(m, s))) continue;
        const rm = n(m).rank;
        const rr = n(r).rank;
        const shields = rm > rr
          || (rm === rr && anyIn(r, n(m).target) && !anyIn(m, n(r).target))
          || (rm === rr && !anyIn(r, n(m).target) && !anyIn(m, n(r).target) && !signersOf(m).some((s) => revokedBy(r, s)));
        if (shields) append(shieldedBy, r, m);
      }
    }
    for (const [i, c] of this.#causes(entries, takenBy, barredBy, shieldedBy)) {
      if (c.size > 0) this.#void1.set(i);
    }

    // Step 2: each id's adds that are neither void nor revoked, for every id at once.
    const sets = new Map();
    for (const a of adds) {
      if (!this.#void1.has(a) && !this.#revoked.has(a)) append(sets, n(a).deviceId, a);
    }

    // Step 3: winners. An add kept by an applying removal of t does not displace an add of its id
    // made outside t's subtree.
    const keptUnder = new Map();
    for (const r of removals) {
      for (const k of n(r).keep) append(keptUnder, k, n(r).target);
    }
    const displaces = (a2, a1) => g.isAncestor(n(a2).signer, a1) && !(keptUnder.get(a2)?.some((t) => !g.inSubtree(a1, t)) ?? false);
    const chosen = new Map();
    for (const [id, set] of sets) {
      if (set.length === 1) {
        chosen.set(id, set[0]);
        continue;
      }
      const winners = set.filter((w) => set.every((o) => o === w || (displaces(w, o) && !displaces(o, w))));
      if (winners.length === 1) {
        chosen.set(id, winners[0]);
        for (const o of set) if (o !== winners[0]) this.subordinateTo.set(o, winners[0]);
      } else {
        this.conflicted.add(id);
      }
    }

    // Step 4: subordinate adds are void, and what they granted, transitively.
    for (const i of entries) {
      const node = n(i);
      if (this.subordinateTo.has(i)) {
        this.#void4.set(i);
        continue;
      }
      if (node.isGrant && node.signer !== i && (this.#void4.has(node.signer) || this.subordinateTo.has(node.signer))) this.#void4.set(i);
    }

    // Step 5: bars, then which ids are active.
    for (const [id, add] of chosen) {
      if (!this.isVoid(add) && !this.#barred.has(add)) this.#active.set(id, add);
    }

    for (const i of entries) {
      const node = n(i);
      if (node.op === 'genesis') this.#confirmed.add(i);
      if (node.op === 'confirm' && !this.isVoid(i)) this.#confirmed.add(node.target);
    }

    // Names: the latest non-void renames of each active add; the smallest hash among several.
    const renames = new Map();
    for (const i of entries) {
      if (n(i).op === 'rename' && !this.isVoid(i)) append(renames, n(i).target, i);
    }
    for (const [target, group] of renames) {
      const id = n(target).deviceId;
      if (id === null || this.#active.get(id) !== target) continue;
      this.#renames.set(id, group);
      const named = this.#latest(group, () => false);
      if (named) {
        this.#names.set(id, n(named.pick).body.name);
        if (named.concurrent) this.renamedConcurrently.add(id);
      }
    }
  }

  static empty = new RosterState(new RosterGraph(), new Bits());

  // Active devices by id, with the add that made each active.
  get active() { return this.#active; }
  get activeCount() { return this.#active.size; }

  activeAdd(id) {
    const a = this.#active.get(id);
    return a === undefined ? null : a;
  }

  isActiveAdd(add) {
    if (add < 0 || add >= this.#g.count) return false;
    const id = this.#g.at(add).deviceId;
    return id !== null && this.#active.get(id) === add;
  }

  isConfirmed(add) { return this.#confirmed.has(add); }
  isVoid(index) { return this.#void1.has(index) || this.#void4.has(index); }
  isRevoked(add) { return this.#revoked.has(add); }
  isBarred(add) { return this.#barred.has(add); }
  contains(index) { return this.#in.has(index); }

  device(id) {
    const a = this.activeAdd(id);
    return a === null ? null : this.#g.at(a).body.device;
  }

  // A device's name; with `pending`, as it is with those renames left pending: the latest of the
  // others, or the name it was enrolled with.
  name(id, pending = null) {
    if (pending === null && this.#names.has(id)) return this.#names.get(id);
    const renames = pending === null ? undefined : this.#renames.get(id);
    if (renames === undefined) return this.device(id)?.name ?? null;
    const named = this.#latest(renames, pending);
    return named ? this.#g.at(named.pick).body.name : this.device(id)?.name ?? null;
  }

  get personDevices() {
    return [...this.#active].filter(([, a]) => isPerson(this.#g.at(a).body.device.kind)).map(([id]) => id);
  }

  #latest(renames, pending) {
    const g = this.#g;
    const applied = renames.filter((x) => !pending(x));
    const latest = applied.filter((x) => !applied.some((y) => y !== x && g.isAncestor(x, y)));
    if (latest.length === 0) return null;
    const pick = latest.reduce((best, x) => (compare(g.at(x).hash, g.at(best).hash) < 0 ? x : best));
    return { pick, concurrent: latest.length > 1 };
  }

  // For each grant, the applying removals it is void because of (§3.5, "Voiding"): directly,
  // where it was signed through an add a removal revokes or bars and is not that removal's
  // ancestor, or through the void add that signed it. A self-removal of D does not void D's
  // grants that are ancestors of another applying removal of D that D did not sign. With shields,
  // a cause R is dropped for every entry descending from a removal that shields against R;
  // without, these are the raw chains.
  #causes(entries, takenBy, barredBy, shields) {
    const g = this.#g;
    const n = (i) => g.at(i);
    const causes = new Map();
    const otherRemovalKeeps = (self, grant) => {
      const target = n(self).target;
      for (const r of entries) {
        if (r === self || n(r).op !== 'remove' || n(r).target !== target) continue;
        if (n(r).signer === target || n(r).cosigner === target) continue;
        if (g.isAncestor(grant, r)) return true;
      }
      return false;
    };
    for (const i of entries) {
      const node = n(i);
      if (!node.isGrant || node.signer < 0 || node.signer === i) continue;
      const signer = node.signer;
      const c = new Set();
      for (const r of [...(takenBy.get(signer) ?? []), ...(barredBy.get(signer) ?? [])]) {
        if (g.isAncestor(i, r)) continue;
        if (n(r).isSelfRemoval && n(r).target === signer && otherRemovalKeeps(r, i)) continue;
        c.add(r);
      }
      for (const r of causes.get(signer) ?? []) c.add(r);
      if (shields && c.size > 0) {
        for (const r of [...c]) {
          if (shields.get(r)?.some((m) => g.isAncestor(m, i))) c.delete(r);
        }
      }
      if (c.size > 0) causes.set(i, c);
    }
    return causes;
  }
}
