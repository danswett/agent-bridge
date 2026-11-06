// A ticker that runs only while the page is in sight (#610). The app's clocks ("2m ago", "last
// seen 5m ago") moved on every 15 or 30 s whether anyone could see them or not, each a wake-up
// of a phone that was meant to be asleep. Out of sight they stop; back in sight they catch up
// at once, then tick again.
export function whileVisible(fn, ms, doc = globalThis.document) {
  let timer = null;
  const hidden = () => doc?.visibilityState === 'hidden';
  const start = () => { if (!timer) { timer = setInterval(fn, ms); } };
  const pause = () => { clearInterval(timer); timer = null; };
  const changed = () => {
    if (hidden()) { pause(); return; }
    if (timer) { return; }
    fn();
    start();
  };
  if (!hidden()) { start(); }
  doc?.addEventListener?.('visibilitychange', changed);
  return {
    stop() {
      pause();
      doc?.removeEventListener?.('visibilitychange', changed);
    },
  };
}
