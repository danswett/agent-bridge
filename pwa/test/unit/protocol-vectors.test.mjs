// The shared protocol vectors (docs/protocol/vectors, README.md there), replayed through this
// app's own reading, checks and request building. The C# records generate them, and
// tests/AgentBridge.Protocol.Tests replays the same files, so a field renamed or a limit moved
// on one side fails here until this side follows. Where the two disagree today the vector file
// lists a known difference with its issue; each is checked to still differ, so fixing one fails
// here until its entry goes (tests/AgentBridge.Protocol.Tests/KnownDifferences.cs).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import {
  readActivity, readAnswerResult, readAttachmentState, readBackground, readDiscoveryHeld, readHello, readOwn,
  readQuestion, readSession, readSource, readStopResult, readSubagent, readTool,
} from '../../js/protocol.js';
import { checkAnswer } from '../../js/answers.js';
import { checkReply } from '../../js/replies.js';
import { checkLaunch, readLaunchOptions, readLaunchResult, readTrustQuestion, readTrustResult } from '../../js/launch.js';
import { readUpdateCommandResult, readUpdateState } from '../../js/update.js';
import { readUsage } from '../../js/usage.js';
import { HostClient } from '../../js/host-client.js';
import { PatchError, applyPatch, readPatch } from '../../js/session-patch.js';
import { SessionsStore } from '../../js/sessions-store.js';
import { AnswerService } from '../../js/answer-service.js';
import { ReplyService } from '../../js/reply-service.js';
import { StopService } from '../../js/stop-service.js';
import { LaunchService } from '../../js/launch-service.js';
import { prepareAttachment, uploadAttachment } from '../../js/attachments.js';
import { P256KeyPair } from '../../js/noise-p256.js';
import { deviceIdOf } from '../../js/identity.js';
import { fromBase64url, toBase64url } from '../../js/blobs.js';
import { fromUtf8 } from '../../js/bytes.js';
import { signCommand } from '../../js/transfer.js';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const folder = path.join(pwa, '..', 'docs', 'protocol', 'vectors');
const load = (name) => JSON.parse(readFileSync(path.join(folder, name), 'utf8'));
const SIGNED = 'signed-command.json';
// Recorded sessions patched as a host sends them: not records, replayed on their own below.
const PATCHES = 'session-patches.json';
// The channel's IK handshake, not records: test/unit/channels.test.mjs replays it.
const NOISE = 'noise-ik-p256.json';
const files = readdirSync(folder).filter((f) => f.endsWith('.json') && f !== SIGNED && f !== PATCHES && f !== NOISE).sort().map((f) => [f, load(f)]);
const records = new Map(files.flatMap(([file, json]) => Object.entries(json.records).map(([name, r]) => [name, { ...r, file, contexts: json.contexts }])));
const known = new Map(files.flatMap(([, json]) => json.knownDifferences).filter((d) => d.differs === 'pwa').map((d) => [`${d.vector}:${d.step}`, d]));

// ---- harness ----

// A transport the test plays the host on, as test/unit/host-client.test.mjs does.
function fakeTransport() {
  return {
    sent: [],
    onmessage: null,
    onclose: null,
    send(m) { this.sent.push(m); return Promise.resolve(); },
    close() {},
    host(m) { this.onmessage(JSON.parse(JSON.stringify(m))); },
    lastRequest(op) { return [...this.sent].reverse().find((m) => m.op === op); },
  };
}
const flush = () => new Promise((r) => setImmediate(r));
async function respond(t, op, result) {
  await flush();
  t.host({ re: t.lastRequest(op).id, ok: true, result });
}
async function subscribed({ patch = false, resumed = false, held = [] } = {}) {
  const store = new SessionsStore();
  for (const id of held) { store.upsert({ id, agent: 'copilot', status: 'idle', activity: {} }); }
  if (resumed) { Object.assign(store, { instance: 'run-1', revision: 1, whole: true }); }
  const t = fakeTransport();
  const client = new HostClient(t, { store, timeoutMs: 1000 });
  const p = client.subscribe({ patch: patch || resumed, instance: 'run-1' });
  await respond(t, 'sessions.subscribe', { revision: 1, count: 0, ...(resumed ? { resumed: true } : {}) });
  await p;
  return { t, client, store };
}

