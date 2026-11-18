const PREFIX = 'agent-bridge.app.replies:';
const LOCK = 'agent-bridge.app.replies';
const storageLocks = new WeakMap();
export const replyAccount = (account) => String(account?.accountId ?? account?.login ?? '');
const prefix = (account) => `${PREFIX}${JSON.stringify(account)}:`;
const key = (account, entry) => `${prefix(account)}${JSON.stringify([entry.hostId, entry.sessionId, ''])}`;
export const REPLY_MEMORY_MS = 8 * 60 * 1000;

export function withReplies(storage, action) {
  const locks = storageLocks.get(storage) ?? globalThis.navigator?.locks;
  return locks?.request ? locks.request(LOCK, action) : action();
}

export function clearReplies(account, storage) {
  const keys = Array.from({ length: storage.length }, (_, i) => storage.key(i));
  for (const k of keys) { if (k?.startsWith(prefix(account))) { storage.removeItem(k); } }
}

export class ReplyState {
  constructor({ storage, account, locks = globalThis.navigator?.locks, active, now }) {
    Object.assign(this, { storage, account, locks, active, now });
    if (storage && typeof storage === 'object') { storageLocks.set(storage, locks); }
  }
  read(entry) { return JSON.parse(this.storage.getItem(key(this.account, entry)) ?? 'null'); }
  expired(entry) { return this.now() - entry.firstSentAt > REPLY_MEMORY_MS; }
  records() {
    try {
      const keys = Array.from({ length: this.storage.length }, (_, i) => this.storage.key(i));
      return keys.filter((k) => k?.startsWith(prefix(this.account))).map((k) => {
        const entry = JSON.parse(this.storage.getItem(k));
        if (entry === null) { return null; }
        if (!['hostId', 'sessionId', 'request', 'text'].every((p) => typeof entry[p] === 'string')
          || entry.questionId !== '' || !Array.isArray(entry.attachments) || entry.attachments.some((id) => typeof id !== 'string')
          || !Number.isFinite(entry.firstSentAt) || entry.firstSentAt < 0 || entry.firstSentAt > this.now()
          || key(this.account, entry) !== k) { throw new Error('invalid pending reply'); }
        return entry;
      }).filter(Boolean);
    } catch {
      this.problem = new Error('Reply storage cannot be read safely. Nothing new will be sent; sign out to clear it.');
      console.error(this.problem.message);
      return [];
    }
  }
  async claim(entry, uuid) {
    if (this.problem) { throw this.problem; }
    if (!this.locks?.request) { throw new Error('This browser needs Web Locks to send replies safely. Nothing was sent.'); }
    try {
      return await this.locks.request(LOCK, () => {
        const saved = this.read(entry);
        if (!this.active()) { throw Object.assign(new Error('The account changed. The reply was not sent.'), { accountChanged: true }); }
        if (saved) { return saved; }
        const record = {
          hostId: entry.hostId, sessionId: entry.sessionId, questionId: '', request: uuid(),
          text: entry.text, attachments: entry.attachments.map((file) => file.id), firstSentAt: this.now(),
        };
        this.storage.setItem(key(this.account, entry), JSON.stringify(record));
        return record;
      });
    } catch (error) {
      if (error.accountChanged) { throw error; }
      throw new Error(error.name === 'QuotaExceededError'
        ? 'Local storage is full. The reply was not sent.'
        : 'Local storage is unavailable. The reply was not sent.');
    }
  }
  async check(entry) {
    try {
      return await this.locks.request(LOCK, () => {
        const saved = this.read(entry);
        if (this.active() && saved?.request === entry.request && saved.text === entry.answer.text
          && JSON.stringify(saved.attachments) === JSON.stringify(entry.answer.attachments.map((file) => file.id)) && !this.expired(saved)) { return; }
        throw new Error('identity changed');
      });
    } catch { throw Object.assign(new Error('The saved reply cannot be retried safely. Check the conversation before sending again.'), { fromHost: true }); }
  }
  async settle(entry, discard = false) {
    try {
      if (!this.locks?.request) { throw new Error('Web Locks unavailable'); }
      await this.locks.request(LOCK, () => {
        const saved = this.read(entry);
        if (saved?.request === entry.request && (discard || entry.outcome !== 'unconfirmed' || this.expired(saved))) {
          this.storage.removeItem(key(this.account, entry));
        }
      });
    } catch { console.error('The saved reply could not be cleared; its identity is still required before another send.'); }
  }
}
