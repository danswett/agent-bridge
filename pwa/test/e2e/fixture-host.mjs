// A minimal Bridge Host for the end-to-end tests, in Node: it signs in through the relay
// like any device, accepts Noise channels as the responder, and answers the sessions
// protocol (hello, sessions.list, session.get, sessions.subscribe) with events, and
// session.answer as a host must: it checks the answer (with the app's mirror of the C#
// Answers.Check), says stale when the session is no longer asking, returns a repeated
// request's first outcome without "typing" again, even while the first is still being
// delivered, and takes an answered question away with an upsert; and session.reply the same
// way (Replies.Check through the app's mirror, refused while the session asks a question,
// only Copilot sessions, a repeat typed once). It offers push and records push.subscribe and
// push.unsubscribe, but sends no notifications. Given a usage report, it offers usage as
// well (usage.get, usage.subscribe, and usage.update when a test sets a new one) and reports
// itself as a dev build with its session count. It reuses
// the app's own relay and Noise code, so it proves the wire format against the real relay
// and the real browser crypto, not against a copy of either.
//
// It is not the product host (that is C#, src/), only enough of one to drive the PWA.
import { P256KeyPair, XXHandshake, hex } from '../../js/noise-p256.js';
import { Relay } from '../../js/relay.js';
import { KIND, HEADER, prologue } from '../../js/channels.js';
import { deviceIdOf } from '../../js/identity.js';
import { startDeviceFlow } from '../../js/signin.js';
import { concat, fromUtf8, utf8 } from '../../js/bytes.js';
import { checkAnswer } from '../../js/answers.js';
import { checkReply, typedForm } from '../../js/replies.js';
import { readQuestion } from '../../js/protocol.js';

const EMPTY = new Uint8Array(0);