// The service worker, against a stubbed scope (as test/unit/sw.test.mjs runs it), showing one push.
const SCOPE = 'https://danswett.github.io/agent-bridge/';
async function shown(message) {
  const handlers = {};
  const list = [];
  const self = {
    registration: { scope: SCOPE, showNotification: async (title, options) => { list.push({ title, ...options }); } },
    clients: { matchAll: async () => [], openWindow: async () => {}, claim() {} },
    addEventListener: (type, fn) => { handlers[type] = fn; },
    skipWaiting() {},
  };
  vm.runInNewContext(readFileSync(path.join(pwa, 'sw.js'), 'utf8'), { self, location: new URL(SCOPE), URL, caches: {}, fetch });
  let done;
  handlers.push({ data: { json: () => JSON.parse(JSON.stringify(message)) }, waitUntil: (p) => { done = p; } });
  await done;
  const n = JSON.parse(JSON.stringify(list[0]));
  // Back into the payload's own names, to hold against the vector.
  return {
    notification: {
      title: n.title, body: n.body, tag: n.tag, lang: n.lang, silent: n.silent, navigate: n.data.url,
      data: { kind: n.data.kind, machine: n.data.machine, session: n.data.session, question: n.data.question },
    },
  };
}

// What one service sends for one press, with request as the vector's.
async function sentBy(Service, op, request, submit) {
  let data;
  const client = {
    closed: false,
    [op]: async (d) => { data = JSON.parse(JSON.stringify(d)); return { outcome: 'delivered' }; },
    attachmentStart: async (d) => ({ received: d.size, complete: true }),
  };
  submit(new Service({ current: () => ({ client, hostId: 'h' }), uuid: () => request, retryMs: 60000 }));
  for (let i = 0; i < 20 && data === undefined; i++) { await flush(); }
  return data;
}

// Every part of the vector's JSON appears, as it is, in what the PWA read, but for the parts it
// does not read (unread, by path, with [] for any element of a list).
function holds(read, wire, unread = [], at = '') {
  if (unread.includes(at)) { return; }
  const where = at || 'the record';
  if (Array.isArray(wire)) {
    assert.ok(Array.isArray(read), `${where}: read as ${JSON.stringify(read)}, not a list`);
    assert.equal(read.length, wire.length, `${where}: ${read.length} entries read of ${wire.length}`);
    wire.forEach((w, i) => holds(read[i], w, unread, `${at}[]`));
  } else if (wire !== null && typeof wire === 'object') {
    assert.ok(read !== null && typeof read === 'object', `${where}: read as ${JSON.stringify(read)}`);
    for (const [k, w] of Object.entries(wire)) { holds(read[k], w, unread, at ? `${at}.${k}` : k); }
  } else {
    assert.equal(read, wire, `${where}: read as ${JSON.stringify(read)}, sent as ${JSON.stringify(wire)}`);
  }
}

// ---- what the PWA does with each record ----

