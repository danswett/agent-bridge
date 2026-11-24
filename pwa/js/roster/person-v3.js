// K1's writer of a v3 person descriptor and genesis (PersonDescriptorV3 in
// src/AgentBridge.Roster/CapSign.cs): the same members in the same order, byte for byte the C#
// output for the same inputs, each checked by the parser before it is returned. The caller
// supplies capSign's proof as an async function of the binding (the key itself stays in custody).
import { base64Url } from './codec.js';
import { utf8 } from './framing.js';
import { deviceId, parseBody, capSignBinding, SIGN_ALGORITHM } from './entry.js';

// JSON.stringify, with each character beyond the BMP as uppercase surrogate escapes, as the C#
// relaxed encoder writes them. Names and accounts carry no control characters (the parser's rule).
const json = (value) => JSON.stringify(value).replace(/[\u{10000}-\u{10FFFF}]/gu,
  (c) => `\\u${c.charCodeAt(0).toString(16).toUpperCase()}\\u${c.charCodeAt(1).toString(16).toUpperCase()}`);

// The exact device bytes D, refusing as the parser would (for example `device` or `cap-proof`).
export async function writeDescriptorV3({ account, generation, dh, sign, presence, webauthn = null, capSign, proveCapSign, kind, platform, name }) {
  const d = { id: await deviceId(dh), dh: base64Url(dh), sign: base64Url(sign), signAlg: SIGN_ALGORITHM, presence };
  const w = webauthn;
  if (w) d.webauthn = { rpId: w.rpId, credentialId: base64Url(w.credentialId), key: base64Url(w.key), attested: w.attested, backupEligible: w.backupEligible };
  const capSignProof = base64Url(await proveCapSign(capSignBinding(account, generation, dh)));
  Object.assign(d, { capSign: base64Url(capSign), capSignAlg: SIGN_ALGORITHM, capSignProof, kind, platform, name });
  const device = utf8(json(d));
  await genesisV3(account, generation, '1970-01-01T00:00:00Z', device);
  return device;
}

// The genesis body carrying D verbatim, for its own device's `sign` to sign (the caller's job).
export async function genesisV3(account, generation, at, device) {
  const by = JSON.parse(new TextDecoder().decode(device))?.id ?? null;
  const head = json({ v: 3, op: 'genesis', account, generation, seq: 0, by, at });
  const parts = [utf8(`${head.slice(0, -1)},"device":`), device, utf8(',"approval":{"method":"genesis"}}')];
  const body = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  parts.reduce((offset, p) => (body.set(p, offset), offset + p.length), 0);
  await parseBody(body);
  return body;
}
