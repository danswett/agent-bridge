// Base64url without padding and lowercase hex, strict both ways as the C# library's Codec: a value
// has exactly one accepted spelling.
import { RosterFormatError, REFUSAL } from './refusal.js';

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
const INDEX = new Map([...ALPHABET].map((c, i) => [c, i]));

export function base64Url(bytes) {
  let out = '';
  for (let i = 0; i < bytes.length; i += 3) {
    const n = (bytes[i] << 16) | ((bytes[i + 1] ?? 0) << 8) | (bytes[i + 2] ?? 0);
    const chars = bytes.length - i >= 3 ? 4 : bytes.length - i + 1;
    for (let j = 0; j < chars; j++) out += ALPHABET[(n >> (18 - 6 * j)) & 63];
  }
  return out;
}

// Null for padding, a character outside the URL-safe alphabet, a length no encoding has, or
// non-zero unused trailing bits.
export function tryBase64Url(text) {
  if (typeof text !== 'string' || text.length % 4 === 1) return null;
  const bytes = new Uint8Array(Math.floor((text.length * 3) / 4));
  let bits = 0;
  let acc = 0;
  let at = 0;
  for (const c of text) {
    const v = INDEX.get(c);
    if (v === undefined) return null;
    acc = (acc << 6) | v;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      bytes[at++] = (acc >> bits) & 0xff;
    }
    acc &= (1 << bits) - 1;
  }
  if (acc !== 0) return null;
  return bytes;
}

export function fromBase64Url(text, what, length) {
  const bytes = tryBase64Url(text);
  if (!bytes) throw new RosterFormatError(REFUSAL.base64, what);
  if (length !== undefined && bytes.length !== length) throw new RosterFormatError(REFUSAL.length, what);
  return bytes;
}

export const hex = (bytes) => Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');

export function fromHex(text) {
  const bytes = new Uint8Array(text.length / 2);
  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(text.slice(2 * i, 2 * i + 2), 16);
  return bytes;
}

// 32 lowercase hex characters: a device id or a generation.
export const isId = (text) => typeof text === 'string' && /^[0-9a-f]{32}$/.test(text);

export function equal(a, b) {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

// Head order: ascending by raw bytes, compared byte by byte.
export function compare(a, b) {
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) if (a[i] !== b[i]) return a[i] - b[i];
  return a.length - b.length;
}
