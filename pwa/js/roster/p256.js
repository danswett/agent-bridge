// P-256 for roster keys and signatures, as the C# library's P256: uncompressed SEC1 points checked
// on the curve with this code's own arithmetic, and signatures as strict minimal DER.
import { RosterFormatError, REFUSAL } from './refusal.js';
import { fromBase64Url } from './codec.js';

export const POINT_BYTES = 65;

const P = 0xffffffff00000001000000000000000000000000ffffffffffffffffffffffffn;
const B = 0x5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604bn;
const N = 0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551n;
const HALF_N = N >> 1n;

const mod = (a, m) => ((a % m) + m) % m;

function unsigned(bytes) {
  let v = 0n;
  for (const b of bytes) v = (v << 8n) | BigInt(b);
  return v;
}

function bytes32(v) {
  const out = new Uint8Array(32);
  for (let i = 31; i >= 0; i--) { out[i] = Number(v & 0xffn); v >>= 8n; }
  return out;
}

export function isPoint(point) {
  if (point.length !== POINT_BYTES || point[0] !== 0x04) return false;
  const x = unsigned(point.subarray(1, 33));
  const y = unsigned(point.subarray(33, 65));
  if (x >= P || y >= P) return false;
  return mod(y * y, P) === mod(x * x * x - 3n * x + B, P);
}

export function point(text, what) {
  const bytes = fromBase64Url(text, what, POINT_BYTES);
  if (!isPoint(bytes)) throw new RosterFormatError(REFUSAL.point, what);
  return bytes;
}

// r and s from strict minimal DER, each in [1, n); null for anything else.
export function readDer(der) {
  if (der.length < 8 || der[0] !== 0x30 || der[1] >= 0x80 || der[1] !== der.length - 2) return null;
  let at = 2;
  const int = () => {
    if (at + 2 > der.length || der[at] !== 0x02) return null;
    const length = der[at + 1];
    if (length === 0 || length >= 0x80 || at + 2 + length > der.length) return null;
    const content = der.subarray(at + 2, at + 2 + length);
    if ((content[0] & 0x80) !== 0 || (length > 1 && content[0] === 0 && (content[1] & 0x80) === 0)) return null;
    at += 2 + length;
    return unsigned(content);
  };
  const r = int();
  if (r === null) return null;
  const s = int();
  if (s === null || at !== der.length) return null;
  if (r === 0n || s === 0n || r >= N || s >= N) return null;
  return { r, s };
}

export const isLowS = (s) => s <= HALF_N;

export function checkRosterSignature(der, what) {
  const rs = readDer(der);
  if (!rs) throw new RosterFormatError(REFUSAL.der, what);
  if (!isLowS(rs.s)) throw new RosterFormatError(REFUSAL.highS, what);
}

export function checkAssertionSignature(der, what) {
  if (!readDer(der)) throw new RosterFormatError(REFUSAL.der, what);
}

function integer(v) {
  let hex = v.toString(16);
  if (hex.length % 2) hex = `0${hex}`;
  const bytes = hex.match(/../g).map((h) => parseInt(h, 16));
  return bytes[0] & 0x80 ? [0, ...bytes] : bytes;
}

export function der(r, s) {
  const rb = integer(r);
  const sb = integer(s);
  return Uint8Array.from([0x30, 4 + rb.length + sb.length, 0x02, rb.length, ...rb, 0x02, sb.length, ...sb]);
}

// WebCrypto's raw r ‖ s as the minimal low-S DER a roster signature is: what this client sends.
export function fromRaw(raw) {
  if (raw.length !== 64) throw new Error('a raw P-256 signature is 64 bytes');
  const r = unsigned(raw.subarray(0, 32));
  const s = unsigned(raw.subarray(32));
  if (r === 0n || s === 0n || r >= N || s >= N) throw new Error('r or s out of range');
  return der(r, isLowS(s) ? s : N - s);
}

// ECDSA with SHA-256 under a point already checked by isPoint, over DER already checked.
export async function verify(publicPoint, message, signature) {
  const rs = readDer(signature);
  if (!rs) return false;
  const key = await crypto.subtle.importKey('raw', publicPoint, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
  const raw = new Uint8Array(64);
  raw.set(bytes32(rs.r), 0);
  raw.set(bytes32(rs.s), 32);
  return crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, raw, message);
}
