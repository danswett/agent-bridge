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
  const v = new RosterVerifier(vectors.account, vectors.generation, () => now);
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
