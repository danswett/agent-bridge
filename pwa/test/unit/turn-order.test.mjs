// #1642: a reply keyed to its own turn. Turn A streams, message B arrives, A's final text comes after:
// it is A's, drawn under A before B, and never under B's header.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DemoHost } from '../../js/demo.js';
import { HostClient } from '../../js/host-client.js';
import { HistoryStore } from '../../js/history-store.js';
import { liveVoice } from '../../js/attribution.js';
import { shownParts } from '../../js/ui/history-view.js';
import { readSession } from '../../js/protocol.js';

const ID = 's1';
const turn = (n) => `${ID}-turn-${n}`;
const said = (n, responseTurn, response, userMessage) => readSession({
  id: ID, agent: 'copilot', status: 'working', updatedAt: new Date().toISOString(),
  activity: { turn: turn(n), responseTurn: turn(responseTurn), userMessage, response },
});

test("a turn's late reply text sits under its own turn, before the next message, and not under the live turn", async (t) => {
  const host = new DemoHost({ sessions: [{ id: ID, agent: 'copilot', status: 'idle', updatedAt: new Date().toISOString(), activity: { turnCount: 0 } }] }, { answerDelayMs: 0, history: true });
  host.historyTurns.set(ID, []);
  const client = new HostClient(host.connect(), { timeoutMs: 2000 });
  t.after(() => client.close());
  await client.hello();
  const store = new HistoryStore({ current: () => ({ client }) });
  await store.open('h', ID);
  store.live('h', said(1, 1, 'A starts', 'Prompt A'));
  const b = said(2, 1, 'A starts and ends', 'Message B');
  store.live('h', b);
  const [a, live] = store.get('h', ID).turns;
  assert.deepEqual([a.id, live.id], [turn(1), turn(2)]);
  assert.equal(shownParts(a).find((p) => p.kind === 'response')?.text, 'A starts and ends');
  assert.equal(live.live.response ?? null, null);
  assert.equal(liveVoice(b).response, null);
  assert.equal(liveVoice(said(2, 2, 'B text', 'Message B')).response, 'B text');
});
