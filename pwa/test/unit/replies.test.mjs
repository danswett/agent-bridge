import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ReplyLimits, checkReply, replyRequest, typedForm } from '../../js/replies.js';
import { ReplyService } from '../../js/reply-service.js';
import { HostClient, HostError } from '../../js/host-client.js';

const REQUEST = '6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b';
const reply = (text, extra = {}) => ({ id: 's1', request: REQUEST, text, ...extra });
const flush = () => new Promise((r) => setImmediate(r));

// ---- checkReply: the C# Replies.Check, case for case and word for word ----

test('a reply must say something, and control characters alone say nothing', () => {
  assert.equal(checkReply(reply('please carry on')), null);
  assert.equal(checkReply(reply(undefined)), 'the reply is empty');
  assert.equal(checkReply(reply(' \t\r\n ')), 'the reply is empty');
  assert.equal(checkReply(reply('\u0001\u001b\u2028\u0085')), 'the reply is empty');
});

test('the 16 KiB limit counts UTF-8 bytes, not characters', () => {
  assert.equal(checkReply(reply('x'.repeat(ReplyLimits.text))), null);
  assert.equal(checkReply(reply('x'.repeat(ReplyLimits.text + 1))), 'the reply is longer than 16 KiB');
  const cjk = '界'.repeat(Math.floor(ReplyLimits.text / 3) + 1);
  assert.ok(cjk.length < ReplyLimits.text);
  assert.equal(checkReply(reply(cjk)), 'the reply is longer than 16 KiB');
});

test('a slash or bang command is refused wherever leading space or control characters hide it', () => {
  assert.match(checkReply(reply('/clear')), /^a reply cannot start with \/, which the agent would run as a command/);
  assert.match(checkReply(reply('  \n\u0001/exit')), /cannot start with \//);
  assert.match(checkReply(reply('\t!rm -rf build')), /^a reply cannot start with !, which the agent would run as a shell command/);
  assert.equal(checkReply(reply('use /tmp and run !build later')), null);
});

test('attachments are refused until their slice, but an empty list or null is none', () => {
  assert.equal(checkReply(reply('see this', { attachments: [{ name: 'shot.png' }] })), 'attachments are not supported yet; send the text alone, or attach at the machine');
  assert.equal(checkReply(reply('see this', { attachments: {} })), 'attachments are not supported yet; send the text alone, or attach at the machine');
  assert.equal(checkReply(reply('see this', { attachments: [] })), null);
  assert.equal(checkReply(reply('see this', { attachments: null })), null);
});

test('text that would make the request larger than a message is refused before it is sent', () => {
  // 16 KiB of control characters is within the text limit but 96 KiB of JSON.
  assert.equal(checkReply(reply(`go${'\u0001'.repeat(ReplyLimits.text - 2)}`)), 'the reply is too large to send');
});

test('the host types every line break and control character as a space, and the text goes well-formed', () => {
  assert.equal(typedForm('first line\r\nsecond\tline\u2028third 🚀'), 'first line  second line third 🚀');
  assert.deepEqual(replyRequest({ sessionId: 's1', request: 'r1', text: 'ok \uD83D then' }), { id: 's1', request: 'r1', text: 'ok \uFFFD then' });
});

// ---- ReplyService ----

function fakeClient() {
  const c = {
    closed: false,
    calls: [],
    reply(data) { return new Promise((resolve, reject) => { c.calls.push({ data, resolve, reject }); }); },
    answer() { throw new Error('a reply never goes as an answer'); },
    last() { return c.calls[c.calls.length - 1]; },
    close() {
      c.closed = true;
      for (const call of c.calls) { call.reject(new HostError('session.reply', 'the channel closed')); }
    },
  };
  return c;
}

function setup() {
  let n = 0;
  const conn = { client: fakeClient(), hostId: 'h1' };
  const service = new ReplyService({ current: () => conn.client && { client: conn.client, hostId: conn.hostId }, uuid: () => `r${++n}`, retryMs: 5 });
  const seen = [];
  service.watch('h1', 's1', (e) => seen.push(`${e.state}${e.outcome ? `:${e.outcome}` : ''}`));
  const submit = (text = 'carry on') => service.submit({ hostId: 'h1', sessionId: 's1', text });
  return { conn, service, seen, submit };
}

test('a reply goes as session.reply with a fresh request, one at a time per session', async () => {
  const { conn, service, seen, submit } = setup();
  submit();
  submit('a second tap while the first is on its way');
  assert.equal(conn.client.calls.length, 1);
  assert.deepEqual(conn.client.last().data, { id: 's1', request: 'r1', text: 'carry on' });
  conn.client.last().resolve({ outcome: 'delivered', reason: null });
  await flush();
  assert.deepEqual(seen, ['sending', 'done:delivered']);
  assert.equal(service.get('h1', 's1').outcome, 'delivered');
});

test('unlike an answer, any outcome leaves the session open to the next reply, as a new request', async () => {
  const { conn, service, submit } = setup();
  for (const [k, outcome] of ['delivered', 'unconfirmed', 'stale', 'busy', 'refused'].entries()) {
    submit(`reply ${k}`);
    assert.equal(conn.client.calls.length, k + 1);
    assert.deepEqual(conn.client.last().data, { id: 's1', request: `r${k + 1}`, text: `reply ${k}` });
    conn.client.last().resolve({ outcome, reason: outcome === 'delivered' ? null : 'why' });
    await flush();
    assert.equal(service.get('h1', 's1').outcome, outcome);
  }
});

test('a reply whose response was lost is resent with the same request on the next channel', async () => {
  const { conn, service, seen, submit } = setup();
  submit();
  conn.client.close();
  await flush();
  assert.equal(service.get('h1', 's1').state, 'waiting');
  conn.client = fakeClient();
  service.ready();
  await flush();
  assert.equal(conn.client.last().data.request, 'r1');
  conn.client.last().resolve({ outcome: 'delivered' });
  await flush();
  assert.deepEqual(seen.at(-1), 'done:delivered');
});

test('a host that fails the op is reported as failed, and Try again resends the same request', async () => {
  const { conn, service, submit } = setup();
  submit();
  conn.client.last().reject(new HostError('session.reply', 'unknown op session.reply', { fromHost: true }));
  await flush();
  assert.equal(service.get('h1', 's1').state, 'failed');
  service.resend('h1', 's1');
  assert.equal(conn.client.last().data.request, 'r1');
});

// ---- HostClient ----

test('the client sends session.reply, waits longer for it than for anything else, and reads an unknown outcome as unconfirmed', async () => {
  const sent = [];
  const t = { send(m) { sent.push(m); return Promise.resolve(); }, close() {}, onmessage: null, onclose: null };
  const client = new HostClient(t, { timeoutMs: 10, answerTimeoutMs: 20 });
  const p = client.reply({ id: 's1', request: 'r1', text: 'go' });
  await new Promise((r) => setTimeout(r, 60));
  assert.deepEqual(sent[0], { id: 1, op: 'session.reply', data: { id: 's1', request: 'r1', text: 'go' } });
  t.onmessage({ re: 1, ok: true, result: { outcome: 'queued' } });
  assert.deepEqual(await p, { outcome: 'unconfirmed', reason: null });
});
