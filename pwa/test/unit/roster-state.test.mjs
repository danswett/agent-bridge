// The device roster's acceptance rule and state (docs/protocol/vectors/roster-state.json), which
// the C# library generates (tests/AgentBridge.Roster.Tests/RosterStateVectorTests.cs), replayed
// through the PWA's port (js/roster/verifier.js): every scenario's entries offered in order reach
// the same verdict and refusal code at every step, and the same roster after them.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { base64Url, fromBase64Url, hex } from '../../js/roster/codec.js';
import { utf8 } from '../../js/roster/framing.js';
import { parseEntry } from '../../js/roster/entry.js';
import { RosterVerifier } from '../../js/roster/verifier.js';

const vectors = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/roster-state.json', import.meta.url), 'utf8'));
const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
const sorted = (list) => [...list].sort(ordinal);

async function replay(scenario) {
  const nameOf = new Map(Object.entries(scenario.devices).map(([name, id]) => [id, name]));
  const entries = new Map();
  const labelOf = new Map();
  for (const e of scenario.entries) {
    const parsed = await parseEntry(utf8(e.wrapper));
    assert.equal(base64Url(parsed.hash), e.hash, `${e.label}'s hash`);
    entries.set(e.label, parsed);
    labelOf.set(hex(parsed.hash), e.label);
  }
  let now = 0;
  const v = new RosterVerifier(vectors.account, vectors.generation, () => now, { addCap: scenario.limits?.addCap, acceptHostSign: scenario.acceptHostSign === true });
  const servingNames = () => sorted([...v.serving.keys()].map((id) => nameOf.get(id)));
  const steps = [];
  for (const step of scenario.steps) {
    const o = {};
    if (step.hours !== undefined) {
      now = step.hours * 3600 * 1000;
      o.hours = step.hours;
    }
    if (step.offer !== undefined) {
      const [offered] = await v.offer([entries.get(step.offer)]);
      o.offer = step.offer;
      o.verdict = offered.verdict;
      if (offered.reason) o.reason = offered.reason;
    } else if (step.adopt !== undefined) {
      v.adopt(fromBase64Url(scenario.entries.find((e) => e.label === step.adopt).hash, 'vector', 32));
      o.adopt = step.adopt;
    } else {
      o.serving = servingNames();
      o.names = Object.fromEntries(sorted([...v.state.active.keys()].map((id) => nameOf.get(id))).map((n) => [n, v.name(scenario.devices[n])]));
    }
    steps.push(o);
  }

  const s = v.state;
  const label = (i) => labelOf.get(hex(v.graph.at(i).hash));
  const active = {};
  for (const [id, add] of [...s.active].sort(([a], [b]) => ordinal(nameOf.get(a), nameOf.get(b)))) {
    const a = { add: label(add), confirmed: s.isConfirmed(add), name: v.name(id) };
    const pending = v.pendingName(id);
    if (pending !== null) a.pending = pending;
    active[nameOf.get(id)] = a;
  }
  const where = (pick) => sorted([...Array(v.graph.count).keys()].filter(pick).map(label));
  const state = {
    active,
    conflicted: sorted([...s.conflicted].map((id) => nameOf.get(id))),
    heads: v.heads.map((h) => labelOf.get(hex(h))),
    quarantined: where((i) => v.holds(v.graph.at(i).hash) && !v.graph.at(i).evidence && v.isQuarantined(i)),
    evidence: where((i) => v.graph.at(i).evidence),
    serving: servingNames(),
  };
  return { steps, state };
}

for (const scenario of vectors.scenarios) {
  test(`${scenario.name}: ${scenario.about}`, async () => {
    const { steps, state } = await replay(scenario);
    assert.deepEqual(steps, scenario.steps);
    assert.deepEqual(state, scenario.state);
  });
}

test('storage evidence is a non-applying candidate and survives canonical replay', async () => {
  const scenario = vectors.scenarios.find((s) => s.name === 'line');
  const v = new RosterVerifier(vectors.account, vectors.generation, () => 0);
  const wrappers = [];
  for (const item of scenario.entries) {
    const wrapper = JSON.stringify({ ...JSON.parse(item.wrapper), ...(item.label === scenario.entries.at(-1).label ? { evidence: true } : {}) });
    const e = await parseEntry(utf8(wrapper));
    wrappers.push(e.canonical);
    const [result] = await v.offer([e]);
    if (item === scenario.entries.at(-1)) {
      assert.equal(result.verdict, 'refused');
      assert.equal(result.reason, 'cap');
      assert.equal(v.holds(e.hash), false);
      assert.equal(JSON.parse(e.canonical).evidence, true);
    }
  }
  const replay = new RosterVerifier(vectors.account, vectors.generation, () => 0);
  await replay.offer(await Promise.all(wrappers.map((w) => parseEntry(utf8(w)))));
  assert.deepEqual(replay.heads, v.heads);
});

test('a plain copy upgrades marked candidates without duplicating them, including removals', async () => {
  const scenario = vectors.scenarios.find((s) => s.name === 'cascade');
  for (const item of scenario.entries.toReversed()) {
    const v = new RosterVerifier(vectors.account, vectors.generation, () => 0);
    for (const prior of scenario.entries.slice(0, scenario.entries.indexOf(item))) await v.offer([await parseEntry(utf8(prior.wrapper))]);
    const plain = await parseEntry(utf8(item.wrapper));
    const marked = await parseEntry(utf8(JSON.stringify({ ...JSON.parse(item.wrapper), evidence: true })));
    assert.equal((await v.offer([marked]))[0].verdict, 'refused');
    const count = v.graph.count;
    assert.equal((await v.offer([{ ...plain, sig: new Uint8Array(plain.sig.length) }]))[0].reason, 'signature');
    assert.equal((await v.offer([plain]))[0].verdict, 'accepted', item.label);
    assert.equal(v.graph.count, count);
    assert.equal(v.graph.at(v.graph.find(plain.hash)).entry.evidenceOnly, false);
    assert.equal((await v.offer([plain]))[0].verdict, 'duplicate');
    if (plain.body.op === 'remove') assert.equal(v.state.active.has(plain.body.target.id), false);
  }
  const promotion = vectors.scenarios.find((s) => s.name === 'evidence-only-adopting-merge');
  const v = new RosterVerifier(vectors.account, vectors.generation, () => 0);
  for (const item of promotion.entries) await v.offer([await parseEntry(utf8(item.wrapper))]);
  const plain = await parseEntry(utf8(JSON.stringify({ ...JSON.parse(promotion.entries.find((e) => e.label === 'adopt').wrapper), evidence: undefined })));
  assert.equal((await v.offer([plain]))[0].verdict, 'accepted');
  assert.equal(v.graph.at(v.graph.find(plain.hash)).evidence, false);
  const budget = vectors.scenarios.find((s) => s.name === 'evidence'), held = new RosterVerifier(vectors.account, vectors.generation, () => 0);
  for (const item of budget.entries) await held.offer([await parseEntry(utf8(JSON.stringify({ ...JSON.parse(item.wrapper), ...(budget.state.evidence.includes(item.label) ? { evidence: true } : {}) })))]);
  assert.deepEqual((await held.offer(await Promise.all(budget.entries.slice(-2).reverse().map((e) => parseEntry(utf8(e.wrapper)))))).map((r) => r.verdict), ['duplicate', 'duplicate']);
});
