// A minimal Bridge Host for the end-to-end tests, in Node: it signs in through the relay
// like any device, accepts Noise channels as the responder, and answers the sessions
// protocol (hello, sessions.list, session.get, sessions.subscribe) with events. It reuses
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

const EMPTY = new Uint8Array(0);

export async function startFixtureHost({ base, name = 'fixture-host', sessions = [] }) {
  const key = await P256KeyPair.generate();
  const deviceId = await deviceIdOf(key.public);
  const account = await (await startDeviceFlow(base)).wait();
  const relay = new Relay({ base, key, deviceId, name, kind: 'host' });
  await relay.connect(account.token);

  const state = new Map(sessions.map((s) => [s.id, s]));
  const channels = new Map();
  const log = [];
  let revision = 1;

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
        return reply({ protocol: 1, machine: { id: deviceId, name, os: 'linux', hostVersion: '0.0.0-fixture' }, capabilities: ['sessions.read'] });
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
      default:
        return sendJson(ch, { re: id, ok: false, error: `unknown op ${op}` });
    }
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
  return {
    deviceId,
    name,
    log,
    get openChannels() { return subscribers().length; },
    upsert(s) {
      state.set(s.id, s);
      revision++;
      return Promise.all(subscribers().map((ch) => event(ch, 'session.upsert', listed(s))));
    },
    remove(id) {
      state.delete(id);
      revision++;
      return Promise.all(subscribers().map((ch) => event(ch, 'session.remove', { id })));
    },
    close() { relay.close(); },
  };
}
