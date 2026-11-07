// A session's Details rows, checked against the table both clients share (docs/design/session-details.json,
// ui.md §6.2, #1087): the same rows, in the same order, in the same words. The Windows app's
// tests/AgentBridge.App.Tests/DetailsTableTests.cs reads the same file.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readSession } from '../../js/protocol.js';
import { awayWords, detailRows, startedText } from '../../js/details-rows.js';

const here = (p) => path.join(path.dirname(fileURLToPath(import.meta.url)), p);
const table = JSON.parse(readFileSync(here('../../../docs/design/session-details.json'), 'utf8'));
const now = Date.parse(table.now);
const order = table.rows.map((r) => r.row);

for (const scenario of table.scenarios) {
  test(`Details shows the table's rows for a session with ${scenario.name}, in its order and words`, () => {
    const s = readSession(scenario.session);
    // The words the card gives a machine that is away, as the card makes them.
    const away = scenario.away ? { status: awayWords({ reason: 'offline', since: scenario.away.since }, now).join(' \u00b7 '), away: true } : {};
    const rows = detailRows(s, { machine: scenario.machine, now, ...away });
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

test('Started gains its date once local midnight has passed, with no new start', () => {
  const started = new Date(2026, 10, 6, 23, 30).toISOString();
  const before = startedText(started, new Date(2026, 10, 6, 23, 50).getTime());
  const after = startedText(started, new Date(2026, 10, 7, 0, 10).getTime());
  assert.notEqual(after, before);
  assert.ok(after.includes(before), `${after} holds ${before}`);
});
