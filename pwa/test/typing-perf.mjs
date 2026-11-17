export function summarizeTyping(samples, worstFrameMs) {
  const valid = (n) => typeof n === 'number' && Number.isFinite(n) && n >= 0;
  if (!Array.isArray(samples) || !samples.length || !samples.every(valid) || !valid(worstFrameMs)) {
    throw new Error('Invalid typing measurements');
  }
  const sorted = [...samples].sort((a, b) => a - b);
  const rank = (p) => sorted[Math.ceil(sorted.length * p) - 1];
  const result = { sampleCount: sorted.length, p50: rank(0.5), p95: rank(0.95), max: sorted.at(-1), worstFrameMs };
  result.breaches = Object.entries({ p95: result.p95, max: result.max, frameGap: worstFrameMs })
    .filter(([, value]) => value >= 100).map(([name]) => name);
  return result;
}

// Serialized by locator.evaluate: no module-scoped dependencies inside this function.
export function installTypingMeter(input) {
  const samples = [];
  let pending = null;
  let worstFrameMs = 0;
  let lastFrame = performance.now();
  let frame;
  let storageWrites = 0;
  let transcriptMutations = 0;
  let trustedInputs = 0;
  const initialHeapBytes = performance.memory?.usedJSHeapSize ?? null;
  const transcript = input.closest('.session-view').querySelector('.stream-scroll');
  const observer = new MutationObserver((records) => { transcriptMutations += records.length; });
  observer.observe(transcript, { subtree: true, childList: true, characterData: true });
  const setItem = Storage.prototype.setItem;
  Storage.prototype.setItem = function (...args) {
    storageWrites += 1;
    return Reflect.apply(setItem, this, args);
  };
  const beat = (now) => {
    worstFrameMs = Math.max(worstFrameMs, now - lastFrame);
    lastFrame = now;
    frame = requestAnimationFrame(beat);
  };
  frame = requestAnimationFrame(beat);
  const keydown = (event) => {
    if (event.key.length !== 1 || event.ctrlKey || event.metaKey || event.altKey || event.isComposing) { return; }
    if (pending !== null) { throw new Error('Typing meter received overlapping keystrokes'); }
    pending = event.timeStamp > performance.timeOrigin ? event.timeStamp - performance.timeOrigin : event.timeStamp;
  };
  const oninput = (event) => {
    if (pending === null) { throw new Error('Typing meter received input without a keystroke'); }
    if (event.isTrusted) { trustedInputs += 1; }
    const start = pending;
    // The second frame bounds a rendering opportunity after native input and all its handlers.
    // This is a conservative paint proxy, not a physical display/presentation timestamp.
    requestAnimationFrame(() => requestAnimationFrame(() => {
      samples.push(performance.now() - start);
      pending = null;
    }));
  };
  input.addEventListener('keydown', keydown, true);
  input.addEventListener('input', oninput, true);
  window.__typingPerf = {
    samples,
    finish() {
      cancelAnimationFrame(frame);
      input.removeEventListener('keydown', keydown, true);
      input.removeEventListener('input', oninput, true);
      transcriptMutations += observer.takeRecords().length;
      observer.disconnect();
      Storage.prototype.setItem = setItem;
      return {
        samples: [...samples], worstFrameMs, trustedInputs, storageWrites, transcriptMutations,
        initialHeapBytes, finalHeapBytes: performance.memory?.usedJSHeapSize ?? null,
        domNodes: document.getElementsByTagName('*').length,
      };
    },
  };
}