export async function startFixtureHost({ base, name = 'fixture-host', sessions = [], answering = true, replying = answering, usage = null }) {
  const key = await P256KeyPair.generate();
  const deviceId = await deviceIdOf(key.public);
  const account = await (await startDeviceFlow(base)).wait();
  const relay = new Relay({ base, key, deviceId, name, kind: 'host' });
  await relay.connect(account.token);

  const state = new Map(sessions.map((s) => [s.id, s]));
  const channels = new Map();
  const log = [];
  let revision = 1;
  // session.answer: every request as received, the outcome of each request id (a promise,
  // so a repeat during delivery waits for the same one), what was typed, and the outcomes
  // a test has scripted for the next answers (delivered once they run out).
  const answers = [];
  const outcomes = new Map();
  const typed = [];
  const script = [];
  // session.reply, likewise.
  const replies = [];
  const replyOutcomes = new Map();
  const typedReplies = [];
  const replyScript = [];
  // push.subscribe and push.unsubscribe requests, in order.
  const push = [];

  const send = (ch, kind, body) => relay.send(ch.peer, concat(new Uint8Array([kind]), ch.id, body));
  const sendJson = (ch, message) => {
    const p = ch.chain.then(async () => send(ch, KIND.transport, await ch.hs.send.encryptWithAd(EMPTY, utf8.encode(JSON.stringify(message)))));
    ch.chain = p.catch(() => {});
    return p;
  };
  const event = (ch, ev, data) => sendJson(ch, { ev, seq: ++ch.seq, data });
  const listed = (s) => { const c = structuredClone(s); delete c.activity?.history; return c; };

  async function serve(ch, { id, op, data }) {
    log.push(op);
    const reply = (result) => sendJson(ch, { re: id, ok: true, result });
    switch (op) {
      case 'hello':
        return reply({ protocol: 1, machine: { id: deviceId, name, os: 'linux', hostVersion: '0.0.0-fixture', ...(usage ? { dev: true, sessions: state.size } : {}) }, capabilities: ['sessions.read', ...(answering ? ['sessions.answer'] : []), ...(replying ? ['sessions.reply'] : []), 'push', ...(usage ? ['usage'] : [])] });
      case 'usage.get':
        if (!usage) { break; }
        return reply(usage);
      case 'usage.subscribe':
        if (!usage) { break; }
        ch.usage = true;
        return reply(usage);
      case 'sessions.list':
        return reply({ sessions: [...state.values()].map(listed), revision });
      case 'session.get': {
        const s = state.get(data?.id);
        return reply(s ? { ...s, activity: { ...s.activity, history: s.activity?.history ?? [] } } : { missing: true });
      }
      case 'sessions.subscribe':
        ch.subscribed = true;
        await reply({ revision, count: state.size });
        for (const s of state.values()) { await event(ch, 'session.upsert', listed(s)); }
        return event(ch, 'sessions.synced', { revision });
      case 'session.answer':
        if (!answering) { break; }
        answers.push(structuredClone(data));
        if (!outcomes.has(data?.request)) { outcomes.set(data?.request, answer(data)); }
        return reply(await outcomes.get(data?.request));
      case 'session.reply':
        if (!replying) { break; }
        replies.push(structuredClone(data));
        if (!replyOutcomes.has(data?.request)) { replyOutcomes.set(data?.request, replyTo(data)); }
        return reply(await replyOutcomes.get(data?.request));
      // Notifications: what a phone registers is recorded, as the C# host would store it.
      case 'push.subscribe':
        push.push({ op, peer: ch.peer, peerName: ch.peerName, data: structuredClone(data) });
        return reply({ subscribed: true, count: 1 });
      case 'push.unsubscribe':
        push.push({ op, peer: ch.peer, data: structuredClone(data) });
        return reply({ removed: true });
      default:
        break;
    }
    return sendJson(ch, { re: id, ok: false, error: `unknown op ${op}` });
  }

  async function answer(data) {
    const step = script.shift() ?? 'delivered';
    const { outcome, reason, holdMs = 0 } = typeof step === 'string' ? { outcome: step } : step;
    // Delivering takes a while; holdMs lets a test drop the channel in the middle of it.
    if (holdMs) { await new Promise((r) => setTimeout(r, holdMs)); }
    const s = state.get(data?.id);
    const q = s?.question ? readQuestion(s.question) : null;
    if (!q || q.id !== data.question) { return { outcome: 'stale', reason: 'the session is no longer asking that question' }; }
    const problem = checkAnswer(q, data);
    if (problem) { return { outcome: 'refused', reason: problem }; }
    if (['stale', 'busy', 'refused'].includes(outcome)) { return { outcome, reason: reason ?? outcome }; }
    typed.push({ id: s.id, question: q.id, request: data.request, fields: data.fields, text: data.text });
    await upsert({ ...s, status: 'working', statusSince: new Date().toISOString(), updatedAt: new Date().toISOString(), question: null });
    return outcome === 'delivered' ? { outcome } : { outcome, reason: reason ?? 'not confirmed' };
  }

  async function replyTo(data) {
    const step = replyScript.shift() ?? 'delivered';
    const { outcome, reason, holdMs = 0 } = typeof step === 'string' ? { outcome: step } : step;
    if (holdMs) { await new Promise((r) => setTimeout(r, holdMs)); }
    const problem = checkReply(data);
    if (problem) { return { outcome: 'refused', reason: problem }; }
    const s = state.get(data?.id);
    if (!s) { return { outcome: 'stale', reason: 'the session has ended' }; }
    if (s.agent !== 'copilot') { return { outcome: 'refused', reason: `replying is not supported for ${s.agent} yet; reply at the machine` }; }
    if (s.question) { return { outcome: 'refused', reason: 'the session is waiting for an answer to its question; answer that first' }; }
    if (['stale', 'busy', 'refused'].includes(outcome)) { return { outcome, reason: reason ?? outcome }; }
    const said = typedForm(data.text);
    typedReplies.push({ id: s.id, request: data.request, said });
    const t = new Date().toISOString();
    await upsert({ ...s, status: 'working', statusSince: t, updatedAt: t, activity: { ...s.activity, userMessage: said } });
    return outcome === 'delivered' ? { outcome } : { outcome, reason: reason ?? 'not confirmed' };
  }

  relay.onData = async (from, payload) => {
    if (payload.length < HEADER) { return; }
    const kind = payload[0];
    const id = payload.slice(1, HEADER);
    const k = `${from}:${hex(id)}`;
    const body = payload.slice(HEADER);
    let ch = channels.get(k);
    try {
      if (kind === KIND.msg1 && !ch) {
        ch = { peer: from, id, seq: 0, chain: Promise.resolve(), subscribed: false, hs: await XXHandshake.create(false, key, prologue(from, deviceId, id)) };
        channels.set(k, ch);
        await ch.hs.readMessage(body);
        send(ch, KIND.msg2, await ch.hs.writeMessage(utf8.encode(JSON.stringify({ name, kind: 'host' }))));
      } else if (kind === KIND.msg3 && ch && !ch.hs.isComplete) {
        const intro = JSON.parse(fromUtf8.decode(await ch.hs.readMessage(body)));
        if (await deviceIdOf(ch.hs.remoteStatic) !== from) { throw new Error('peer key does not match its id'); }
        ch.peerName = intro.name;
      } else if (kind === KIND.transport && ch?.hs.isComplete) {
        const message = JSON.parse(fromUtf8.decode(await ch.hs.receive.decryptWithAd(EMPTY, body)));
        if (typeof message.op === 'string') { serve(ch, message).catch((e) => log.push(`error ${e.message}`)); }
      } else if (kind === KIND.close && ch) {
        channels.delete(k);
      }
    } catch (e) {
      channels.delete(k);
      log.push(`channel failed: ${e.message}`);
    }
  };
  relay.onPresence = (list) => {
    // A peer that left cannot use its channels again; it opens new ones.
    const online = new Set(list.map((d) => d.id));
    for (const [k, ch] of channels) { if (!online.has(ch.peer)) { channels.delete(k); } }
  };

  const subscribers = () => [...channels.values()].filter((c) => c.subscribed && c.hs.isComplete);
  function upsert(s) {
    state.set(s.id, s);
    revision++;
    return Promise.all(subscribers().map((ch) => event(ch, 'session.upsert', listed(s))));
  }
  return {
    deviceId,
    name,
    log,
    answers,
    typed,
    push,
    // The next answers' outcomes: 'delivered', 'unconfirmed', 'stale', 'busy', 'refused',
    // or { outcome, reason, holdMs }.
    scriptAnswers(...steps) { script.push(...steps); },
    replies,
    typedReplies,
    // The next replies' outcomes, the same way.
    scriptReplies(...steps) { replyScript.push(...steps); },
    get openChannels() { return subscribers().length; },
    upsert,
    remove(id) {
      state.delete(id);
      revision++;
      return Promise.all(subscribers().map((ch) => event(ch, 'session.remove', { id })));
    },
    // A new usage report, sent as usage.update to every channel subscribed to usage.
    setUsage(report) {
      usage = report;
      return Promise.all([...channels.values()].filter((c) => c.usage && c.hs.isComplete).map((ch) => event(ch, 'usage.update', report)));
    },
    close() { relay.close(); },
  };
}
