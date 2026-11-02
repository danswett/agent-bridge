// Motion (docs/design/ui.md §5.3): every animation the PWA draws itself, timed by the
// generated tokens in design/tokens.css (--ab-motion-<entry>-duration, -ease, -distance,
// -exit-duration, -exit-ease) and never by a number of this file's own. Reduced motion is in
// those tokens already: under prefers-reduced-motion every distance is 0 and every duration
// 0 or the cross-fade, so nothing here asks the media query.
//
// Every motion only shows a change that has already happened: the DOM is in its new state
// before anything moves, nothing is made inert or untappable while it moves, and a second
// change interrupts the first rather than waiting behind it.

const MS = /^(-?[\d.]+)(ms|s)$/;

function length(value) {
  const m = MS.exec(String(value).trim());
  if (!m) { return parseFloat(value) || 0; }
  return Number(m[1]) * (m[2] === 's' ? 1000 : 1);
}

const kebab = (s) => s.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();

// A catalogue entry's timing, read from the tokens now (so a change of the reduced-motion
// setting applies to the next motion): { duration, easing, distance }, or the leaving half's.
export function timing(entry, { exit = false, doc = globalThis.document } = {}) {
  const css = doc?.documentElement ? getComputedStyle(doc.documentElement) : null;
  const read = (name) => css?.getPropertyValue(name).trim() ?? '';
  const p = `--ab-motion-${kebab(entry)}`;
  const half = exit && read(`${p}-exit-duration`) ? `${p}-exit` : p;
  return {
    duration: Math.max(0, length(read(`${half}-duration`))),
    easing: read(`${half}-ease`) || 'linear',
    distance: length(read(`${p}-distance`)),
  };
}

// The smooth scroll the catalogue's Scroll to a question asks for, or a jump under reduced motion.
export function scrollBehavior(doc = globalThis.document) {
  return timing('scroll', { doc }).duration > 0 ? 'smooth' : 'auto';
}

// What the motions drew, by entry, as Animation.id: the tests read them back.
function run(el, keyframes, entry, t, options = {}) {
  if (!el?.isConnected || typeof el.animate !== 'function' || !(t.duration > 0)) { return null; }
  try {
    const a = el.animate(keyframes, { duration: t.duration, easing: t.easing, fill: 'backwards', ...options });
    a.id = `ab-motion-${entry}`;
    return a;
  } catch {
    // An engine without this (a pseudo-element it can't animate, say) shows the change at once.
    return null;
  }
}

// Shows a hidden element (a banner) as the catalogue's Toast and banner does: in from its edge
// (dy -1, from above; 1, from below).
export function reveal(el, { entry = 'toast', dy = -1 } = {}) {
  if (!el || !el.hidden) { return; }
  el.hidden = false;
  enter(el, entry, { dy });
}

// Disclosures (Reasoning, Tool history, a delegate's details, Ended): when the person opens
// one, what it reveals settles in, a nudge down, as its space opens at once; closing is at
// once. Only an open the person asked for: a <details> a page opens for them (a question
// unfolding its card) appears as it is. Installed once, for every <details> on the page.
export function watchDisclosures(doc = globalThis.document) {
  const asked = new WeakSet();
  doc.addEventListener('click', (e) => {
    const summary = e.target.closest?.('summary');
    const details = summary?.parentElement;
    if (details?.localName === 'details' && details.querySelector(':scope > summary') === summary) { asked.add(details); }
  }, true);
  // toggle doesn't bubble: heard on the way down.
  doc.addEventListener('toggle', (e) => {
    const details = e.target;
    if (details?.localName !== 'details' || !asked.has(details)) { return; }
    asked.delete(details);
    if (!details.open) { return; }
    for (const child of details.children) {
      if (child.localName !== 'summary') { enter(child, 'disclosure', { dy: -1 }); }
    }
  }, true);
}

