// One verifier's copy of a generation (§3.5): the PWA's port of
// src/AgentBridge.Roster/RosterVerifier.cs, which docs/protocol/vectors/roster-state.json checks.
// The set of accepted entries, which are quarantined, what has been adopted, and the state it
// applies. It decides each offered entry by the acceptance rule, in the order offered; nothing
// accepted is ever dropped.
import { compare } from './codec.js';
import { isPerson, signedBy, signedByCosigner } from './entry.js';
import { verify } from './p256.js';
import { Bits, RosterGraph } from './graph.js';
import { RosterState } from './state.js';
import { INVALID } from './invalid.js';
import { presenceProof } from './presence.js';

export { INVALID };

export const VERDICT = Object.freeze({
  // Accepted and applied (or held for probation, which only delays an effect).
  accepted: 'accepted',
  // Accepted and kept, not applied: outside the window, a fifth head, or on a quarantined branch.
  quarantined: 'quarantined',
  duplicate: 'duplicate',
  // Refused, with its code. One refused only for a cap or budget is kept aside, in case a later
  // removal brings it as evidence; it is never applied.
  refused: 'refused',
});

export const WINDOW = 8;
export const MAX_HEADS = 4;
export const MAX_ADDS = 512;
export const MAX_ENTRIES = 4096;
export const MAX_ACTIVE = 64;
export const SIGNER_BUDGET = 64;
export const RENAMES_PER_ADD = 8;
export const EVIDENCE_PER_SIGNER = 256;
export const EVIDENCE_TOTAL = 1024;
export const PROBATION_MS = 72 * 3600 * 1000;

// `clock` is the verifier's persisted monotonic running time in milliseconds (§3.4): it moves
// only while the verifier runs, never with the wall clock, and survives restarts.
export class RosterVerifier {
  #g = new RosterGraph();
  #closureStates = new Map();
  #children = [];
  #candidate = new Bits();
  #adopted = new Bits();
  #adoptingMerges = new Set();
  #firstAccepted = new Map();
  #refusedCandidates = new Map();
  #budgets = new Map();
  #renames = new Map();
  #evidenceBySigner = new Map();
  #evidence = 0;
  #adds = 0;
  #nonRemovals = 0;
  #quarantined = null;
  #applied = null;
  #clock;

  // acceptHostSign: roster v2 (a host add with hostSign) is switched on per roster, never inferred
  // (agent-principals.md §8.10, V1); until then a v2 add is refused.
  constructor(account, generation, clock, { acceptHostSign = false } = {}) {
    this.acceptHostSign = acceptHostSign;
    this.account = account;
    this.generation = generation;
    this.#clock = clock;
  }

