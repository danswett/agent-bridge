import { hostJsonBytes, wellFormed } from './answers.js';
import { isProtocolTime } from './protocol-time.js';

const object = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const int32 = (v) => Number.isInteger(v) && v >= -2147483648 && v <= 2147483647;
const int64 = (v) => Number.isInteger(v) && v >= -(2 ** 63) && v < 2 ** 63;
const asciiKey = (name) => name.replace(/[A-Z]/g, (c) => c.toLowerCase());
const members = (raw) => object(raw)
  ? new Map(Object.entries(raw).map(([name, value]) => [asciiKey(name), value])) : null;
const field = (raw, name) => raw.get(asciiKey(name));
const fields = (raw, shape) => raw
  ? Object.fromEntries(Object.entries(shape).filter(([k, fits]) => fits(field(raw, k))).map(([k]) => [k, field(raw, k)])) : null;
const enumOf = (v, known, fallback) => known.includes(v) ? v : fallback;
const stateOf = (v, session = false) => enumOf(v, session ? ['fresh', 'partial'] : ['fresh', 'partial', 'stale'], 'unavailable');

export const readMachineLoadCpu = (raw) => fields(members(raw), { percent: Number.isFinite, cores: int32 });
export const readMachineLoadMemory = (raw) => fields(members(raw), { totalBytes: int64, availableBytes: int64, committedPercent: Number.isFinite });
export const readUnattributedLoad = (raw) => fields(members(raw), { processes: int32, workingSetBytes: int64 });

export function readSessionLoad(raw) {
  const data = members(raw), session = data && field(data, 'session');
  if (typeof session !== 'string' || wellFormed(session) !== session) { return null; }
  const read = { session, agent: enumOf(field(data, 'agent'), ['agency', 'direct', 'copilot', 'claude', 'codex'], 'other'),
    state: stateOf(field(data, 'state'), true), ...fields(data, { processes: int32, cpuPercent: Number.isFinite, workingSetBytes: int64, handles: int32 }) };
  const handleKind = field(data, 'handleKind');
  if (typeof handleKind === 'string') { read.handleKind = enumOf(handleKind, ['handles', 'fds'], 'other'); }
  return read;
}

// Complete record reading is separate from the feed's guarded bars-only projection.
export function readMachineLoad(raw) {
  const data = members(raw);
  if (!data) { return null; }
  const givenSessions = field(data, 'sessions');
  let sessions = Array.isArray(givenSessions) ? givenSessions.map(readSessionLoad).filter(Boolean) : [];
  let state = stateOf(field(data, 'state'));
  if (sessions.length > 200) {
    sessions = [...sessions].sort((a, b) => (b.workingSetBytes ?? -Infinity) - (a.workingSetBytes ?? -Infinity)).slice(0, 200);
    if (state === 'fresh') { state = 'partial'; }
  }
  const read = { state, sessions, ...fields(data, { ageMs: int64, staleAfterMs: int64 }) };
  const sampledAt = field(data, 'sampledAt');
  if (isProtocolTime(sampledAt)) {
    const time = new Date(sampledAt), year = time.getUTCFullYear();
    if (year >= 1 && year <= 9999) { read.sampledAt = time.toISOString(); }
  }
  for (const [key, decode] of [['cpu', readMachineLoadCpu], ['memory', readMachineLoadMemory], ['unattributed', readUnattributedLoad]]) {
    const value = decode(field(data, key));
    if (value !== null) { read[key] = value; }
  }
  return read;
}

const nonnegative = (n) => Number.isFinite(n) && n >= 0;
const numbers = (raw, keys) => Object.fromEntries(keys.filter((k) => nonnegative(raw?.[k])).map((k) => [k, raw[k]]));
function readLoad(raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)
    || !['fresh', 'partial', 'stale', 'unavailable'].includes(raw.state)
    || !nonnegative(raw.ageMs) || !nonnegative(raw.staleAfterMs) || hostJsonBytes(raw) > 65536) {
    console.error('Ignoring malformed or oversized machine load');
    return null;
  }
  const { state, ageMs, staleAfterMs } = raw;
  const cpu = numbers(raw.cpu, ['percent']), memory = numbers(raw.memory, ['totalBytes', 'availableBytes']);
  if (cpu.percent > 100) { delete cpu.percent; }
  return { state, ageMs, staleAfterMs, ...(Object.keys(cpu).length ? { cpu } : {}),
    ...(Object.keys(memory).length ? { memory } : {}) };
}

// Attach to a machine's existing HostClient after hello; wiring belongs to the tile slice.
export class LoadFeed {
  #machines = new Map();
  constructor({ now = () => performance.now(), onChange = () => {} } = {}) { Object.assign(this, { now, onChange }); }
  load(machineId) { return this.#machines.get(machineId)?.record ?? null; }

  attach(machineId, client) {
    const previous = this.#machines.get(machineId);
    if (previous?.client === client && client.capabilities.includes('load')) { return previous.pending; }
    const detached = this.detach(machineId);
    if (client.closed || !client.capabilities.includes('load')) { return detached; }
    void detached.catch((error) => console.error(error));
    const entry = { client, record: null, revision: 0, lastUpdate: -Infinity, off: [], subscribing: false };
    this.#machines.set(machineId, entry);
    const current = () => this.#machines.get(machineId) === entry;
    const accept = (raw, update = false) => {
      if (!current()) { return; }
      const record = readLoad(raw), now = this.now();
      if (!record || (update && now - entry.lastUpdate < 1000)) { return; }
      if (update) { entry.lastUpdate = now; }
      entry.record = record;
      entry.revision++;
      this.onChange(machineId);
    };
    entry.off.push(client.on('event', (m) => { if (m.ev === 'load.update') { accept(m.data, true); } }),
      client.on('close', () => { void this.detach(machineId); }));
    const ask = async (op) => {
      const revision = entry.revision;
      const raw = await client.request(op);
      if (revision === entry.revision) { accept(raw); }
    };
    entry.pending = Promise.resolve().then(async () => {
      if (!current()) { return; }
      await ask('load.get');
      if (!current()) { return; }
      entry.subscribing = true;
      await ask('load.subscribe');
    }).catch((error) => {
      if (!current()) { return; }
      entry.record = null;
      this.onChange(machineId);
      throw error;
    });
    return entry.pending;
  }

  async detach(machineId) {
    const entry = this.#machines.get(machineId);
    if (!entry) { return; }
    this.#machines.delete(machineId);
    for (const off of entry.off) { off(); }
    this.onChange(machineId);
    if (entry.subscribing && !entry.client.closed) { await entry.client.request('load.unsubscribe'); }
  }
}