// An element arriving where it now is: it fades in, travelling the entry's distance from
// (dx, dy), each -1, 0 or 1. With no distance (reduced motion, or an entry that never travels)
// it only fades.
export function enter(el, entry, { dx = 0, dy = 0 } = {}) {
  const t = timing(entry, { doc: el?.ownerDocument });
  const x = dx * t.distance;
  const y = dy * t.distance;
  const from = x || y ? { opacity: 0, transform: `translate(${x}px, ${y}px)` } : { opacity: 0 };
  return run(el, [from, x || y ? { opacity: 1, transform: 'none' } : { opacity: 1 }], entry, t);
}

// An element that moved from (fromX, fromY), in px, to where it is now: it slides the rest of
// the way. pseudo: '::before' or '::after', for a pill drawn as one. scaleX: its width before,
// over its width now, for an indicator that changes width as it moves.
export function slide(el, entry, fromX, fromY, { pseudo = null, scaleX = 1 } = {}) {
  if (!fromX && !fromY && scaleX === 1) { return null; }
  const t = timing(entry, { doc: el?.ownerDocument });
  const scale = scaleX !== 1 ? ` scaleX(${scaleX})` : '';
  const origin = scaleX !== 1 ? { transformOrigin: 'left center' } : {};
  return run(el, [{ transform: `translate(${fromX}px, ${fromY}px)${scale}`, ...origin }, { transform: 'none', ...origin }], entry, t, pseudo ? { pseudoElement: pseudo } : {});
}

// A copy of el, drawn where it is, that fades out over the entry's leaving time, while the
// element itself goes at once: the change takes effect at once (a row's space closes, a menu's
// choice acts), and the copy only shows where it went. The copy carries its computed styles
// inline, inside a closed shadow root, so it is none of the page's: no class, id, data or text
// a selector, a screen reader or a tap can find. Call it before el leaves the page.
export function leave(el, entry) {
  const doc = el?.ownerDocument;
  const t = timing(entry, { exit: true, doc });
  if (!el?.isConnected || !(t.duration > 0) || !doc?.body?.attachShadow) { return null; }
  const r = el.getBoundingClientRect();
  if (!r.width || !r.height) { return null; }
  const host = doc.createElement('div');
  host.setAttribute('aria-hidden', 'true');
  host.inert = true;
  Object.assign(host.style, {
    position: 'fixed', left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px`,
    margin: '0', padding: '0', pointerEvents: 'none', zIndex: '30', contain: 'layout paint',
  });
  host.attachShadow({ mode: 'closed' }).append(inlineCopy(el));
  doc.body.append(host);
  const a = run(host, [{ opacity: 1 }, { opacity: 0 }], entry, t, { fill: 'forwards' });
  if (!a) { host.remove(); return null; }
  const done = () => host.remove();
  a.finished.then(done, done);
  return a;
}

// Attributes a copy keeps: those that draw something (an SVG's geometry and sprite), never
// those that name or identify it.
const DRAWN = new Set(['viewBox', 'width', 'height', 'href', 'd', 'cx', 'cy', 'r', 'x', 'y', 'x1', 'y1', 'x2', 'y2', 'transform', 'fill', 'stroke', 'stroke-width', 'preserveAspectRatio']);

function inlineCopy(el) {
  const doc = el.ownerDocument;
  const copy = (node) => {
    if (node.nodeType === 3) { return doc.createTextNode(node.data); }
    if (node.nodeType !== 1) { return null; }
    const c = doc.createElementNS(node.namespaceURI, node.localName);
    for (const a of node.attributes) { if (DRAWN.has(a.name)) { c.setAttribute(a.name, a.value); } }
    const cs = getComputedStyle(node);
    if (cs.display === 'none') { return null; }
    for (let i = 0; i < cs.length; i += 1) {
      const p = cs[i];
      if (p.startsWith('--') || p.startsWith('animation') || p.startsWith('transition')) { continue; }
      c.style.setProperty(p, cs.getPropertyValue(p));
    }
    for (const child of node.childNodes) { const k = copy(child); if (k) { c.append(k); } }
    return c;
  };
  const top = copy(el) ?? doc.createElement('div');
  top.style.setProperty('margin', '0');
  top.style.setProperty('position', 'static');
  return top;
}