// Records a host sends, by how the PWA reads them: read returns what the views get, or null when
// it drops the record. unread: the parts it has no use for. refuses: false, with why, when it
// cannot drop one it fails to read.
const SESSION_ID = '4f0c2a1e-9d3b-4e6f-8a7c-5b1d2e3f4a5b';
const READS = {
  MachineInfo: { read: (j) => readHello({ protocol: 1, machine: j, capabilities: [] }).machine },
  HelloResult: { read: readHello },
  ToolCall: { read: readTool },
  Option: { read: (j) => readQuestion({ fields: [{ name: 'f', type: 'choice', options: [j] }] }).fields[0].options[0] ?? null },
  Field: { read: (j) => readQuestion({ fields: [j] }).fields[0] ?? null },
  Question: { read: readQuestion },
  Background: { read: readBackground },
  ActivitySource: { read: readSource },
  OwnActivity: { read: readOwn },
  Activity: { read: readActivity },
  Subagent: { read: readSubagent },
  Session: { read: readSession },
  DiscoveryHeld: { read: readDiscoveryHeld },
  // As listAll pages through it: the cursor it reads is the one it sends for the next page.
  SessionList: {
    read: async (j) => {
      const t = fakeTransport();
      const p = new HostClient(t, { timeoutMs: 1000 }).listAll({ maxPages: 2 });
      await respond(t, 'sessions.list', j);
      await flush();
      const next = t.sent[1]?.data?.cursor;
      if (t.sent.length > 1) { t.host({ re: t.sent[1].id, ok: true, result: { sessions: [], revision: j.revision } }); }
      const { sessions, revision } = await p;
      return { sessions, revision, ...(next === undefined ? {} : { next }) };
    },
    // The live view takes the hold from sessions.synced instead.
    unread: ['discoveryHeld'],
  },
  SessionsSynced: {
    // A resumed snapshot's (with live) as a client holding each session it names; any other as a
    // fresh snapshot's. One the PWA cannot take ends no snapshot, and reads as dropped.
    read: async (j) => {
      const live = Array.isArray(j?.live) && j.live.every((id) => typeof id === 'string') ? j.live : null;
      const { t, client, store } = await subscribed(live ? { resumed: true, held: live } : {});
      t.host({ ev: 'sessions.synced', seq: 1, data: j });
      client.close();
      if (!client.synced) { return null; }
      return { revision: store.revision, discoveryHeld: store.held, ...(live ? { live: live.filter((id) => store.has(id)) } : {}) };
    },
  },
  DiscoveryChanged: {
    read: async (j) => {
      const { t, store } = await subscribed();
      t.host({ ev: 'discovery.changed', seq: 1, data: j });
      return { discoveryHeld: store.held };
    },
  },
  SessionRemoved: {
    // After the snapshot, where a removal's revision is the one a reconnect resumes from.
    read: async (j) => {
      const { t, store } = await subscribed({ patch: true });
      t.host({ ev: 'session.upsert', seq: 1, data: { id: SESSION_ID, agent: 'copilot', status: 'idle', activity: {} } });
      t.host({ ev: 'sessions.synced', seq: 2, data: { revision: 1 } });
      let removed = null;
      store.onChange((c) => { if (c.type === 'remove') { removed = c.id; } });
      t.host({ ev: 'session.remove', seq: 3, data: j });
      return removed === null ? null : { id: removed, revision: store.revision };
    },
  },
  SessionPatch: { read: readPatch },
  SubagentsPatch: { read: (j) => readPatch({ id: SESSION_ID, subagents: j })?.subagents ?? null },
  AnswerResult: { read: readAnswerResult },
  ReplyResult: { read: readAnswerResult },
  StopResult: { read: readStopResult },
  AttachmentState: { read: readAttachmentState },
  AttachmentDiscarded: {
    read: async (j) => {
      const t = fakeTransport();
      const p = new HostClient(t, { timeoutMs: 1000 }).attachmentDiscard('x');
      await respond(t, 'attachment.discard', j);
      return { discarded: await p };
    },
  },
  LaunchOptions: { read: readLaunchOptions },
  LaunchAgent: { read: (j) => readLaunchOptions({ agents: [j] }).agents[0] ?? null },
  LaunchTuning: { read: (j) => readLaunchOptions({ agents: [{ id: 'copilot', label: 'Copilot', kind: 'copilot', defaults: j }] }).agents[0].defaults },
  LaunchWorkspace: { read: (j) => readLaunchOptions({ workspaces: [j] }).workspaces[0] ?? null },
  ResumableSession: { read: (j) => readLaunchOptions({ resumable: [j] }).resumable[0] ?? null },
  // The form shows where a launch went, not what it was started with.
  LaunchResult: { read: readLaunchResult, unread: ['launched.profile', 'launched.model', 'launched.effort', 'launched.context'] },
  LaunchedWith: { read: (j) => readLaunchResult({ launched: j }).launched, unread: ['profile', 'model', 'effort', 'context'] },
  LaunchedTransfer: { read: (j) => readLaunchResult({ launched: { agent: 'claude', transfer: j } }).launched.transfer },
  TrustQuestion: { read: readTrustQuestion },
  TrustAnswerResult: { read: readTrustResult },
  UpdateState: { read: readUpdateState },
  UpdateResult: { read: (j) => readUpdateState({ last: j }).last },
  UpdateCommandResult: { read: readUpdateCommandResult },
  UsageReport: { read: readUsage },
  AgentUsage: { read: (j) => readUsage({ agents: [j] }).agents[0] ?? null },
  UsageWindow: { read: (j) => readUsage({ agents: [{ agent: 'copilot', label: 'Copilot', windows: [j] }] }).agents[0].windows[0] ?? null },
  UsageReader: { read: (j) => readUsage({ agents: [{ agent: 'copilot', label: 'Copilot', readBy: j }] }).agents[0].readBy },
  // Every push must show something, or iOS revokes the app's notifications (sw.js): the worker
  // shows a generic notice for whatever it cannot read, and never drops one.
  PushMessage: { read: shown, unread: ['web_push', 'notification.data.v', 'notification.data.at'], refuses: 'a push is always shown' },
  PushNotification: { read: async (j) => (await shown({ notification: j })).notification, unread: ['data.v', 'data.at'], refuses: 'a push is always shown' },
  PushNoticeData: { read: async (j) => (await shown({ notification: { data: j } })).notification.data, unread: ['v', 'at'], refuses: 'a push is always shown' },
};

