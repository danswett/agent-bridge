// The blob store (docs/protocol/blobs.md, design §4.6): files too large for the channel go
// sealed into storage, straight from this device, on a write-once URL the relay issues, and
// the machine fetches, opens and checks them itself. The format is the host's
// (src/AgentBridge.Transport/Blobs.cs) byte for byte: 64 KiB AES-256-GCM records, the nonce
// the record's index, the associated data binding the blob's id, the index and whether the
// record is the last, then zeros to a size bucket. The key, the size and the file's SHA-256
// go only to the machine, inside the end-to-end channel; the relay and storage see this.
//
// Runs in browsers and in Node 22+ (the unit tests).

export const BlobFormat = Object.freeze({ record: 64 * 1024, tag: 16, keyBytes: 32, idBytes: 16, minStored: 4096 });

const MIB = 1024 * 1024;
const LABEL = new TextEncoder().encode('agent-bridge blob v1');

export class BlobIntegrityError extends Error {}

export const records = (size) => (size <= 0 ? 0 : Math.ceil(size / BlobFormat.record));

export const sealedSize = (size) => size + records(size) * BlobFormat.tag;

// Powers of two up to 1 MiB, then whole MiB.
export function bucket(n) {
  if (n <= BlobFormat.minStored) { return BlobFormat.minStored; }
  if (n <= MIB) { let p = BlobFormat.minStored; while (p < n) { p *= 2; } return p; }
  return Math.ceil(n / MIB) * MIB;
}

export const storedSize = (size) => bucket(sealedSize(size));

// The largest file whose stored form fits in maxStored bytes.
export function maxPlain(maxStored) {
  if (!(maxStored >= BlobFormat.minStored)) { return 0; }
  let lo = 0, hi = maxStored;
  while (lo < hi) {
    const mid = lo + Math.ceil((hi - lo) / 2);
    if (storedSize(mid) <= maxStored) { lo = mid; } else { hi = mid - 1; }
  }
  return lo;
}

export function fromBase64url(s) {
  if (typeof s !== 'string' || !/^[A-Za-z0-9_-]*$/.test(s)) { return null; }
  const std = s.replace(/-/g, '+').replace(/_/g, '/');
  try { return Uint8Array.from(atob(std + '='.repeat((4 - (std.length % 4)) % 4)), (c) => c.charCodeAt(0)); } catch { return null; }
}

export function toBase64url(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) { s += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); }
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// The 16 bytes that start a relay's ticket: the blob's id, which every record binds.
export function blobIdOf(ticket) {
  if (typeof ticket !== 'string' || ticket.indexOf('.') !== 22) { return null; }
  const id = fromBase64url(ticket.slice(0, 22));
  return id?.length === BlobFormat.idBytes ? id : null;
}

function nonce(i) {
  const n = new Uint8Array(12);
  new DataView(n.buffer).setBigUint64(4, BigInt(i));
  return n;
}

function aad(id, i, final) {
  const a = new Uint8Array(LABEL.length + BlobFormat.idBytes + 9);
  a.set(LABEL);
  a.set(id, LABEL.length);
  new DataView(a.buffer).setBigUint64(LABEL.length + BlobFormat.idBytes, BigInt(i));
  a[a.length - 1] = final ? 1 : 0;
  return a;
}

const importKey = (key, use) => {
  if (key?.length !== BlobFormat.keyBytes) { throw new Error('a blob key is 32 bytes'); }
  return crypto.subtle.importKey('raw', key, 'AES-GCM', false, [use]);
};

export const newBlobKey = () => crypto.getRandomValues(new Uint8Array(BlobFormat.keyBytes));

// A whole file sealed into what is stored, padding included. onProgress(bytes sealed).
export async function sealBlob(bytes, key, id, { onProgress, signal } = {}) {
  if (!bytes?.length) { throw new Error('a blob is at least one byte'); }
  if (id?.length !== BlobFormat.idBytes) { throw new Error('a blob id is 16 bytes'); }
  const k = await importKey(key, 'encrypt');
  const n = records(bytes.length);
  const out = new Uint8Array(storedSize(bytes.length));
  let at = 0;
  for (let i = 0; i < n; i++) {
    signal?.throwIfAborted();
    const chunk = bytes.subarray(i * BlobFormat.record, Math.min(bytes.length, (i + 1) * BlobFormat.record));
    const sealed = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce(i), additionalData: aad(id, i, i === n - 1), tagLength: 128 }, k, chunk));
    out.set(sealed, at);
    at += sealed.length;
    onProgress?.(Math.min(bytes.length, (i + 1) * BlobFormat.record));
  }
  return out;
}

