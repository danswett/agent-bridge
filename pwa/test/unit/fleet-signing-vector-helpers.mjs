// Test-only adapter for the shared vectors; this is not a trusted registry reader.
import { tryBase64Url } from '../../js/roster/codec.js';
import { frame, utf8 } from '../../js/roster/framing.js';
import { isLowS, isPoint, readDer, verify } from '../../js/roster/p256.js';

export function canonicalRecord(record) {
  function encode(value, root = false) {
    if (Array.isArray(value)) return `[${value.map((item) => encode(item)).join(',')}]`;
    if (value !== null && typeof value === 'object') {
      return `{${Object.keys(value).filter((key) => !root || key !== 'sig').sort()
        .map((key) => `${JSON.stringify(key)}:${encode(value[key])}`).join(',')}}`;
    }
    if (value === null || typeof value === 'string' || typeof value === 'boolean' || Number.isSafeInteger(value)) {
      return JSON.stringify(value);
    }
    throw new Error('unsupported registry vector value');
  }
  if (!record || typeof record !== 'object' || Array.isArray(record)) throw new Error('bad registry vector');
  return encode(record, true);
}

export const signedBytes = (record, context) => frame(utf8(context), utf8(canonicalRecord(record)));

export async function verifyRecord(record, hostSign, context) {
  const point = tryBase64Url(hostSign);
  const signature = tryBase64Url(record.sig);
  if (!point || !isPoint(point) || !signature) return false;
  const rs = readDer(signature);
  if (!rs || !isLowS(rs.s)) return false;
  return verify(point, signedBytes(record, context), signature);
}