// Records the PWA sends that it checks first, mirroring the host's check word for word.
const CHECKS = {
  // The PWA answers only the question it shows, by its id.
  AnswerRequest: (j, context) => {
    assert.equal(j.question, context.question.id, 'the vector answers another question than its context asks');
    return checkAnswer(readQuestion(context.question), j);
  },
  ReplyRequest: (j) => checkReply(j),
  LaunchRequest: (j) => checkLaunch(j),
  // A patch the host sends, applied as host-client.js applies it to the session its context holds
  // (from the event at seq), which must then be its context's result exactly.
  SessionPatch: (j, context) => {
    const patch = readPatch(j);
    assert.ok(patch, 'a patch the PWA cannot read at all');
    let applied;
    try { applied = applyPatch(context.session ?? null, context.seq ?? null, patch); }
    catch (e) { if (e instanceof PatchError) { return e.message; } throw e; }
    if (context.result !== undefined) { assert.deepEqual(applied, context.result); }
    return null;
  },
};

// Records the PWA sends, as it builds them: each valid vector built from its own parts must be
// that vector. only: the vectors this way of sending can make. offered: refused vectors whose
// parts a person could give it, which it must refuse or change rather than send as they are.
const UUID_FILE = (id, size = 1) => ({ id, name: 'f', type: '', size, sha256: 'x', bytes: new Uint8Array(size) });
// A picked file of the vector's name, type and size (zeros, which the vectors' SHA-256 is of),
// prepared and started as the reply box does it.
async function sendPrepared(j) {
  const file = new File([new Uint8Array(j.size)], j.name, { type: j.type ?? '' });
  const prepared = await prepareAttachment(file, { uuid: () => j.attachment, shrink: async () => null });
  let sent;
  await uploadAttachment({ attachmentStart: async (d) => { sent = d; return { received: d.size, complete: true }; } }, prepared);
  return sent;
}
const BUILDS = {
  // From what the store holds of the machine: a since only for a list it holds whole from that run.
  SubscribeRequest: {
    build: async (j) => {
      const store = new SessionsStore();
      if (j.since) { Object.assign(store, { instance: j.since.instance, revision: j.since.revision, whole: true }); }
      const t = fakeTransport();
      const client = new HostClient(t, { store, timeoutMs: 1000 });
      client.subscribe({ patch: j.patch === true, instance: j.since?.instance ?? 'run-1' }).catch(() => {});
      await flush();
      client.close();
      return t.lastRequest('sessions.subscribe').data;
    },
  },
  AnswerRequest: { build: (j) => sentBy(AnswerService, 'answer', j.request, (s) => s.submit({ hostId: 'h', sessionId: j.id, questionId: j.question, answer: j.text !== undefined ? { text: j.text } : { fields: j.fields } })) },
  ReplyRequest: { build: (j) => sentBy(ReplyService, 'reply', j.request, (s) => s.submit({ hostId: 'h', sessionId: j.id, text: j.text, attachments: (j.attachments ?? []).map((a) => UUID_FILE(a.id)) })) },
  StopRequest: { build: (j) => sentBy(StopService, 'stop', j.request, (s) => s.submit({ hostId: 'h', sessionId: j.id, confirm: j.confirm ?? null })) },
  LaunchRequest: {
    // From the names LaunchView.settings() (ui/launch-view.js) and its signing give the fields,
    // never the vector's own keys: a field C# renames is then one the PWA would not send.
    build: (j) => {
      const launch = {};
      for (const k of ['agent', 'permissions', 'workspace', 'resume', 'profile', 'model', 'effort', 'context', 'prompt']) {
        if (j[k] !== undefined) { launch[k] = j[k]; }
      }
      if (j.transfer) {
        const c = j.transfer.command;
        launch.transfer = { from: j.transfer.from, session: j.transfer.session, ...(c ? { command: { body: c.body, by: c.by, mac: c.mac } } : {}) };
      }
      if (j.attachments) { launch.attachments = j.attachments.map((a) => ({ id: a.id })); }
      return sentBy(LaunchService, 'launch', j.request, (s) => s.submit({ hostId: 'h', launch }));
    },
  },
  ListRequest: {
    build: async (j) => {
      const t = fakeTransport();
      const p = new HostClient(t, { timeoutMs: 1000 }).listAll({ maxPages: 2 });
      await respond(t, 'sessions.list', { sessions: [], revision: 1, ...(j.cursor ? { next: j.cursor } : {}) });
      await flush();
      const sent = t.sent.at(-1).data;
      t.host({ re: t.sent.at(-1).id, ok: true, result: { sessions: [], revision: 1 } });
      await p;
      return sent;
    },
  },
  AttachmentStart: {
    // In the channel; a blob store or a direct channel is not where a phone's file goes.
    only: ['in-the-channel', 'no-type', 'at-the-channels-256-KiB-limit', 'a-name-at-255-bytes'],
    build: sendPrepared,
    // Its id and SHA-256 are the PWA's own making, so only these come from the person.
    offered: ['a-blank-name', 'a-name-over-255-bytes', 'a-type-over-127-bytes', 'empty', 'over-256-KiB-in-the-channel'],
    made: ['attachment', 'sha256'],
  },
  AttachmentChunk: {
    build: async (j, context) => {
      const bytes = new Uint8Array(context.fileSize);
      bytes.set(Uint8Array.from(Buffer.from(j.data, 'base64')), j.offset);
      let sent;
      const client = {
        attachmentStart: async () => ({ received: j.offset, complete: false }),
        // The first chunk the PWA sends from the vector's offset, whatever follows it.
        attachmentChunk: async (d) => { sent ??= d; return { received: bytes.length, complete: true }; },
      };
      await uploadAttachment(client, { id: j.attachment, name: 'f', type: '', size: bytes.length, sha256: 'x', bytes });
      return sent;
    },
  },
  AttachmentDiscard: {
    build: async (j) => {
      const t = fakeTransport();
      const p = new HostClient(t, { timeoutMs: 1000 }).attachmentDiscard(j.attachment);
      await respond(t, 'attachment.discard', { discarded: true });
      await p;
      return t.lastRequest('attachment.discard').data;
    },
  },
  // Without shared the PWA sends {}, which a host reads as shared: false.
  UsageRequest: {
    only: ['shared'],
    build: async () => {
      const t = fakeTransport();
      const p = new HostClient(t, { timeoutMs: 1000 }).usage({ shared: true });
      await respond(t, 'usage.get', { agents: [] });
      await p;
      return t.lastRequest('usage.get').data;
    },
  },
};

