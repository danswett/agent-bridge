import { RosterVerifier } from './roster/verifier.js';
import { parseStrict } from './roster/strict-json.js';
import { RosterFormatError } from './roster/refusal.js';
import { fromBase64Url } from './roster/codec.js';
import { frame, utf8 } from './roster/framing.js';
import { checkRosterSignature, verify } from './roster/p256.js';
import { isUtc } from './roster/entry.js';
import { instant, instantOfMs, compareInstants } from './roster/commands.js';

const recordFields = ['v', 'machine', 'epoch', 'seq', 'at', 'truncated', 'total', 'sessions', 'sig'];
const sessionFields = ['id', 'node', 'name', 'state', 'since', 'startedAt'];
const hex16 = (v) => typeof v === 'string' && /^[0-9a-f]{16}$/.test(v);
const members = (v, allowed) => v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).every((k) => allowed.includes(k));
const canonical = (n, root = false) => {
  if (n.t === 'object') return `{${n.members.filter(([k]) => !root || k !== 'sig').sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)
    .map(([k, v]) => `${JSON.stringify(k)}:${canonical(v)}`).join(',')}}`;
  if (n.t === 'array') return `[${n.items.map((v) => canonical(v)).join(',')}]`;
  if (n.t === 'number') {
    if (!/^-?(0|[1-9]\d*)$/.test(n.raw) || BigInt(n.raw) < -(1n << 63n) || BigInt(n.raw) >= 1n << 63n) throw new RangeError('registry integer');
    return BigInt(n.raw).toString();
  }
  return n.t === 'string' ? JSON.stringify(n.v) : n.t;
};
export const registrySignedBytes = (record) => frame(utf8('agent-bridge fleet registry v1'), utf8(canonical(record, true)));

function schema(r, machine) {
  return members(r, recordFields) && r.v === 1 && r.machine === machine && hex16(r.epoch)
    && typeof r.seq === 'number' && r.seq >= 1 && typeof r.at === 'string' && isUtc(r.at) && typeof r.truncated === 'boolean'
    && typeof r.sig === 'string' && r.sig.length <= 256 && Array.isArray(r.sessions) && r.sessions.length <= 64
    && (r.total === undefined || (r.truncated && Number.isInteger(r.total) && r.total > r.sessions.length && r.total <= 2147483647))
    && r.sessions.every((s) => members(s, sessionFields) && hex16(s.id) && typeof s.node === 'string'
      && s.node.startsWith('agent_bridge_') && hex16(s.node.slice(13)) && ['working', 'waiting', 'idle', 'ending'].includes(s.state)
      && typeof s.since === 'string' && isUtc(s.since)
      && (s.startedAt === undefined || (typeof s.startedAt === 'string' && s.startedAt.length === 20 && isUtc(s.startedAt)))
      && (s.name === undefined || (typeof s.name === 'string' && /^[\x20-\x7e]{1,64}$/.test(s.name))));
}

// C1's reader contract, ported for the PWA. The caller supplies retained, independently pinned
// roster state. No relay frame, setting or reader option switches roster v2 on.
export class FleetRegistryReader {
  #last = new Map();
  #queue = Promise.resolve();
  constructor({ account, generation, roster, now = () => Date.now() }) {
    Object.assign(this, { account, generation, roster, now });
  }

  read(frames) {
    const run = this.#queue.then(() => this.#read(frames));
    this.#queue = run.catch(() => {});
    return run;
  }

  async #read(frames) {
    const accepted = [];
    for (const bytes of frames) {
      try {
        if (bytes.length > 24576) continue;
        const parsed = parseStrict(bytes, 'registry');
        const node = parsed.get?.('record');
        if (!node) continue;
        const text = canonical(node);
        const record = JSON.parse(text);
        const message = JSON.parse(new TextDecoder().decode(bytes));
        if (utf8(text).length > 16384 || !schema(record, message.machine)) continue;
        const roster = this.roster();
        if (!(roster instanceof RosterVerifier) || !roster.acceptHostSign || roster.account !== this.account || roster.generation !== this.generation) continue;
        const add = roster.state.activeAdd(message.machine);
        if (add === null || roster.signerKeyCollides(add)) continue;
        const device = roster.graph.at(add).body.device;
        if (device.kind !== 'host' || !device.hostSign) continue;
        const signature = fromBase64Url(record.sig, 'registry.sig');
        checkRosterSignature(signature, 'registry.sig');
        if (!await verify(device.hostSign, registrySignedBytes(node), signature)) continue;
        // Verification yields to WebCrypto: recheck authority and freshness before committing.
        if (this.roster() !== roster || !roster.acceptHostSign || roster.account !== this.account || roster.generation !== this.generation
          || roster.state.activeAdd(message.machine) !== add || roster.signerKeyCollides(add)) continue;
        const at = instant(record.at);
        const now = instantOfMs(this.now());
        if (compareInstants(at, { ...now, seconds: now.seconds - 60 }) < 0 || compareInstants(at, { ...now, seconds: now.seconds + 60 }) > 0) continue;
        const seq = BigInt(node.get('seq').raw);
        const old = this.#last.get(message.machine);
        if (old && (old.epoch === record.epoch ? seq <= old.seq : compareInstants(at, old.at) <= 0)) continue;
        this.#last.set(message.machine, { epoch: record.epoch, seq, at });
        accepted.push({ machine: message.machine, online: message.online === true, sessions: record.sessions, truncated: record.truncated, verified: true });
      } catch (e) {
        if (!(e instanceof RosterFormatError) && !(e instanceof RangeError)) throw e;
      }
    }
    return accepted;
  }
}