  get graph() { return this.#g; }

  // The entries with no child, in head order: what rosterHead reports.
  get heads() {
    return this.#headIndices().map((i) => this.#g.at(i).hash).sort(compare);
  }

  #headIndices() {
    const out = [];
    for (let i = 0; i < this.#g.count; i++) if (this.#children[i] === 0 && !this.#candidate.has(i)) out.push(i);
    return out;
  }

  isQuarantined(index) { return this.#quarantine().has(index); }

  holds(hash) {
    const i = this.#g.find(hash);
    return i !== null && !this.#candidate.has(i);
  }

  // Parsed entries (js/roster/entry.js parseEntry) in an order where each comes after its
  // parents, as one offer: a removal in it may bring ancestors over a cap or budget as evidence.
  async offer(entries) {
    const results = [];
    for (const e of entries) results.push(await this.#offerOne(e));
    return results;
  }

  async #offerOne(e) {
    const g = this.#g;
    const known = g.find(e.hash);
    if (known !== null) {
      if (!this.#candidate.has(known)) return { hash: e.hash, verdict: VERDICT.duplicate };
      return { hash: e.hash, verdict: VERDICT.refused, reason: this.#refusedCandidates.get(known) ?? INVALID.budget };
    }
    const refused = (reason) => ({ hash: e.hash, verdict: VERDICT.refused, reason });
    const body = e.body;
    if (body.account !== this.account) return refused(INVALID.account);
    if (body.generation !== this.generation) return refused(INVALID.generation);

    const parents = [];
    for (const prev of body.prev) {
      const p = g.find(prev);
      if (p === null) return refused(INVALID.parent);
      parents.push(p);
    }
    if (body.op === 'genesis' && g.count > 0) return refused(INVALID.genesis);
    if (body.op !== 'genesis' && g.count === 0) return refused(INVALID.parent);

    const ancestors = new Bits(g.count + 1);
    for (const p of parents) {
      ancestors.set(p);
      ancestors.or(g.at(p).ancestors);
    }
    const before = parents.length === 0 ? RosterState.empty : this.stateOf(parents);
    const facts = { signer: -1, cosigner: -1, target: -1, keep: [], rank: 0 };
    const reason = await this.#validate(e, parents, ancestors, before, facts);
    const candidateParent = parents.some((p) => this.#candidate.has(p));
    const overCap = reason === INVALID.cap || reason === INVALID.budget;
    if (reason !== null && !overCap) return refused(reason);

    if (overCap || candidateParent) {
      // Kept aside, in case a removal in this or a later offer needs it as evidence.
      if (body.op !== 'remove' || overCap) {
        if (this.#refusedCandidates.size >= 2 * EVIDENCE_TOTAL) return refused(reason ?? INVALID.evidence);
        const c = this.#commit(e, parents, before, facts, true);
        this.#candidate.set(c.index);
        this.#refusedCandidates.set(c.index, reason ?? INVALID.evidence);
        return refused(reason ?? INVALID.evidence);
      }
      const evidence = this.#evidenceFor(facts, ancestors);
      if (evidence === null) return refused(INVALID.evidence);
      for (const x of evidence) {
        this.#candidate.clear(x);
        this.#refusedCandidates.delete(x);
        g.at(x).evidence = true;
        const s = g.at(x).signer;
        this.#evidenceBySigner.set(s, (this.#evidenceBySigner.get(s) ?? 0) + 1);
        this.#evidence++;
        for (const p of g.at(x).parents) this.#children[p]++;
      }
    }

    // §3.5 rule 3: the window and the heads, for everything but removals and merges.
    let arrival = false;
    if (!['remove', 'merge', 'genesis'].includes(body.op)) {
      const q = this.#quarantine();
      let unseen = 0;
      for (let i = 0; i < g.count; i++) {
        if (!q.has(i) && !this.#candidate.has(i) && !g.at(i).evidence && !ancestors.has(i)) unseen++;
      }
      const heads = this.#headIndices().filter((h) => !parents.includes(h)).length + 1;
      arrival = parents.length !== 1 || unseen > WINDOW || heads > MAX_HEADS;
    }

    const node = this.#commit(e, parents, before, facts, false);
    node.quarantinedOnArrival = arrival;
    if (node.isAdd && !this.#firstAccepted.has(node.index)) this.#firstAccepted.set(node.index, this.#clock());
    this.#count(node);
    return { hash: e.hash, verdict: this.#quarantine().has(node.index) ? VERDICT.quarantined : VERDICT.accepted };
  }

  // A candidate (kept aside, not accepted) is no parent's child until it is admitted as evidence.
  #commit(e, parents, before, facts, candidate) {
    const g = this.#g;
    const node = g.add(e, parents);
    node.signer = e.body.op === 'genesis' ? node.index : facts.signer;
    node.cosigner = facts.cosigner;
    node.target = facts.target;
    node.keep = facts.keep;
    node.rank = facts.rank;
    node.parentState = before;
    if (e.body.op === 'remove') {
      const takes = new Bits(g.count);
      takes.set(facts.target);
      if (!node.isSelfRemoval) {
        for (const a of node.ancestors.members()) {
          if (g.at(a).isAdd && g.inSubtree(a, facts.target) && !facts.keep.includes(a)) takes.set(a);
        }
      }
      node.takes = takes;
    }
    this.#children.push(0);
    if (!candidate && !parents.some((p) => this.#candidate.has(p))) {
      for (const p of parents) this.#children[p]++;
    }
    this.#quarantined = null;
    this.#applied = null;
    return node;
  }

  #count(node) {
    const op = node.op;
    if (op !== 'remove') this.#nonRemovals++;
    if (node.isAdd) this.#adds++;
    if (['add', 'confirm', 'merge', 'remove'].includes(op)) {
      const key = `${node.signer}:${op}`;
      this.#budgets.set(key, (this.#budgets.get(key) ?? 0) + 1);
    }
    if (op === 'rename') this.#renames.set(node.target, (this.#renames.get(node.target) ?? 0) + 1);
  }

  // A removal's over-cap ancestors are admitted as evidence only when it is not a self-removal,
  // each is signed by its target or a device of the target's subtree it does not keep, and the
  // verifier's limits hold (§3.3). Null when they are not.
  #evidenceFor(r, ancestors) {
    const g = this.#g;
    const evidence = ancestors.members().filter((i) => this.#candidate.has(i));
    if (r.target === r.signer) return null;
    const bySigner = new Map();
    for (const x of evidence) {
      const s = g.at(x).signer;
      if (s !== r.target && !g.inSubtree(s, r.target)) return null;
      if (r.keep.includes(s)) return null;
      bySigner.set(s, (bySigner.get(s) ?? 0) + 1);
    }
    for (const [s, n] of bySigner) {
      if (n > EVIDENCE_PER_SIGNER || (this.#evidenceBySigner.get(s) ?? 0) + n > EVIDENCE_PER_SIGNER) return null;
    }
    return this.#evidence + evidence.length <= EVIDENCE_TOTAL ? evidence : null;
  }

  // The history state of a set of parents: computed from all their ancestors, quarantine ignored.
  stateOf(parents) {
    if (parents.length === 1) return this.#closureState(parents[0]);
    const members = new Bits(this.#g.count);
    for (const p of parents) members.or(this.#g.closure(p));
    return new RosterState(this.#g, members);
  }

  #closureState(index) {
    let s = this.#closureStates.get(index);
    if (!s) {
      s = new RosterState(this.#g, this.#g.closure(index));
      this.#closureStates.set(index, s);
    }
    return s;
  }

  // The history state an entry was judged in.
  historyState(hash) {
    const i = this.#g.find(hash);
    return i === null ? RosterState.empty : this.#closureState(i);
  }

  // ---- validity (§3.3) ----

  async #validate(e, parents, ancestors, p, facts) {
    const g = this.#g;
    const body = e.body;
    const seq = parents.length === 0 ? 0 : Math.max(...parents.map((x) => g.at(x).body.seq)) + 1;
    if (body.seq !== seq) return INVALID.seq;

    if (body.op === 'genesis') {
      const d = body.device;
      if (!(await verify(d.sign, signedBy(e.bodyBytes), e.sig))) return INVALID.signature;
      return presenceProof(e.bodyBytes, d, 'by', e.uvBy);
    }

    const signer = p.activeAdd(body.by);
    if (signer === null || !isPerson(g.at(signer).body.device.kind)) return INVALID.signer;
    const signerDevice = g.at(signer).body.device;
    if (!(await verify(signerDevice.sign, signedBy(e.bodyBytes), e.sig))) return INVALID.signature;
    const noPresence = await presenceProof(e.bodyBytes, signerDevice, 'by', e.uvBy);
    if (noPresence !== null) return noPresence;
    let cosigner = -1;
    if (body.cosigner !== null) {
      const c = p.activeAdd(body.cosigner);
      if (c === null || !isPerson(g.at(c).body.device.kind)) return INVALID.signer;
      cosigner = c;
      const cd = g.at(c).body.device;
      if (!(await verify(cd.sign, signedByCosigner(e.bodyBytes), e.cosig))) return INVALID.signature;
      const noCoPresence = await presenceProof(e.bodyBytes, cd, 'cosigner', e.uvCosigner);
      if (noCoPresence !== null) return noCoPresence;
    }
    facts.signer = signer;
    facts.cosigner = cosigner;

    switch (body.op) {
      case 'add': {
        if (body.version === 2 && !this.acceptHostSign) return INVALID.version;
        for (const a of ancestors.members()) {
          if (g.at(a).isAdd && g.at(a).deviceId === body.device.id) return INVALID.readded;
        }
        if (p.activeCount >= MAX_ACTIVE) return INVALID.activeCap;
        if (this.#adds >= MAX_ADDS || this.#nonRemovals >= MAX_ENTRIES) return INVALID.cap;
        return this.#overBudget(signer, 'add');
      }
      case 'remove': {
        const t = this.#targetOf(body, ancestors, p);
        if (t === null) return INVALID.target;
        facts.target = t;
        facts.rank = cosigner >= 0 ? 2 : 1;
        const self = body.target.id === body.by;
        if (!self && cosigner < 0 && !g.isAncestor(signer, t)) return INVALID.seniority;
        if (cosigner >= 0 && !this.#cosignTest(signer, cosigner, t, p)) return INVALID.cosign;
        const keep = [];
        for (const k of body.keep) {
          const ki = g.find(k);
          if (ki === null || !ancestors.has(ki) || !g.at(ki).isAdd || !g.inSubtree(ki, t)) return INVALID.keep;
          keep.push(ki);
        }
        for (const k of keep) {
          const s = g.at(k).signer;
          if (s !== t && !keep.includes(s)) return INVALID.keep;
          // Not a copy: no other add of its id in the removal's parents' history.
          for (const a of ancestors.members()) {
            if (a !== k && g.at(a).isAdd && g.at(a).deviceId === g.at(k).deviceId) return INVALID.keep;
          }
        }
        facts.keep = keep;
        if (!self) {
          for (const s of cosigner >= 0 ? [signer, cosigner] : [signer]) {
            if (s === t || (g.inSubtree(s, t) && !keep.includes(s))) return INVALID.signer;
          }
        }
        return this.#overBudget(signer, 'remove');
      }
      case 'rename': {
        const t = this.#targetOf(body, ancestors, p);
        if (t === null) return INVALID.target;
        facts.target = t;
        if (t !== signer && !g.isAncestor(signer, t)) return INVALID.seniority;
        if ((this.#renames.get(t) ?? 0) >= RENAMES_PER_ADD) return INVALID.budget;
        return this.#nonRemovals >= MAX_ENTRIES ? INVALID.cap : null;
      }
      case 'confirm': {
        const t = this.#targetOf(body, ancestors, p);
        if (t === null) return INVALID.target;
        facts.target = t;
        if (!p.isConfirmed(signer) || g.at(t).signer !== signer || p.isConfirmed(t)) return INVALID.confirm;
        if (this.#nonRemovals >= MAX_ENTRIES) return INVALID.cap;
        return this.#overBudget(signer, 'confirm');
      }
      case 'merge': {
        // Two to four distinct heads: none the ancestor of another.
        for (const a of parents) {
          if (parents.some((b) => g.isAncestor(a, b))) return INVALID.merge;
        }
        if (this.#nonRemovals >= MAX_ENTRIES) return INVALID.cap;
        return this.#overBudget(signer, 'merge');
      }
      default:
        return INVALID.target;
    }
  }

  #overBudget(signer, op) {
    return (this.#budgets.get(`${signer}:${op}`) ?? 0) >= SIGNER_BUDGET ? INVALID.budget : null;
  }

  // The add a remove, rename or confirm names: an ancestor that adds that id, active in the
  // parents' state.
  #targetOf(body, ancestors, p) {
    const g = this.#g;
    const t = g.find(body.target.add);
    if (t === null || !ancestors.has(t) || !g.at(t).isAdd || g.at(t).deviceId !== body.target.id) return null;
    return p.isActiveAdd(t) ? t : null;
  }

  // Both signers confirmed in the parents' state, and their lineages, cut at the target (its add
  // and every add senior to it left out), disjoint (§3.4).
  #cosignTest(a, b, target, p) {
    if (!p.isConfirmed(a) || !p.isConfirmed(b)) return false;
    const g = this.#g;
    const kept = (x) => x !== target && !g.isAncestor(x, target);
    const cutA = new Set(g.lineage(a).filter(kept));
    return !g.lineage(b).filter(kept).some((x) => cutA.has(x));
  }

  // ---- quarantine, adoption and the applied state (§3.5) ----

  #quarantine() {
    if (this.#quarantined !== null) return this.#quarantined;
    const g = this.#g;
    for (;;) {
      const q = new Bits(g.count);
      for (let i = 0; i < g.count; i++) {
        const n = g.at(i);
        if (this.#candidate.has(i) || n.evidence) {
          q.set(i);
          continue;
        }
        if (this.#adopted.has(i)) continue;
        if (n.quarantinedOnArrival || n.parents.some((x) => q.has(x))) q.set(i);
      }
      // An adopting merge adopts once its own signer is outside the quarantine; adoption is
      // irrevocable, and may let another merge adopt in turn.
      let more = false;
      for (let i = 0; i < g.count; i++) {
        const n = g.at(i);
        if (n.op !== 'merge' || !n.body.adopt || this.#adoptingMerges.has(i) || this.#candidate.has(i) || q.has(n.signer)) continue;
        this.#adoptingMerges.add(i);
        this.#adoptClosure(i);
        more = true;
      }
      if (!more) {
        this.#quarantined = q;
        return q;
      }
    }
  }

  #adoptClosure(index) {
    for (const a of this.#g.closure(index).members()) {
      if (!this.#g.at(a).evidence && !this.#candidate.has(a)) this.#adopted.set(a);
    }
  }

  // Local adoption (§3.5): the person at the machine adopts a quarantined branch. Irrevocable.
  adopt(head) {
    const i = this.#g.find(head);
    if (i === null || this.#candidate.has(i)) return;
    this.#adoptClosure(i);
    this.#quarantined = null;
    this.#applied = null;
  }

  // The state this verifier applies: unquarantined entries, and quarantined removals valid in
  // their parents' state computed from unquarantined entries alone. Probation is not applied
  // here (serving does).
  get state() {
    if (this.#applied !== null) return this.#applied;
    const g = this.#g;
    const q = this.#quarantine();
    const members = new Bits(g.count);
    for (let i = 0; i < g.count; i++) if (!q.has(i)) members.set(i);
    for (let i = 0; i < g.count; i++) {
      const n = g.at(i);
      if (q.has(i) && n.op === 'remove' && !n.evidence && !this.#candidate.has(i) && this.#appliesUnquarantined(i, members)) members.set(i);
    }
    this.#applied = new RosterState(g, members);
    return this.#applied;
  }

  #appliesUnquarantined(r, unquarantined) {
    const g = this.#g;
    const n = g.at(r);
    const only = new Bits(g.count);
    for (const a of n.ancestors.members()) if (unquarantined.has(a)) only.set(a);
    const p = new RosterState(g, only);
    if (!p.isActiveAdd(n.signer) || (n.cosigner >= 0 && !p.isActiveAdd(n.cosigner)) || !p.isActiveAdd(n.target)) return false;
    if (n.cosigner >= 0) return this.#cosignTest(n.signer, n.cosigner, n.target, p);
    return true;
  }

  // ---- probation (§3.4) ----

  // When this verifier first accepted an add, by its own clock; an earlier time an approver
  // reported at enrollment, or the machine's app reported, is adopted.
  adoptFirstAccepted(add, at) {
    const i = this.#g.find(add);
    if (i === null || !this.#g.at(i).isAdd) return;
    const t = this.#firstAccepted.get(i);
    if (t === undefined || at < t) this.#firstAccepted.set(i, at);
  }

  onProbation(add) {
    if (this.#g.at(add).op === 'genesis' || this.state.isConfirmed(add)) return false;
    const t = this.#firstAccepted.get(add);
    return t === undefined || this.#clock() - t < PROBATION_MS;
  }

  // Active ids whose add is held: signed by a device on probation, or by a held device.
  get held() {
    const held = new Set();
    for (const a of [...this.state.active.values()].sort((x, y) => x - y)) {
      if (this.#isHeld(a, held)) held.add(a);
    }
    return new Set([...held].map((a) => this.#g.at(a).deviceId));
  }

  #isHeld(add, known) {
    const g = this.#g;
    for (let x = add; g.at(x).signer !== x; x = g.at(x).signer) {
      const s = g.at(x).signer;
      if (known.has(s) || this.onProbation(s)) return true;
    }
    return false;
  }

  // A device's name as this verifier shows it. A rename signed by a device on probation, or by a
  // held device, stays pending until that hold ends (decided by A, Trust lead, 2026-11-04).
  name(id) {
    const held = this.held;
    const g = this.#g;
    return this.state.name(id, (r) => this.onProbation(g.at(r).signer) || held.has(g.at(g.at(r).signer).deviceId));
  }

  // The name a pending rename would give the device once its signer's hold ends, if any.
  pendingName(id) {
    const named = this.state.name(id);
    return named !== null && named !== this.name(id) ? named : null;
  }

  // The devices a host serves: active and not held.
  get serving() {
    const held = this.held;
    return new Map([...this.state.active].filter(([id]) => !held.has(id)));
  }
}