// Records the PWA neither reads nor builds through a function these tests can call, and why.
const NOT_REPLAYED = {
  SubscribeResult: 'the PWA waits for sessions.synced and uses nothing of this result',
  SubscribeSince: 'built only within SubscribeRequest, from what the store holds',
  PushSubscribeRequest: 'PushRegistrar sends the browser\'s own subscription, from a private method',
  PushKeys: 'part of PushSubscribeRequest',
  PushUnsubscribeRequest: 'PushRegistrar sends the browser\'s own endpoint, from a private method',
  PushSubscribeResult: 'the PWA does not read push.subscribe\'s result',
  PushUnsubscribeResult: 'the PWA does not read push.unsubscribe\'s result',
  PushListResult: 'the PWA does not call push.list',
  PushDevice: 'the PWA does not call push.list',
  AttachmentRef: 'written as ReplyRequest\'s attachments, replayed there',
  BlobRef: 'written by the blob-store path of uploadAttachment, which needs a relay',
  LaunchDefaults: 'read only within LaunchOptions, where it must name an agent and a workspace offered',
  TrustAnswerRequest: 'built in ui/launch-view.js; its command is replayed in signed-command.json',
  LaunchTransfer: 'built in ui/launch-view.js; its command is replayed in signed-command.json',
  SignedCommand: 'replayed by signing signed-command.json\'s commands again',
  CommandBody: 'replayed by signing signed-command.json\'s commands again',
  CommandPrincipal: 'replayed by signing signed-command.json\'s commands again',
  CommandTarget: 'replayed by signing signed-command.json\'s commands again',
  TransferRequest: 'host to host: no client sends it',
  TransferResult: 'host to host: no client reads it',
  UpdateCommand: 'built inside UpdateControl from a fresh request, with nothing else in it',
  // The PWA asks for progress from #600; until then a host sends neither to it.
  DeliveryProgress: 'progress frames come only to a client that sends progress: true, which the PWA does not yet',
  DeliveryTimings: 'timings come only to a client that sends progress: true, which the PWA does not yet',
};

