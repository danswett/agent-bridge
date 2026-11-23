export function lifetime(expiry, { now = Date.now, monotonic = () => performance.now(), signal } = {}) {
  if (!Number.isSafeInteger(expiry)) throw new Error('Refused: invalid expiry.');
  const controller = new AbortController(), duration = Math.max(0, Math.min(300000, expiry - now())), end = monotonic() + duration;
  const abort = () => controller.abort(), timer = setTimeout(abort, duration);
  if (signal?.aborted) abort(); else signal?.addEventListener('abort', abort, { once: true });
  const remaining = () => Math.max(0, Math.min(expiry - now(), end - monotonic()));
  const valid = () => { if (remaining() <= 0) abort(); return !controller.signal.aborted; };
  return { signal: controller.signal, remaining, valid,
    async wait(work) {
      if (!valid()) throw new DOMException('session ended', 'AbortError');
      let rejectAbort; const cancelled = new Promise((_, reject) => { rejectAbort = () => reject(new DOMException('session ended', 'AbortError')); });
      controller.signal.addEventListener('abort', rejectAbort, { once: true });
      try { return await Promise.race([cancelled, work(controller.signal)]); }
      finally { controller.signal.removeEventListener('abort', rejectAbort); }
    },
    close() { clearTimeout(timer); signal?.removeEventListener('abort', abort); abort(); },
  };
}
