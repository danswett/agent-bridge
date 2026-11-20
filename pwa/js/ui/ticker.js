// A ticker that runs only while the page is in sight (#610). The app's clocks ("2m ago", "last
// seen 5m ago") moved on every 15 or 30 s whether anyone could see them or not, each a wake-up
// of a phone that was meant to be asleep. Out of sight they stop; back in sight they catch up
// at once, then tick again.
const hiddenPages = new WeakSet();
const trackedPages = new WeakSet();
export const isPageHidden = (doc = globalThis.document) => doc?.hidden === true || doc?.visibilityState === 'hidden' || (doc != null && hiddenPages.has(doc));

// Page-cache navigation can hide a page without changing visibilityState.
export function watchPageVisibility(changed, doc = globalThis.document) {
  const win = doc?.defaultView;
  // Keep the document's state accurate even between short-lived ticker/FLIP subscriptions.
  if (doc && !trackedPages.has(doc)) {
    trackedPages.add(doc);
    win?.addEventListener?.('pagehide', () => hiddenPages.add(doc));
    win?.addEventListener?.('pageshow', (event) => { if (event.persisted) { hiddenPages.delete(doc); } });
  }
  const show = (event) => { if (event.persisted) { changed(); } };
  doc?.addEventListener?.('visibilitychange', changed);
  win?.addEventListener?.('pagehide', changed);
  win?.addEventListener?.('pageshow', show);
  return () => {
    doc?.removeEventListener?.('visibilitychange', changed);
    win?.removeEventListener?.('pagehide', changed);
    win?.removeEventListener?.('pageshow', show);
  };
}

export function whileVisible(fn, ms, doc = globalThis.document) {
  let timer = null;
  let stopped = false;
  const hidden = () => isPageHidden(doc);
  const start = () => { if (!stopped && !timer && !hidden()) { timer = setInterval(fn, ms); } };
  const pause = () => { clearInterval(timer); timer = null; };
  const changed = () => {
    if (stopped) { return; }
    if (hidden()) { pause(); return; }
    if (timer) { return; }
    fn();
    start();
  };
  if (!hidden()) { start(); }
  const unwatch = watchPageVisibility(changed, doc);
  return {
    stop() {
      stopped = true;
      pause();
      unwatch();
    },
  };
}