// The file a stored blob holds, or BlobIntegrityError for anything but exactly what was sealed
// for this id and size.
export async function openBlob(stored, size, key, id) {
  if (id?.length !== BlobFormat.idBytes) { throw new Error('a blob id is 16 bytes'); }
  if (stored.length !== storedSize(size)) { throw new BlobIntegrityError('the blob is not the size of its file'); }
  const k = await importKey(key, 'decrypt');
  const n = records(size);
  const out = new Uint8Array(size);
  let at = 0;
  for (let i = 0; i < n; i++) {
    const len = Math.min(BlobFormat.record, size - i * BlobFormat.record);
    const sealed = stored.subarray(at, at + len + BlobFormat.tag);
    let plain;
    try { plain = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: nonce(i), additionalData: aad(id, i, i === n - 1), tagLength: 128 }, k, sealed)); } catch { throw new BlobIntegrityError('a part of the blob was changed, moved or is not from this blob'); }
    out.set(plain, i * BlobFormat.record);
    at += len + BlobFormat.tag;
  }
  if (stored.subarray(at).some((b) => b !== 0)) { throw new BlobIntegrityError('the blob has bytes after its end'); }
  return out;
}

// Whether a URL from the relay (which is not trusted) is one a blob may be sent to: Azure Blob
// Storage over HTTPS, or the relay itself (a dev relay serves blobs). The page's
// Content-Security-Policy narrows it further, to the one storage account the build names.
export function resolveBlobUrl(relayBase, url) {
  let u;
  try { u = new URL(url, relayBase); } catch { return null; }
  const relay = new URL(relayBase);
  if (u.origin === relay.origin) { return u.href; }
  if (u.protocol === 'https:' && u.port === '' && !u.username && !u.password && u.hostname.endsWith('.blob.core.windows.net')) { return u.href; }
  return null;
}

export class UploadError extends Error {
  constructor(status) {
    super(status ? `storage answered ${status} to the upload` : 'the upload failed: no connection to storage');
    this.status = status;
  }
}

// Put Blob with progress, which fetch() cannot report. onProgress(bytes sent). The signal
// aborts it, rejecting with an AbortError.
export function xhrUpload(url, body, { signal, onProgress } = {}) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) { reject(signal.reason); return; }
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', url);
    xhr.setRequestHeader('x-ms-blob-type', 'BlockBlob');
    xhr.setRequestHeader('content-type', 'application/octet-stream');
    xhr.upload.onprogress = (e) => onProgress?.(e.loaded);
    xhr.onload = () => (xhr.status === 201 || xhr.status === 200 ? resolve() : reject(new UploadError(xhr.status)));
    xhr.onerror = () => reject(new UploadError(0));
    xhr.onabort = () => reject(signal?.reason ?? new DOMException('cancelled', 'AbortError'));
    signal?.addEventListener('abort', () => xhr.abort(), { once: true });
    xhr.send(body);
  });
}

// The blob store as this device reaches it, through its relay connection (relay.js).
export class RelayBlobs {
  #relay;
  #upload;

  constructor(relay, { upload = xhrUpload } = {}) {
    this.#relay = relay;
    this.#upload = upload;
  }

  // The largest stored blob the relay takes; 0 without a blob store.
  get max() { return this.#relay?.blobs?.max > 0 ? this.#relay.blobs.max : 0; }

  // { blob, url, expires }: a write-once URL for one blob for the device `to`.
  put(to, size) { return this.#relay.blobPut(to, size); }

  delete(blob) { return this.#relay.blobDelete(blob); }

  upload(url, body, options) {
    const target = resolveBlobUrl(this.#relay.base || globalThis.location?.href, url);
    if (!target) { return Promise.reject(new UploadError(0)); }
    return this.#upload(target, body, options);
  }
}

// What a blob ticket of the relay's lasts, less a margin: one older is uploaded again.
export const TICKET_REUSE_MS = 20 * 60 * 60 * 1000;
