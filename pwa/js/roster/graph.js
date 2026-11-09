// The accepted entries of one generation as a DAG: the PWA's port of
// src/AgentBridge.Roster/RosterGraph.cs, which docs/protocol/vectors/roster-state.json checks.
import { hex } from './codec.js';

// A set of entry indices. Entries are indexed in acceptance order, which is topological (an
// entry is accepted only after its parents), so an ancestor always has a lower index than its
// descendants.
export class Bits {
  constructor(capacity = 32) {
    this.words = new Uint32Array(Math.max(1, (capacity + 31) >>> 5));
  }

  has(i) {
    return i >= 0 && (i >>> 5) < this.words.length && (this.words[i >>> 5] & (1 << (i & 31))) !== 0;
  }

  set(i) {
    if ((i >>> 5) >= this.words.length) this.#grow((i >>> 5) + 1);
    this.words[i >>> 5] |= 1 << (i & 31);
  }

  clear(i) {
    if ((i >>> 5) < this.words.length) this.words[i >>> 5] &= ~(1 << (i & 31));
  }

  or(other) {
    if (other.words.length > this.words.length) this.#grow(other.words.length);
    for (let w = 0; w < other.words.length; w++) this.words[w] |= other.words[w];
  }

  clone() {
    const c = new Bits(0);
    c.words = this.words.slice();
    return c;
  }

  // Every member, ascending.
  members() {
    const out = [];
    for (let w = 0; w < this.words.length; w++) {
      let word = this.words[w];
      while (word !== 0) {
        const low = word & -word;
        out.push((w << 5) + 31 - Math.clz32(low));
        word ^= low;
      }
    }
    return out;
  }

  #grow(length) {
    const words = new Uint32Array(Math.max(this.words.length * 2, length));
    words.set(this.words);
    this.words = words;
  }
}

// One accepted entry and what its own history fixes about it (§3.3): who signed it (the add that
// made `by` active in its parents' state), what it targets and, for a removal, its rank and what
// it takes. All of it is read from the entry's ancestors alone.
export class RosterNode {
  constructor(index, entry, parents, ancestors) {
    this.index = index;
    this.entry = entry;
    this.parents = parents;
    // Every ancestor's index, not the entry's own.
    this.ancestors = ancestors;
    this.signer = -1;
    this.cosigner = -1;
    this.target = -1;
    this.keep = [];
    this.takes = null;
    this.rank = 0;
    // The state of the entry's parents (history, quarantine ignored), which bars read.
    this.parentState = null;
    // Kept as evidence for a removal: stored and gossiped, never applied.
    this.evidence = false;
    // Quarantined when accepted (outside the window, or a fifth head).
    this.quarantinedOnArrival = false;
  }

  get body() { return this.entry.body; }
  get op() { return this.entry.body.op; }
  get hash() { return this.entry.hash; }
  get isSelfRemoval() { return this.op === 'remove' && this.target === this.signer; }
  get isAdd() { return this.op === 'add' || this.op === 'genesis'; }
  get isGrant() { return this.op === 'add' || this.op === 'rename' || this.op === 'confirm'; }
  get deviceId() { return this.entry.body.device?.id ?? null; }
}

export class RosterGraph {
  #byHash = new Map();

  constructor() {
    this.nodes = [];
  }

  get count() { return this.nodes.length; }

  at(index) { return this.nodes[index]; }

  find(hash) {
    const i = this.#byHash.get(hex(hash));
    return i === undefined ? null : i;
  }

  add(entry, parents) {
    const ancestors = new Bits(this.nodes.length + 1);
    for (const p of parents) {
      ancestors.set(p);
      ancestors.or(this.nodes[p].ancestors);
    }
    const node = new RosterNode(this.nodes.length, entry, parents, ancestors);
    this.nodes.push(node);
    this.#byHash.set(hex(entry.hash), node.index);
    return node;
  }

  // Whether a is a strict ancestor of b.
  isAncestor(a, b) { return a < b && this.nodes[b].ancestors.has(a); }

  // The entry and its ancestors.
  closure(index) {
    const c = this.nodes[index].ancestors.clone();
    c.set(index);
    return c;
  }

  // The add's approval lineage: the add, the add that signed it, and so on to the genesis.
  lineage(add) {
    const out = [];
    for (let x = add; ; x = this.nodes[x].signer) {
      out.push(x);
      if (this.nodes[x].signer === x || this.nodes[x].signer < 0) return out;
    }
  }

  // Whether `add` is in t's approval subtree: t is in its lineage, other than as the add itself.
  inSubtree(add, t) {
    return add !== t && this.lineage(add).includes(t);
  }
}
