import { kvGet, kvPut, kvDelete } from './identity.js';
import { accountStore } from './signin.js';

const KEY = 'sent-images';
const GENERATION = `${KEY}-generation`;
const lock = (task, clearing = false) => {
  if (!globalThis.document) { return task(); }
  if (!navigator.locks || typeof BroadcastChannel !== 'function') {
    console.warn('Local image previews need Web Locks and BroadcastChannel; file chips are shown instead.');
    return clearing ? task() : null;
  }
  return navigator.locks.request(KEY, task);
};
const RASTER = /^image\/(png|jpeg|gif|webp)$/;
const MAX = 8 * 1024 * 1024;
const AGE = 24 * 60 * 60 * 1000;
const digest = async (ref) => [...new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(ref)))].map((b) => b.toString(16).padStart(2, '0')).join('');

export class SentImages extends EventTarget {
  #tail = Promise.resolve();
  #epoch = 0;
  constructor({ get = kvGet, put = kvPut, remove = kvDelete, now = Date.now, account = () => { const a = accountStore.load(); return String(a?.accountId ?? a?.login ?? ''); }, serial = lock, channel = globalThis.document && typeof BroadcastChannel === 'function' ? new BroadcastChannel(KEY) : null } = {}) {
    super();
    Object.assign(this, { read: get, write: put, remove, now, scope: account, serial, channel });
    channel?.addEventListener('message', () => { this.#epoch++; this.#changed(); });
  }
  get account() { return this.scope(); }
  get generation() { return this.#epoch; }
  #run(task, clearing = false) {
    this.#tail = this.#tail.then(() => this.serial(task, clearing)).catch(() => { console.warn('Local image previews are unavailable.'); return null; });
    return this.#tail;
  }
  #changed() { this.dispatchEvent(new Event('change')); }
  stamp() { return this.#run(async () => await this.read(GENERATION) ?? ''); }
  async #entries(account) {
    const saved = await this.read(KEY);
    const entries = saved?.account === account && Array.isArray(saved.entries) ? saved.entries : [];
    const fresh = entries.filter((e) => RASTER.test(e.type) && e.bytes instanceof Uint8Array && e.at > this.now() - AGE);
    if (fresh.length !== entries.length) { await this.write(KEY, { account, entries: fresh }); }
    return fresh;
  }
  remember(bindings, files, account = this.account, stamp = undefined) {
    const epoch = this.#epoch;
    return this.#run(async () => {
      if (!account || account !== this.account || epoch !== this.#epoch) { return; }
      if (stamp !== undefined && stamp !== (await this.read(GENERATION) ?? '')) { return; }
      const entries = await this.#entries(account);
      for (const binding of bindings) {
        const file = files.find((f) => f.id === binding.id);
        if (!file || !RASTER.test(file.type) || !(file.bytes instanceof Uint8Array) || file.bytes.length > MAX) { continue; }
        const key = await digest(binding.ref);
        if (!entries.some((e) => e.key === key)) {
          const bitmap = typeof createImageBitmap === 'function' ? await createImageBitmap(new Blob([file.bytes], { type: file.type })) : null;
          entries.push({ key, type: file.type, bytes: file.bytes, at: this.now(), width: bitmap?.width, height: bitmap?.height });
          bitmap?.close();
        }
      }
      entries.sort((a, b) => a.at - b.at);
      let total = entries.reduce((n, e) => n + e.bytes.length, 0);
      while (entries.length > 32 || total > MAX) { total -= entries.shift().bytes.length; }
      if (account !== this.account || epoch !== this.#epoch) { return; }
      await this.write(KEY, { account, entries });
      this.#changed();
    });
  }
  get(ref) {
    const account = this.account, epoch = this.#epoch;
    return this.#run(async () => {
      if (!account) { return null; }
      const entries = await this.#entries(account);
      const key = await digest(ref);
      return account === this.account && epoch === this.#epoch ? entries.find((e) => e.key === key) ?? null : null;
    });
  }
  clear() {
    this.#epoch++;
    return this.#run(async () => {
      await this.write(GENERATION, crypto.randomUUID());
      await this.remove(KEY);
      this.#changed(); this.channel?.postMessage(null);
    }, true);
  }
}

export const sentImages = new SentImages();
