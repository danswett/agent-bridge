// Bringing a session that ended on one machine to another: docs/protocol/v1-transfer.md.
// This device signs a command for the machine the session is on, naming it, the session and the
// machine that receives it, and sends session.launch with it to that machine, which carries it.
// Mirrors src/AgentBridge.Host/SignedCommands.cs: the device key is an ECDH key WebCrypto will not
// let sign, so the signature is a MAC under a key from ECDH between this device and the machine
// it is for, which only those two can make.
//
//   key = HKDF-SHA256(ikm: ECDH(this device, machine), salt: "agent-bridge command v1",
//                     info: "<this device id>|<machine device id>", 32 bytes)
//   mac = HMAC-SHA256(key, the body's exact UTF-8 bytes)
import { utf8 } from './bytes.js';
import { toBase64url } from './blobs.js';
import { deviceIdOf } from './identity.js';

export const TRANSFER = Object.freeze({
  capability: 'sessions.transfer',
  serve: 'sessions.transfer.serve',
  action: 'session.transfer',
});

// How long a command may be carried out for: the host takes at most ten minutes.
export const COMMAND_LIFETIME_S = 5 * 60;

const subtle = () => globalThis.crypto.subtle;

// The MAC key shared by this device (key, a P256KeyPair) and a machine (its public key).
export async function commandKey(key, machinePublic, signerId, machineId) {
  const z = await key.dh(machinePublic);
  const ikm = await subtle().importKey('raw', z, 'HKDF', false, ['deriveBits']);
  const bits = await subtle().deriveBits({ name: 'HKDF', hash: 'SHA-256', salt: utf8.encode('agent-bridge command v1'), info: utf8.encode(`${signerId}|${machineId}`) }, ikm, 256);
  return new Uint8Array(bits);
}

export async function macOf(keyBytes, bodyBytes) {
  const k = await subtle().importKey('raw', keyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await subtle().sign('HMAC', k, bodyBytes));
}

// A signed session.transfer command: { body, by, mac }, each base64url.
export async function signTransfer({ key, deviceId, machinePublic, session, to, now = Date.now(), request = globalThis.crypto.randomUUID() }) {
  const machine = await deviceIdOf(machinePublic);
  const issuedAt = Math.floor(now / 1000);
  const body = utf8.encode(JSON.stringify({
    v: 1,
    action: TRANSFER.action,
    request,
    principal: { kind: 'user', device: deviceId },
    target: { machine, session },
    to,
    issuedAt,
    expiresAt: issuedAt + COMMAND_LIFETIME_S,
  }));
  const mac = await macOf(await commandKey(key, machinePublic, deviceId, machine), body);
  return { body: toBase64url(body), by: toBase64url(key.public), mac: toBase64url(mac) };
}

// The sessions the account's other machines offer for a transfer to target, each with its
// machine's static key, which the command is signed for: [{ hostId, machineName, key,
// resumable }]. One machine at a time, each over a short channel, as fleet.js looks at them; a
// machine that does not answer, or does not hand its sessions on, contributes nothing.
export async function collectRemote({ connection, targetId, me, makeClient, timeoutMs = 10000 }) {
  const out = [];
  for (const d of connection.presence ?? []) {
    if (d.kind !== 'host' || d.id === me || d.id === targetId) { continue; }
    let channel = null;
    let client = null;
    try {
      channel = await connection.openChannel(d.id, { timeoutMs });
      client = makeClient(channel);
      const hello = await client.hello();
      if (!hello.capabilities.includes(TRANSFER.serve)) { continue; }
      const options = await client.launchOptions();
      const key = channel.hs?.remoteStatic;
      if (!key || options.resumable.length === 0) { continue; }
      out.push({ hostId: d.id, machineName: hello.machine?.name || d.name || 'another machine', key: new Uint8Array(key), resumable: options.resumable });
    } catch {
      // Asleep, offline or older: nothing from it this time.
    } finally {
      if (client) { client.close(); } else { channel?.close(); }
    }
  }
  return out;
}
