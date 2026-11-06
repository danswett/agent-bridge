import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DemoHost } from '../../js/demo.js';

// The demo is what a person sees before connecting a machine: its refusals say "machine" or "bridge",
// never "host" (Creative, 6692784990).
const SESSION = { id: 's1', agent: 'copilot', title: 'T', status: 'idle', statusSince: '2026-01-01T00:00:00Z', startedAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', processId: 1, activity: {} };
const flush = () => new Promise((r) => setTimeout(r, 5));

function ask(host, op, data) {
  const got = [];
  host.receive({ deliver: (m) => got.push(m) }, { id: '1', op, data });
  return flush().then(() => got.find((m) => m.re === '1'));
}

const attachment = { attachment: 'abcdefgh-1', name: 'a.png', size: 10, sha256: 'AAAA' };

test('a chunk for a file the demo machine does not hold says it is not on the machine', async () => {
  const host = new DemoHost({ sessions: [SESSION] }, { answerDelayMs: 0 });

  const response = await ask(host, 'attachment.chunk', { attachment: 'abcdefgh-1', offset: 0, data: '' });

  assert.match(response.error, /^attachment abcdefgh is not on the machine \(/);
});

test('a reply naming a file the demo machine does not hold is refused as not on the machine', async () => {
  const host = new DemoHost({ sessions: [SESSION] }, { answerDelayMs: 0 });

  const response = await ask(host, 'session.reply', { id: 's1', request: 'r1', text: 'hi', attachments: [{ id: '12345678-1234-4234-8234-123456789012' }] });

  assert.equal(response.result.outcome, 'refused');
  assert.match(response.result.reason, /^attachment 12345678 is not on the machine \(/);
});

test('a machine without a blob store says it cannot fetch from one, and one whose file is gone says it could not fetch it', async () => {
  const without = new DemoHost({ sessions: [SESSION] }, { answerDelayMs: 0, large: false });
  const blob = { ...attachment, blob: { id: 'b'.repeat(22), key: 'k'.repeat(43) } };
  const refused = await ask(without, 'attachment.start', blob);

  assert.match(refused.error, /^this machine cannot fetch from the blob store now/);

  const gone = new DemoHost({ sessions: [SESSION] }, { answerDelayMs: 0 });
  await ask(gone, 'attachment.start', blob);
  const failed = await ask(gone, 'attachment.start', blob);

  assert.match(failed.error, /^the machine could not fetch the attachment from the blob store/);
});

test('a launch refused because agent-ha-bridge is running says this bridge leaves launching to it', async () => {
  const host = new DemoHost({ sessions: [SESSION] }, { answerDelayMs: 0 });
  host.scriptLaunches(['refused']);

  const response = await ask(host, 'session.launch', { request: 'r1' });

  assert.equal(response.result.reason, 'agent-ha-bridge is running for this user, so this bridge leaves launching to it');
});
