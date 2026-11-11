import { hostJsonBytes } from './answers.js';

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
    entry.pending = detached.then(async () => {
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
