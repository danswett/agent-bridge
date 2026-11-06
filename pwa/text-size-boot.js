// Text size, put on the page before its first paint, so a page set Smaller or Larger never
// shows at Default first. A classic script, as main.js is a module and runs after the paint.
// The setting and its storage are js/text-size.js; the key and values here must match it.
try {
  const size = globalThis.localStorage?.getItem('agent-bridge.app.textSize');
  if (size === 'smaller' || size === 'larger') { document.documentElement.setAttribute('data-text-size', size); }
} catch { /* Storage that can't be read leaves Default. */ }