// ---- the replay ----

// Known differences are asserted to still differ: a fix fails here until its entry goes.
async function agrees(key, step, check) {
  const d = known.get(`${key}:${step}`);
  if (!d) { await check(); return; }
  await assert.rejects(async () => check(), assert.AssertionError,
    `${key} no longer differs when the PWA ${step}s it: remove its known difference (#${d.issue}) from tests/AgentBridge.Protocol.Tests/KnownDifferences.cs and regenerate`);
}

test('every record in the vectors is replayed here, or says why not', () => {
  const ways = [READS, CHECKS, BUILDS, NOT_REPLAYED].flatMap(Object.keys);
  assert.deepEqual([...records.keys()].filter((r) => !ways.includes(r)), []);
  assert.deepEqual(ways.filter((r) => !records.has(r)), [], 'a table names a record the vectors do not have');
});

test('every known difference names a vector there is', () => {
  for (const { vector: key } of known.values()) {
    const [record, name] = key.split('/');
    const r = records.get(record);
    assert.ok(r && [...r.valid, ...r.refused].some((v) => v.name === name), key);
  }
});

test('every vector a builder names is one there is', () => {
  for (const [record, b] of Object.entries(BUILDS)) {
    const r = records.get(record);
    for (const name of b.only ?? []) { assert.ok(r.valid.some((v) => v.name === name), `${record}/${name}`); }
    for (const name of b.offered ?? []) { assert.ok(r.refused.some((v) => v.name === name), `${record}/${name}`); }
  }
});

for (const [record, r] of records) {
  const reads = READS[record];
  if (reads) {
    for (const v of r.valid) {
      test(`a ${record} "${v.name}" reads with every part the PWA uses intact`,       () => agrees(`${record}/${v.name}`, 'read', async () => {
        holds(await reads.read(JSON.parse(JSON.stringify(v.json))), v.reads ?? v.json, reads.unread);
      }));
    }
    for (const v of r.refused.filter((x) => x.refusedBy === 'parse')) {
      if (reads.refuses) { continue; }
      test(`a ${record} "${v.name}" is dropped, as the host's reader refuses it`,       () => agrees(`${record}/${v.name}`, 'read', async () => {
        const read = await reads.read(JSON.parse(JSON.stringify(v.json)));
        assert.equal(read, null, `read as ${JSON.stringify(read)}`);
      }));
    }
  }
  const check = CHECKS[record];
  if (check) {
    const context = (v) => (v.context ? r.contexts[v.context] : undefined);
    for (const v of r.valid) {
      test(`a ${record} "${v.name}" passes the PWA's check`,       () => agrees(`${record}/${v.name}`, 'check', () => {
        assert.equal(check(v.json, context(v)), null);
      }));
    }
    // A record that cannot be read at all is the host's to refuse: the PWA never writes one.
    for (const v of r.refused.filter((x) => x.refusedBy !== 'parse')) {
      test(`a ${record} "${v.name}" is refused by the PWA's check in the host's words`,       () => agrees(`${record}/${v.name}`, 'check', () => {
        assert.equal(check(v.json, context(v)), v.reason);
      }));
    }
  }
  const builds = BUILDS[record];
  if (builds) {
    const context = (v) => (v.context ? r.contexts[v.context] : undefined);
    for (const v of r.valid.filter((x) => !builds.only || builds.only.includes(x.name))) {
      test(`the PWA sends a ${record} "${v.name}" exactly as the vector has it`, () => agrees(`${record}/${v.name}`, 'build', async () => {
        assert.deepEqual(await builds.build(v.json, context(v)), v.json);
      }));
    }
    for (const v of r.refused.filter((x) => builds.offered?.includes(x.name))) {
      test(`the PWA never sends a ${record} "${v.name}" as it is`, () => agrees(`${record}/${v.name}`, 'build', async () => {
        let sent;
        try { sent = await builds.build(v.json, context(v)); } catch { return; }
        // Only what the person gave counts: the id and digest the PWA makes itself would differ anyway.
        const given = (x) => Object.fromEntries(Object.entries(x).filter(([k]) => !(builds.made ?? []).includes(k)));
        assert.notDeepEqual(given(sent), given(v.json));
      }));
    }
  }
}

