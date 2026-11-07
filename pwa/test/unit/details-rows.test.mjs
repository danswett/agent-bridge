// A session's Details rows, checked against the table both clients share (docs/design/session-details.json,
// ui.md §6.2, #1087): the same rows, in the same order, in the same words. The Windows app's
// tests/AgentBridge.App.Tests/DetailsTableTests.cs reads the same file.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readSession } from '../../js/protocol.js';
import { sessionStatus } from '../../js/status.js';
import { detailRows } from '../../js/details-rows.js';

const here = (p) => path.join(path.dirname(fileURLToPath(import.meta.url)), p);
const table = JSON.parse(readFileSync(here('../../../docs/design/session-details.json'), 'utf8'));
const now = Date.parse(table.now);
const order = table.rows.map((r) => r.row);

for (const scenario of table.scenarios) {
  test(`Details shows the table's rows for a session with ${scenario.name}, in its order and words`, () => {
    const s = readSession(scenario.session);
    const rows = detailRows(s, { status: sessionStatus(s).text, machine: scenario.machine, now });
    assert.deepEqual(rows.map((r) => r.term), scenario.rows.map((r) => r.row));
    scenario.rows.forEach((want, i) => {
      const got = rows[i];
      if (want.value !== undefined) { assert.equal(got.text, want.value, want.row); } else { assert.match(got.text, new RegExp(want.match, 'u'), `${want.row}: ${got.text}`); }
      assert.equal(got.mono, table.rows.find((r) => r.row === want.row).mono === true, `${want.row} in the mono face`);
    });
  });

  test(`The table's own rows for ${scenario.name} keep the table's order`, () => {
    const at = scenario.rows.map((r) => order.indexOf(r.row));
    assert.ok(at.every((n, i) => n >= 0 && (i === 0 || n > at[i - 1])), at.join(','));
  });
}

test('Details leaves out Driven by while the machine is away, as the Windows app does, and keeps In this state for an idle session', () => {
  const s = readSession(table.scenarios[0].session);
  assert.ok(detailRows(s, { now }).some((r) => r.term === 'Driven by'));
  assert.ok(!detailRows(s, { away: true, now }).some((r) => r.term === 'Driven by'));
  const idle = readSession(table.scenarios[1].session);
  assert.ok(!detailRows(idle, { now }).some((r) => r.term === 'In this state'));
  assert.ok(detailRows(idle, { away: true, now }).some((r) => r.term === 'In this state'));
});
