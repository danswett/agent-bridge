// Every hash, MAC and signature input of the roster protocol: F(x₁, …, xₙ), each part preceded
// by its length as a 4-byte big-endian unsigned integer (docs/protocol/devices.md, "Framing").
export const LABEL = Object.freeze({
  entry: 'agent-bridge roster entry v1',
  cosign: 'agent-bridge roster cosign v1',
  presence: 'agent-bridge presence v1',
  enrollCommit: 'agent-bridge enroll commit v1',
  enroll: 'agent-bridge enroll v1',
  enrollCode1: 'agent-bridge enroll code1 v1',
  enrollCode2: 'agent-bridge enroll code2 v1',
  enrollKey: 'agent-bridge enroll key v1',
  inviteHint: 'agent-bridge invite hint v1',
  qrJoiner: 'agent-bridge enroll qr joiner v1',
  qrApprover: 'agent-bridge enroll qr approver v1',
  head: 'agent-bridge roster head v1',
  personCommand: 'agent-bridge person command v1',
  veto: 'agent-bridge reset veto v1',
});

const encoder = new TextEncoder();

export const utf8 = (text) => encoder.encode(text);

export function frame(...parts) {
  const out = new Uint8Array(parts.reduce((n, p) => n + 4 + p.length, 0));
  const view = new DataView(out.buffer);
  let at = 0;
  for (const p of parts) {
    view.setUint32(at, p.length);
    out.set(p, at + 4);
    at += 4 + p.length;
  }
  return out;
}

export const sha256 = async (bytes) => new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));

export async function hmacSha256(key, bytes) {
  const k = await crypto.subtle.importKey('raw', key, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await crypto.subtle.sign('HMAC', k, bytes));
}
