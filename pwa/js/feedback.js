import { RelayBlobs, bucket, toBase64url } from './blobs.js';

const LABEL = new TextEncoder().encode('slipgates feedback envelope v1');
const MAGIC = new TextEncoder().encode('SGBF');
const EVENT_NAMES = new Set([
  'connection.connecting', 'connection.connected', 'connection.reconnecting', 'connection.resting',
  'connection.replaced', 'connection.signed-out', 'connection.stopped',
  'host.sessions.upsert', 'host.sessions.remove', 'host.sessions.synced',
  'host.sessions.held', 'host.sessions.signin', 'host.sessions.clear',
]);
const LOG_CODES = new Set(['relay-message-error', 'relay-undeliverable', 'transport-error']);
const KEY_ID = /^[A-Za-z0-9._-]{1,64}$/;
const MAX_EVENTS = 100;
const MAX_LOGS = 100;
const MAX_NOTE = 1000;

const concat = (...parts) => {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const part of parts) { out.set(part, at); at += part.length; }
  return out;
};

function decodeBase64url(value, expectedBytes) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value)) { throw new Error('the feedback public key is not base64url'); }
  const binary = atob(value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '='));
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
  if (bytes.length !== expectedBytes || toBase64url(bytes) !== value) { throw new Error(`the feedback value must be a canonical ${expectedBytes}-byte base64url value`); }
  return bytes;
}

function feedbackKey({ keyId, publicKey }) {
  if (!KEY_ID.test(keyId ?? '')) { throw new Error('feedback uploads are not configured with a valid key id'); }
  return { keyIdBytes: new TextEncoder().encode(keyId), publicBytes: decodeBase64url(publicKey, 32) };
}

export function isSensitiveFeedbackText(value) {
  if (typeof value !== 'string') { return false; }
  return /\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16})\b/i.test(value)
    || /\bBearer\s+[A-Za-z0-9._~+/-]{8,}=?/i.test(value)
    || /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/.test(value)
    || /\b(?:password|passwd|secret|token|api[_ -]?key|authorization)\s*[:=]\s*["']?\S+/i.test(value);
}

function browserVersion(userAgent = globalThis.navigator?.userAgent ?? '') {
  const candidates = [
    ['Edge', /\bEdg\/(\d+)/],
    ['Firefox', /\bFirefox\/(\d+)/],
    ['Chrome', /\bChrome\/(\d+)/],
    ['Safari', /\bVersion\/(\d+).*Safari/],
  ];
  for (const [name, pattern] of candidates) {
    const version = userAgent.match(pattern)?.[1];
    if (version) { return `${name} ${version}`; }
  }
  return 'Unknown';
}

function platformName(platform = globalThis.navigator?.userAgentData?.platform ?? globalThis.navigator?.platform ?? '') {
  if (/win/i.test(platform)) { return 'Windows'; }
  if (/mac|ios|iphone|ipad/i.test(platform)) { return 'Apple'; }
  if (/android/i.test(platform)) { return 'Android'; }
  if (/linux/i.test(platform)) { return 'Linux'; }
  return 'Unknown';
}

function safeHostVersions(versions) {
  return [...new Set((Array.isArray(versions) ? versions : [])
    .filter((v) => typeof v === 'string' && /^[A-Za-z0-9.+-]{1,64}$/.test(v)))].sort().slice(0, 20);
}

export class FeedbackDiagnostics {
  #events = [];
  #logs = [];
  #now;

