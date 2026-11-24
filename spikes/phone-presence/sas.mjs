export async function authenticationString(publicKey, challenge) {
  const bytes = publicKey instanceof ArrayBuffer ? new Uint8Array(publicKey) : publicKey;
  if (!(bytes instanceof Uint8Array) || bytes.length < 64 || bytes.length > 512) throw new Error('Refused: invalid enrollment public key.');
  if (!(challenge instanceof Uint8Array) || challenge.length !== 32) throw new Error('Refused: invalid enrollment challenge.');
  const key = await crypto.subtle.importKey('spki', bytes, { name: 'ECDSA', namedCurve: 'P-256' }, true, ['verify']);
  const canonical = new Uint8Array(await crypto.subtle.exportKey('spki', key));
  const input = new Uint8Array(canonical.length + 32);
  input.set(canonical); input.set(challenge, canonical.length);
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', input));
  return Array.from(hash.subarray(0, 8), v => v.toString(16).padStart(2, '0')).join('').match(/.{4}/g).join(' ');
}
