// The demo's read-only "shadow" machine has one label, in js/demo.js, and the browser specs that look at it assert that exact text. They run only
// in a browser, so this keeps the two in step without one: a label changed on one side and not the other fails here, not in a browser run.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = (name) => readFileSync(new URL(`../${name}`, import.meta.url), 'utf8');
const label = /machineId: 'demo-shadow', machineName: '([^']+)'/.exec(read('../js/demo.js'))?.[1];

test('the demo\'s shadow machine has a neutral label, and no machine name is its words', () => {
  assert.ok(label, 'js/demo.js names its shadow machine');
  assert.match(label, /^[a-z]+-[a-z] \(shadow\)$/);
  // A machine's tab shows at most 16 characters of its name on a wide layout (ui/views.js), so the label is not cut.
  assert.ok(label.length <= 16, label);
});

for (const spec of ['launch.spec.mjs', 'diagnose.spec.mjs', 'machines.spec.mjs']) {
  test(`${spec} asserts the label the demo gives its shadow machine, and no other shadow label`, () => {
    const text = read(spec);
    assert.ok(text.includes(label), `${spec} must assert ${label}`);
    const labels = new Set([...text.matchAll(/[A-Za-z0-9-]+ \(shadow\)/g)].map((m) => m[0]));
    assert.deepEqual([...labels], [label]);
  });
}
