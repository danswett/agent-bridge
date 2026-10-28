import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { AttachmentLimits, attachmentName, base64url, prepareAttachment, uploadAttachment } from '../../js/attachments.js';
import { ReplyService } from '../../js/reply-service.js';
import { HostClient, HostError } from '../../js/host-client.js';

const flush = () => new Promise((r) => setImmediate(r));
const sha = (bytes) => createHash('sha256').update(bytes).digest('base64url');
const filled = (n, seed = 1) => Uint8Array.from({ length: n }, (_, i) => (i * 31 + seed) & 0xff);
const file = (bytes, name, type = '') => Object.assign(new Blob([bytes], { type }), { name });
const ids = () => { let n = 0; return () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`; };

// ---- preparing a file ----

test('a file is read, named and hashed as the host will check it', async () => {
  const bytes = filled(5000);
  const f = await prepareAttachment(file(bytes, 'notes.md', 'text/markdown'), { uuid: ids() });
  assert.deepEqual({ ...f, bytes: f.bytes.length }, {
    id: '00000000-0000-4000-8000-000000000001', name: 'notes.md', type: 'text/markdown', size: 5000,
    bytes: 5000, sha256: sha(bytes), image: false, resized: false,
  });
  assert.equal(base64url(Uint8Array.of(0xfb, 0xff)), '-_8');
});

test('a pasted image with no name is named for its type, and an over-long name keeps its extension', async () => {
  assert.equal(attachmentName('', 'image/png'), 'image.png');
  assert.equal(attachmentName(undefined, 'image/jpeg'), 'image.jpg');
  assert.equal(attachmentName('  ', 'application/zip'), 'attachment');
  const long = attachmentName(`${'界'.repeat(100)}.png`, 'image/png');
  assert.ok(new TextEncoder().encode(long).length <= AttachmentLimits.name);
  assert.ok(long.endsWith('界.png'));
});

test('an image over 256 KiB is redrawn smaller as a JPEG, and anything else over it is refused', async () => {
  const big = filled(AttachmentLimits.bytes + 1);
  let asked = null;
  const shrink = async (blob, max) => { asked = { size: blob.size, max }; return new Blob([filled(1000, 7)], { type: 'image/jpeg' }); };
  const f = await prepareAttachment(file(big, 'IMG_0001.HEIC', 'image/heic'), { uuid: ids(), shrink });
  assert.deepEqual(asked, { size: big.length, max: AttachmentLimits.bytes });
  assert.deepEqual([f.name, f.type, f.size, f.resized, f.image], ['IMG_0001.jpg', 'image/jpeg', 1000, true, true]);

  await assert.rejects(prepareAttachment(file(big, 'raw.png', 'image/png'), { shrink: async () => null }),
    { message: 'raw.png is 256 KiB, over the 256 KiB limit for an attachment' });
  await assert.rejects(prepareAttachment(file(filled(3 * 1024 * 1024), 'logs.zip', 'application/zip'), { shrink: async () => assert.fail('only images are shrunk') }),
    { message: 'logs.zip is 3.0 MiB, over the 256 KiB limit for an attachment' });
  await assert.rejects(prepareAttachment(file(new Uint8Array(0), 'empty.txt')), { message: 'empty.txt is empty' });
});

// ---- sending one ----

// A host's attachment ops, keeping what it has of each file as a real one does.
function fakeHost({ failOn = null } = {}) {
  const files = new Map();
  const log = [];
  const client = {
    closed: false,
    async attachmentStart(d) {
      log.push(['start', d.attachment, d.size]);
      if (!files.has(d.attachment)) { files.set(d.attachment, { size: d.size, sha256: d.sha256, chunks: [] }); }
      const f = files.get(d.attachment);
      const received = f.chunks.reduce((n, c) => n + c.length, 0);
      return { received, complete: received === f.size };
    },
    async attachmentChunk(d) {
      const f = files.get(d.attachment);
      const bytes = Buffer.from(d.data, 'base64');
      log.push(['chunk', d.attachment, d.offset, bytes.length]);
      if (failOn?.(d)) { throw failOn(d); }
      f.chunks.push(bytes);
      const received = f.chunks.reduce((n, c) => n + c.length, 0);
      const complete = received === f.size;
      if (complete && sha(Buffer.concat(f.chunks)) !== f.sha256) { throw new HostError('attachment.chunk', 'the attachment\'s bytes do not match its sha256, so it was dropped; send it again', { fromHost: true }); }
      return { received, complete };
    },
    replies: [],
    reply(data) { return new Promise((resolve, reject) => client.replies.push({ data, resolve, reject })); },
  };
  return { client, files, log };
}

test('a file goes in chunks of at most 11 KiB, each from where the host has got to', async () => {
  const { client, files, log } = fakeHost();
  const f = await prepareAttachment(file(filled(30 * 1024), 'shot.png', 'image/png'), { uuid: ids() });
  const seen = [];
  await uploadAttachment(client, f, (n) => seen.push(n));
  assert.deepEqual(log.map((l) => l.slice(0, 1).concat(l.slice(2))), [['start', 30720], ['chunk', 0, 11264], ['chunk', 11264, 11264], ['chunk', 22528, 8192]]);
  assert.deepEqual(seen, [0, 11264, 22528, 30720]);
  assert.deepEqual(Buffer.concat(files.get(f.id).chunks), Buffer.from(f.bytes));
  // Every chunk request fits the 16 KiB the contract allows it, as sent.
  const chunk = { id: 2 ** 31, op: 'attachment.chunk', data: { attachment: f.id, offset: AttachmentLimits.bytes, data: Buffer.alloc(AttachmentLimits.chunk).toString('base64') } };
  assert.ok(new TextEncoder().encode(JSON.stringify(chunk)).length <= 16 * 1024);

  // Sent again (a resend of its reply), a file the host has whole costs one start.
  log.length = 0;
  await uploadAttachment(client, f);
  assert.deepEqual(log.map((l) => l[0]), ['start']);
});

// ---- with a reply ----

function setup(host = fakeHost()) {
  const conn = { client: host.client, hostId: 'h1' };
  const service = new ReplyService({ current: () => conn.client && { client: conn.client, hostId: conn.hostId }, uuid: () => 'r1', retryMs: 5 });
  const seen = [];
  service.watch('h1', 's1', (e) => seen.push(e));
  return { ...host, conn, service, seen };
}

test('a reply sends its files first, reporting how much has gone, then names them', async () => {
  const { client, service, seen, log } = setup();
  const make = ids();
  const a = await prepareAttachment(file(filled(20000), 'a.png', 'image/png'), { uuid: make });
  const b = await prepareAttachment(file(filled(100, 3), 'b.txt'), { uuid: make });

  service.submit({ hostId: 'h1', sessionId: 's1', text: 'look', attachments: [a, b] });
  for (let i = 0; i < 10 && !client.replies.length; i++) { await flush(); }

  assert.deepEqual(client.replies[0].data, { id: 's1', request: 'r1', text: 'look', attachments: [{ id: a.id }, { id: b.id }] });
  assert.deepEqual(log.filter((l) => l[0] === 'start').map((l) => l[1]), [a.id, b.id]);
  const progress = seen.map((e) => e.progress).filter(Boolean);
  assert.deepEqual(progress.at(-1), { sent: 20100, total: 20100 });
  assert.ok(progress.some((p) => p.sent > 0 && p.sent < p.total));
  client.replies[0].resolve({ outcome: 'delivered' });
  await flush();
  assert.equal(service.get('h1', 's1').outcome, 'delivered');
});

test('a reply interrupted while its files go resends only what the host is missing, with the same request', async () => {
  let drop = true;
  const host = fakeHost({ failOn: (d) => (drop && d.offset === 11264 ? (drop = false, new HostError('attachment.chunk', 'the channel closed')) : null) });
  const { client, service, log } = setup(host);
  const a = await prepareAttachment(file(filled(30 * 1024), 'a.png', 'image/png'), { uuid: ids() });

  service.submit({ hostId: 'h1', sessionId: 's1', text: '', attachments: [a] });
  for (let i = 0; i < 20 && service.get('h1', 's1').state !== 'waiting'; i++) { await flush(); }
  assert.equal(service.get('h1', 's1').state, 'waiting');
  service.ready();
  for (let i = 0; i < 20 && !client.replies.length; i++) { await flush(); }

  assert.deepEqual(log.filter((l) => l[0] === 'chunk').map((l) => l[2]), [0, 11264, 11264, 22528]);
  assert.equal(client.replies[0].data.request, 'r1');
});

test('a file the host turns down fails the reply with the host\'s reason, and nothing is replied', async () => {
  const host = fakeHost({ failOn: () => new HostError('attachment.chunk', 'expected the chunk at offset 0', { fromHost: true }) });
  const { client, service } = setup(host);
  const a = await prepareAttachment(file(filled(100), 'a.png', 'image/png'), { uuid: ids() });

  service.submit({ hostId: 'h1', sessionId: 's1', text: 'x', attachments: [a] });
  for (let i = 0; i < 10 && service.get('h1', 's1').state === 'sending'; i++) { await flush(); }

  assert.deepEqual([service.get('h1', 's1').state, service.get('h1', 's1').reason], ['failed', 'expected the chunk at offset 0']);
  assert.equal(client.replies.length, 0);
});

test('the client speaks attachment.start, attachment.chunk and attachment.discard, reading odd results safely', async () => {
  const sent = [];
  const t = { send(m) { sent.push(m); return Promise.resolve(); }, close() {}, onmessage: null, onclose: null };
  const client = new HostClient(t);
  const start = client.attachmentStart({ attachment: 'a1', name: 'a.png', size: 5, sha256: 'x' });
  const chunk = client.attachmentChunk({ attachment: 'a1', offset: 0, data: 'AAAA' });
  const discard = client.attachmentDiscard('a1');
  await flush();
  assert.deepEqual(sent.map((m) => m.op), ['attachment.start', 'attachment.chunk', 'attachment.discard']);
  assert.deepEqual(sent[2].data, { attachment: 'a1' });
  t.onmessage({ re: 1, ok: true, result: { received: 3, complete: false } });
  t.onmessage({ re: 2, ok: true, result: { received: '3', complete: 'yes' } });
  t.onmessage({ re: 3, ok: true, result: { discarded: true } });
  assert.deepEqual(await start, { received: 3, complete: false });
  assert.deepEqual(await chunk, { received: 0, complete: false });
  assert.equal(await discard, true);
});
