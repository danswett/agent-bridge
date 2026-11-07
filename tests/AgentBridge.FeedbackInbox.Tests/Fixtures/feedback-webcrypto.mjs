import { webcrypto } from 'node:crypto';

let input = '';
for await (const chunk of process.stdin) input += chunk;
const fixture = JSON.parse(input);
const crypto = webcrypto.subtle;
const label = Buffer.from('slipgates feedback envelope v1');

if (fixture.mode === 'seal') {
  const keyId = Buffer.from(fixture.keyId, 'ascii');
  const id = Buffer.from(fixture.bundleId, 'base64');
  const recipient = await crypto.importKey('raw', Buffer.from(fixture.publicKey, 'base64'), 'X25519', false, []);
  const ephemeral = await crypto.generateKey('X25519', false, ['deriveBits']);
  const ephemeralPublic = Buffer.from(await crypto.exportKey('raw', ephemeral.publicKey));
  const shared = await crypto.deriveBits({ name: 'X25519', public: recipient }, ephemeral.privateKey, 256);
  const material = await crypto.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const salt = webcrypto.getRandomValues(new Uint8Array(32));
  const nonce = webcrypto.getRandomValues(new Uint8Array(12));
  const context = Buffer.concat([label, keyId, id]);
  const aes = await crypto.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt, info: context }, material, { name: 'AES-GCM', length: 256 }, false, ['encrypt']);
  const ciphertext = Buffer.from(await crypto.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: context, tagLength: 128 }, aes, Buffer.from(fixture.plaintext, 'base64')));
  const length = Buffer.alloc(4);
  length.writeUInt32BE(ciphertext.length);
  const header = Buffer.concat([Buffer.from('SGBF'), Buffer.from([1, keyId.length]), keyId, id, ephemeralPublic, salt, nonce, length]);
  const result = Buffer.alloc(4096);
  header.copy(result);
  ciphertext.copy(result, header.length);
  process.stdout.write(result.toString('base64'));
} else if (fixture.mode === 'open') {
  const stored = Buffer.from(fixture.envelope, 'base64');
  const pkcs8 = Buffer.concat([Buffer.from('302e020100300506032b656e04220420', 'hex'), Buffer.from(fixture.privateKey, 'base64')]);
  const recipient = await crypto.importKey('pkcs8', pkcs8, 'X25519', false, ['deriveBits']);
  let at = 6;
  const keyId = stored.subarray(at, at += stored[5]);
  const id = stored.subarray(at, at += 16);
  const ephemeral = await crypto.importKey('raw', stored.subarray(at, at += 32), 'X25519', false, []);
  const salt = stored.subarray(at, at += 32);
  const nonce = stored.subarray(at, at += 12);
  const length = stored.readUInt32BE(at); at += 4;
  const shared = await crypto.deriveBits({ name: 'X25519', public: ephemeral }, recipient, 256);
  const material = await crypto.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const context = Buffer.concat([label, keyId, id]);
  const aes = await crypto.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt, info: context }, material, { name: 'AES-GCM', length: 256 }, false, ['decrypt']);
  const plaintext = await crypto.decrypt({ name: 'AES-GCM', iv: nonce, additionalData: context, tagLength: 128 }, aes, stored.subarray(at, at + length));
  process.stdout.write(Buffer.from(plaintext).toString('base64'));
} else {
  throw new Error('Unknown synthetic fixture mode.');
}
