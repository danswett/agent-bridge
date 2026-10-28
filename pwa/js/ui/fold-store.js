// Whether each session's card is folded, per viewer and per session, kept across reloads.
// It also remembers which question last opened the card: a new question opens a folded
// card once, so it is never folded away unseen, but folding it again while the same
// question waits is respected.
const PREFIX = 'agent-bridge.app.fold.';

export class FoldStore {
  constructor(storage = globalThis.localStorage) { this.storage = storage; }

  #read(id) {
    try { return JSON.parse(this.storage.getItem(PREFIX + id) ?? 'null') ?? {}; } catch { return {}; }
  }

  #write(id, rec) {
    try {
      if (!rec.folded && !rec.question) { this.storage.removeItem(PREFIX + id); } else { this.storage.setItem(PREFIX + id, JSON.stringify(rec)); }
    } catch { /* storage full or blocked: a nicety, the card still works */ }
  }

  isFolded(id) { return this.#read(id).folded === true; }

  setFolded(id, folded) {
    const rec = this.#read(id);
    this.#write(id, { ...rec, folded });
  }

  // Called with the pending question's id (or null). Returns true if the card was folded
  // and has just been opened for a new question.
  noteQuestion(id, questionId) {
    const rec = this.#read(id);
    if (!questionId || rec.question === questionId) { return false; }
    const opened = rec.folded === true;
    this.#write(id, { folded: false, question: questionId });
    return opened;
  }
}