  constructor({ now = () => Date.now() } = {}) { this.#now = now; }

  #timestamp() { return new Date(this.#now()).toISOString(); }

  recordEvent(name) {
    if (!EVENT_NAMES.has(name)) { return; }
    this.#events.push({ at: this.#timestamp(), name });
    if (this.#events.length > MAX_EVENTS) { this.#events.shift(); }
  }

  recordLog(code) {
    if (!LOG_CODES.has(code)) { return; }
    this.#logs.push({ at: this.#timestamp(), code });
    if (this.#logs.length > MAX_LOGS) { this.#logs.shift(); }
  }

  snapshot({ appVersion, protocolVersion, hostVersions = [], note = '' } = {}) {
    if (typeof note !== 'string' || note.length > MAX_NOTE) { throw new Error(`feedback text must be at most ${MAX_NOTE} characters`); }
    const bundle = {
      schema: 'slipgates-feedback.v1',
      bundleId: toBase64url(crypto.getRandomValues(new Uint8Array(16))),
      createdAt: this.#timestamp(),
      platform: platformName(),
      versions: {
        app: typeof appVersion === 'string' && appVersion.length <= 128 ? appVersion : 'Unknown',
        protocol: Number.isSafeInteger(protocolVersion) ? protocolVersion : null,
        browser: browserVersion(),
        hosts: safeHostVersions(hostVersions),
      },
      events: this.#events.slice(),
      logs: this.#logs.slice(),
    };
    if (note.trim()) { bundle.note = note; }
    return bundle;
  }
}

export function serializeFeedbackBundle(bundle) {
  return JSON.stringify(bundle, null, 2);
}

// Seals the exact preview bytes. The envelope header is documented in docs/protocol/feedback-envelope.md.
export async function sealFeedbackEnvelope(plain, { keyId, publicKey, bundleId }) {
  if (!(plain instanceof Uint8Array) || plain.length === 0) { throw new Error('a feedback bundle must not be empty'); }
  const { keyIdBytes, publicBytes } = feedbackKey({ keyId, publicKey });
  const id = decodeBase64url(bundleId, 16);
  const recipient = await crypto.subtle.importKey('raw', publicBytes, { name: 'X25519' }, false, []);
  const ephemeral = await crypto.subtle.generateKey({ name: 'X25519' }, false, ['deriveBits']);
  const ephemeralPublic = new Uint8Array(await crypto.subtle.exportKey('raw', ephemeral.publicKey));
  const shared = new Uint8Array(await crypto.subtle.deriveBits({ name: 'X25519', public: recipient }, ephemeral.privateKey, 256));
  if (shared.every((b) => b === 0)) { throw new Error('the feedback public key is invalid'); }
  const material = await crypto.subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  shared.fill(0);
  const salt = crypto.getRandomValues(new Uint8Array(32));
  const nonce = crypto.getRandomValues(new Uint8Array(12));
  const context = concat(LABEL, keyIdBytes, id);
  const key = await crypto.subtle.deriveKey(
    { name: 'HKDF', hash: 'SHA-256', salt, info: context },
    material,
    { name: 'AES-GCM', length: 256 },
    false,
    ['encrypt'],
  );
  const ciphertext = new Uint8Array(await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv: nonce, additionalData: context, tagLength: 128 },
    key,
    plain,
  ));
  const header = new Uint8Array(4 + 1 + 1 + keyIdBytes.length + 16 + 32 + 32 + 12 + 4);
  let at = 0;
  header.set(MAGIC, at); at += MAGIC.length;
  header[at++] = 1;
  header[at++] = keyIdBytes.length;
  header.set(keyIdBytes, at); at += keyIdBytes.length;
  header.set(id, at); at += id.length;
  header.set(ephemeralPublic, at); at += ephemeralPublic.length;
  header.set(salt, at); at += salt.length;
  header.set(nonce, at); at += nonce.length;
  new DataView(header.buffer).setUint32(at, ciphertext.length, false);
  const stored = new Uint8Array(bucket(header.length + ciphertext.length));
  stored.set(header);
  stored.set(ciphertext, header.length);
  return stored;
}

export async function sendFeedbackBundle({ relay, bundle, keyId, publicKey, upload } = {}) {
  if (!relay?.open) { throw new Error('connect to the relay before sending feedback'); }
  if (relay.feedbackMaxBytes <= 0) { throw new Error('feedback uploads are not enabled on this relay'); }
  if (isSensitiveFeedbackText(bundle?.note)) { throw new Error('remove credential-like text before sending feedback'); }
  const body = await sealFeedbackEnvelope(new TextEncoder().encode(serializeFeedbackBundle(bundle)), {
    keyId, publicKey, bundleId: bundle.bundleId,
  });
  if (body.length > relay.feedbackMaxBytes) { throw new Error('the feedback bundle is larger than this relay allows'); }
  const slot = await relay.feedbackPut(body.length);
  if (typeof slot?.url !== 'string' || !slot.url) { throw new Error('the relay did not return a feedback upload URL'); }
  const put = upload ?? ((url, bytes) => new RelayBlobs(relay).upload(url, bytes));
  await put(slot.url, body);
  return { storedSize: body.length };
}
