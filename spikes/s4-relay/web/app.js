// Spike S4 PWA: sign in through the relay, prove this device's key, see the account's
// online devices, and talk to a host over Noise end to end. The relay only ever sees
// routing ids and ciphertext. Wire format: see src/S4.Protocol/Wire.cs.
import { P256KeyPair, XXHandshake, hex, unhex } from './noise-p256.js';
import { RELAY } from './config.js';

const BASE = RELAY || location.origin;

const $ = (id) => document.getElementById(id);
const enc = new TextEncoder(), dec = new TextDecoder();
const subtle = crypto.subtle;
const DATA = 1, ID = 16, HDR = 9;
const KIND = { msg1: 1, msg2: 2, msg3: 3, transport: 4, close: 5 };
const EMPTY = new Uint8Array(0);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const concat = (...parts) => {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
};
function log(line) {
  const el = $('log');
  el.textContent = `${new Date().toLocaleTimeString()} ${line}\n${el.textContent}`.slice(0, 8000);
  console.log(line);
}

// ---- identity: a non-extractable P-256 key kept in IndexedDB ----
function db() {
  return new Promise((resolve, reject) => {
    const r = indexedDB.open('agent-bridge-s4', 1);
    r.onupgradeneeded = () => r.result.createObjectStore('kv');
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}
async function kv(op, key, value) {
  const d = await db();
  return new Promise((resolve, reject) => {
    const t = d.transaction('kv', op === 'get' ? 'readonly' : 'readwrite');
    const r = op === 'get' ? t.objectStore('kv').get(key) : t.objectStore('kv').put(value, key);
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}
async function loadIdentity() {
  let rec = await kv('get', 'device');
  if (!rec) {
    const kp = await P256KeyPair.generate();
    rec = { privateKey: kp.privateKey, public: kp.public };
    await kv('put', 'device', rec);
  }
  return new P256KeyPair(rec.privateKey, new Uint8Array(rec.public));
}
const deviceIdOf = async (pub) => hex(new Uint8Array(await subtle.digest('SHA-256', pub)).slice(0, ID));

// ---- sign-in: GitHub device flow, through the relay ----
async function signIn() {
  $('signin').disabled = true;
  const s = await (await fetch(`${BASE}/v1/auth/device`, { method: 'POST' })).json();
  $('code').textContent = s.userCode;
  $('verify').href = s.verificationUri;
  $('verify').textContent = s.verificationUri.replace(/^https?:\/\//, '');
  $('code-box').hidden = false;
  let interval = s.interval;
  const deadline = Date.now() + s.expiresIn * 1000;
  while (Date.now() < deadline) {
    await sleep(interval * 1000);
    const r = await fetch(`${BASE}/v1/auth/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ deviceCode: s.deviceCode }) });
    const j = await r.json();
    if (r.status === 202) { if (j.interval) { interval = j.interval; } continue; }
    if (!r.ok) { throw new Error(`sign-in failed: ${j.error}`); }
    // Spike storage. The product keeps a short-lived token and refreshes it.
    localStorage.setItem('account', JSON.stringify(j));
    return j;
  }
  throw new Error('the code expired');
}

// ---- relay connection ----
class Relay {
  constructor(key, me) { this.key = key; this.me = me; this.onData = null; this.onPresence = null; this.chain = Promise.resolve(); }

  connect(token, name) {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(`${BASE.replace(/^http/, 'ws')}/v1/ws`);
      this.ws = ws;
      ws.binaryType = 'arraybuffer';
      ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', token, pub: hex(this.key.public), name, kind: 'pwa' }));
      ws.onclose = (e) => { log(`relay closed ${e.code} ${e.reason}`); reject(new Error(`relay closed ${e.code} ${e.reason}`)); this.onClose?.(e); };
      // Messages are handled strictly in order: Noise transport messages must be.
      ws.onmessage = (ev) => { this.chain = this.chain.then(() => this.handle(ev.data, resolve)).catch((e) => log(`error: ${e.message}`)); };
    });
  }

  async handle(data, ready) {
    if (typeof data === 'string') {
      const m = JSON.parse(data);
      if (m.t === 'challenge') {
        const secret = await this.key.dh(unhex(m.eph));
        const k = await subtle.importKey('raw', secret, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
        const mac = new Uint8Array(await subtle.sign('HMAC', k, concat(enc.encode('agent-bridge-s4-relay-proof'), unhex(m.nonce), enc.encode(m.account))));
        this.ws.send(JSON.stringify({ t: 'proof', mac: hex(mac) }));
      } else if (m.t === 'ready') {
        if (m.device !== this.me) { throw new Error('relay assigned a different device id'); }
        ready(this);
      } else if (m.t === 'presence') {
        this.onPresence?.(m.devices);
      } else if (m.t === 'undeliverable') {
        log(`undeliverable to ${m.to.slice(0, 8)}`);
      }
      return;
    }
    const b = new Uint8Array(data);
    if (b.length > 1 + ID && b[0] === DATA) { await this.onData?.(hex(b.subarray(1, 1 + ID)), b.subarray(1 + ID)); }
  }

  send(peerId, payload) { this.ws.send(concat(new Uint8Array([DATA]), unhex(peerId), payload)); }
}

// ---- end-to-end channels ----
class Channel {
  constructor(relay, peerId, id, hs) {
    Object.assign(this, { relay, peerId, id, hs, pending: new Map(), next: 0, sendChain: Promise.resolve() });
    this.ready = new Promise((res, rej) => { this.resolveReady = res; this.rejectReady = rej; });
  }
  frame(kind, body) { this.relay.send(this.peerId, concat(new Uint8Array([kind]), this.id, body)); }
  request(op, data, timeoutMs = 15000) {
    const id = ++this.next;
    const p = new Promise((res, rej) => {
      this.pending.set(id, res);
      setTimeout(() => { if (this.pending.delete(id)) { rej(new Error(`${op} timed out`)); } }, timeoutMs);
    });
    // Encrypt and send in one chain, so frames leave in nonce order.
    this.sendChain = this.sendChain.then(async () => {
      this.frame(KIND.transport, await this.hs.send.encryptWithAd(EMPTY, enc.encode(JSON.stringify({ id, op, data }))));
    });
    return p;
  }
}

const channels = new Map();
let relay, key, me, account;

async function openChannel(peerId) {
  const id = crypto.getRandomValues(new Uint8Array(8));
  const prologue = concat(enc.encode(`agent-bridge-s4|${me}|${peerId}|`), id);
  const ch = new Channel(relay, peerId, id, await XXHandshake.create(true, key, prologue));
  channels.set(`${peerId}:${hex(id)}`, ch);
  ch.frame(KIND.msg1, await ch.hs.writeMessage(EMPTY));
  return ch;
}

async function onData(from, payload) {
  if (payload.length < HDR) { return; }
  const kind = payload[0];
  const ch = channels.get(`${from}:${hex(payload.subarray(1, HDR))}`);
  if (!ch) { return; } // the PWA only initiates
  const body = payload.slice(HDR);
  try {
    if (kind === KIND.msg2) {
      const intro = JSON.parse(dec.decode(await ch.hs.readMessage(body)));
      // Self-certifying ids: the key that answered must hash to the id we addressed.
      if (await deviceIdOf(ch.hs.remoteStatic) !== ch.peerId) { throw new Error("the peer's key does not match its device id"); }
      ch.peerName = intro.name; ch.peerKind = intro.kind;
      ch.frame(KIND.msg3, await ch.hs.writeMessage(enc.encode(JSON.stringify({ name: deviceName(), kind: 'pwa' }))));
      ch.resolveReady();
    } else if (kind === KIND.transport) {
      const m = JSON.parse(dec.decode(await ch.hs.receive.decryptWithAd(EMPTY, body)));
      const res = ch.pending.get(m.re);
      if (res) { ch.pending.delete(m.re); res(m); }
    }
  } catch (e) {
    channels.delete(`${from}:${hex(ch.id)}`);
    ch.rejectReady(e);
    log(`channel failed: ${e.message}`);
  }
}

function deviceName() {
  const ua = navigator.userAgent;
  const platform = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android' : /Mac/.test(ua) ? 'Mac' : /Windows/.test(ua) ? 'Windows' : 'browser';
  return `${platform} PWA`;
}

// ---- UI ----
let current;

function renderDevices(list) {
  const ul = $('devices');
  ul.replaceChildren();
  for (const d of list.filter((x) => x.id !== me)) {
    const li = document.createElement('li');
    li.dataset.deviceName = d.name;
    const label = document.createElement('span');
    label.textContent = `${d.name} · ${d.kind} · ${d.id.slice(0, 8)}`;
    const b = document.createElement('button');
    b.textContent = 'Connect';
    b.onclick = () => connectTo(d).catch((e) => log(`connect failed: ${e.message}`));
    li.append(label, b);
    ul.append(li);
  }
  if (!ul.children.length) { ul.textContent = 'No other devices online.'; }
  $('devices-box').hidden = false;
}

async function connectTo(d) {
  const t0 = performance.now();
  const ch = await openChannel(d.id);
  await Promise.race([ch.ready, sleep(20000).then(() => { throw new Error('handshake timed out'); })]);
  const ms = performance.now() - t0;
  current = ch;
  $('channel').hidden = false;
  $('peer').textContent = `${ch.peerName} (${ch.peerKind})`;
  $('peer-check').className = 'ok';
  $('peer-check').textContent = '✓ its key matches the id you connected to';
  $('hs-info').textContent = `Noise XX handshake ${ms.toFixed(0)} ms · session ${hex(ch.hs.handshakeHash).slice(0, 16)}`;
  $('result').textContent = '';
  $('pings').textContent = '';
  log(`channel to ${ch.peerName} in ${ms.toFixed(0)} ms`);
}

$('snapshot').onclick = async () => {
  const r = await current.request('snapshot');
  $('result').textContent = JSON.stringify(r.result, null, 2);
};
$('echo').onclick = async () => {
  const text = 'S4-PLAINTEXT-MARKER café – 世界 ✓';
  const r = await current.request('echo', { text });
  $('result').textContent = r.result.text === text ? `echoed intact: ${r.result.text}` : `MISMATCH: ${JSON.stringify(r.result)}`;
};
$('ping').onclick = async () => {
  const rtts = [];
  for (let i = 0; i < 20; i++) {
    const t = performance.now();
    await current.request('ping');
    rtts.push(performance.now() - t);
  }
  rtts.sort((a, b) => a - b);
  $('pings').textContent = `20 pings: median ${rtts[10].toFixed(0)} ms, p95 ${rtts[18].toFixed(0)} ms, max ${rtts[19].toFixed(0)} ms`;
};
$('signout').onclick = () => { localStorage.removeItem('account'); location.reload(); };
$('signin').onclick = () => signIn().then(start).catch((e) => { log(e.message); $('signin').disabled = false; });

async function start(acct) {
  account = acct;
  $('signin-box').hidden = true;
  $('account').hidden = false;
  $('login').textContent = account.login;
  $('me').textContent = me.slice(0, 8);
  const r = new Relay(key, me);
  r.onData = onData;
  r.onPresence = renderDevices;
  r.onClose = (e) => {
    // textContent: the close reason comes from the relay, which is not trusted.
    $('relay-state').className = 'bad';
    $('relay-state').textContent = `Disconnected (${e.code} ${e.reason})`;
    if (e.code === 4001) { localStorage.removeItem('account'); }
  };
  relay = await r.connect(account.token, deviceName());
  $('relay-state').className = 'ok';
  $('relay-state').textContent = 'Connected to the relay; key proven';
}

(async () => {
  if ('serviceWorker' in navigator) { navigator.serviceWorker.register('sw.js').catch((e) => log(`service worker: ${e.message}`)); }
  key = await loadIdentity();
  me = await deviceIdOf(key.public);
  const saved = localStorage.getItem('account');
  if (saved) { start(JSON.parse(saved)).catch((e) => log(e.message)); }
})();