// ---- recorded sessions, patched ----

// session-patches.json: each recorded session as a host sends it to a client that asked for
// patches. Every patch, applied as the PWA applies it, must give exactly the session an upsert of
// that moment would: the JSON itself, and what the views are given of it.
const recordings = load(PATCHES).sequences;

for (const sequence of recordings) {
  test(`the recorded "${sequence.name}" patched step by step is exactly the session each upsert would be`, () => {
    let held = null;
    let heldSeq = null;
    for (const e of sequence.events) {
      if (e.upsert) { [held, heldSeq] = [e.upsert, e.seq]; continue; }
      const patch = readPatch(JSON.parse(JSON.stringify(e.patch)));
      assert.ok(patch, `seq ${e.seq} cannot be read`);
      const applied = applyPatch(held, heldSeq, patch);
      assert.deepEqual(applied, e.session, `seq ${e.seq}`);
      assert.deepEqual(readSession(applied), readSession(e.session), `seq ${e.seq}`);
      [held, heldSeq] = [applied, e.seq];
    }
  });

  test(`the recorded "${sequence.name}" shown through a subscription for patches is each session as upserted`, async () => {
    const { t, client, store } = await subscribed({ patch: true });
    for (const e of sequence.events) {
      t.host({ ev: e.upsert ? 'session.upsert' : 'session.patch', seq: e.seq, data: e.upsert ?? e.patch });
      const expected = readSession(e.upsert ?? e.session);
      assert.deepEqual(store.get(expected.id), expected, `seq ${e.seq}`);
    }
    assert.equal(t.sent.filter((m) => m.op === 'sessions.subscribe').length, 1, 'a patch was refused, and subscribed again');
    client.close();
  });
}

// ---- signed commands ----

const signed = load(SIGNED);
const hex = (s) => Uint8Array.from(s.match(/../g).map((b) => parseInt(b, 16)));
const pair = (k) => P256KeyPair.fromParameters(hex(k.d), hex(k.x), hex(k.y));

test('the signed-command keys are the device ids and public keys the vectors say', async () => {
  for (const k of Object.values(signed.keys)) {
    const key = await pair(k);
    assert.equal(toBase64url(key.public), k.public);
    assert.equal(await deviceIdOf(key.public), k.device);
  }
});

for (const v of signed.valid) {
  test(`the PWA signs the "${v.name}" command to the same bytes and MAC`, () => agrees(`signed-command/${v.name}`, 'sign', async () => {
    const [signer, machine] = await Promise.all([pair(signed.keys.signer), pair(signed.keys.machine)]);
    const command = await signCommand({
      key: signer, deviceId: signed.keys.signer.device, machinePublic: machine.public,
      action: v.body.action, session: v.body.target.session, to: v.body.to, now: v.body.issuedAt * 1000, request: v.body.request,
    });
    assert.deepEqual(JSON.parse(fromUtf8.decode(fromBase64url(command.body))), v.body);
    assert.deepEqual(command, v.command);
  }));
}
