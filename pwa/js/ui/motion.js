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

// The Needs you lane (#165): an entry that arrives once the app is running has its mark fade in,
// and Approve once's or Deny's outcome rises in under the buttons. The entry, and the command an
// approval shows, appear at once and never fade, slide or pass under anything.
//
// Watched from outside (needs-you-view.js stays as it is) and drawn with the Web Animations API,
// not CSS: a CSS animation can't tell an entry drawn at start from one arriving, and replays
// whenever a hidden lane shows again (Back on a phone), which an animation made here never does.
// fresh(entry): whether an entry that has just appeared is news rather than the app starting.
export function watchLane(list, { fresh = () => true } = {}) {
  const Observer = list?.ownerDocument?.defaultView?.MutationObserver;
  if (!Observer) { return () => {}; }
  // Every entry ever in the list: an entry reordered, or redrawn in place, is not arriving.
  const seen = new WeakSet([...list.children]);
  const observer = new Observer((records) => {
    const added = new Set();
    const statuses = new Set();
    for (const r of records) {
      if (r.type === 'childList') {
        for (const n of r.addedNodes) {
          if (n.nodeType !== 1 || n.parentNode !== list || seen.has(n)) { continue; }
          seen.add(n);
          added.add(n);
          if (fresh(n)) { enter(n.querySelector(':scope > .needs-mark'), 'listItem'); }
        }
      } else if (r.target.matches?.('.needs-status') && !r.target.hidden && r.oldValue !== r.target.getAttribute(r.attributeName)) {
        statuses.add(r.target);
      }
    }
    // An outcome shown or changed on an entry already there; one an entry arrives with is part of it.
    for (const status of statuses) {
      if (!added.has(status.closest('.needs-entry'))) { enter(status, 'acknowledge', { dy: 1 }); }
    }
  });
  observer.observe(list, { childList: true, subtree: true, attributes: true, attributeFilter: ['hidden', 'class'], attributeOldValue: true });
  return () => observer.disconnect();
}

// A question attached inside content that is still sliding in on a push (ui.md §5.3: an
// approval's command never slides): the push stops travelling and only finishes fading.
export function stopTravel(el) {
  const doc = el?.ownerDocument;
  if (!doc?.getAnimations) { return; }
  for (const a of doc.getAnimations()) {
    const target = a.effect?.target;
    if (a.id !== 'ab-motion-push' || !target?.contains?.(el)) { continue; }
    try {
      a.effect.setKeyframes(a.effect.getKeyframes().map(({ offset, opacity }) => (opacity === undefined ? { offset } : { offset, opacity })));
    } catch {
      a.finish();
    }
  }
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

// Press (ui.md §5.3): the pressed fill shows at once, and eases back on release. Only on
// release: app.css eases a control's fill only while it carries data-press="released", so a
// row selected, a tab chosen or a theme changed takes its new fill at once.
export const PRESSABLE = '.button, .icon-button, .row-main, .nav-item, .view-tab, .machine-tab, .row-menu-item, .choice, .toggle';

export function watchPresses(doc = globalThis.document) {
  const timers = new WeakMap();
  let pressed = null;
  doc.addEventListener('pointerdown', (e) => { pressed = e.target.closest?.(PRESSABLE) ?? null; }, true);
  const release = () => {
    const el = pressed;
    pressed = null;
    if (!el) { return; }
    const t = timing('press', { exit: true, doc });
    clearTimeout(timers.get(el));
    if (!(t.duration > 0)) { delete el.dataset.press; return; }
    el.dataset.press = 'released';
    timers.set(el, setTimeout(() => { delete el.dataset.press; }, t.duration + 50));
  };
  for (const type of ['pointerup', 'pointercancel']) { doc.addEventListener(type, release, true); }
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
// choice acts), and the copy only shows where it went. Call it before el leaves the page.
//
// The copy is none of the page's: it sits in a closed shadow root of an inert, aria-hidden host
// that takes no pointer, so no selector, screen reader or tap finds it. A row holds an agent's
// words, so the copy is built node by node, never from markup: text stays text, every HTML element
// becomes a plain div or span (no link, button, field, script or frame), and no id, class, name or
// data attribute is kept; only the glyphs' SVG shapes and their sprite references are.
export function leave(el, entry) {
  const doc = el?.ownerDocument;
  const t = timing(entry, { exit: true, doc });
  if (!el?.isConnected || !(t.duration > 0) || !doc?.body?.attachShadow) { return null; }
  const r = el.getBoundingClientRect();
  if (!r.width || !r.height) { return null; }
  // Only what showed of it: a row half scrolled out of its list, or a glyph under the end of a
  // conversation, must not be drawn whole over what clipped it.
  const v = shown(el, r);
  if (!v) { return null; }
  const host = doc.createElement('div');
  host.setAttribute('aria-hidden', 'true');
  host.inert = true;
  const cut = [v.top - r.top, r.right - v.right, r.bottom - v.bottom, v.left - r.left];
  Object.assign(host.style, {
    position: 'fixed', left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px`,
    margin: '0', padding: '0', pointerEvents: 'none', contain: 'layout paint',
    // Under the app bar and the phone's bottom bar (20): a copy never covers them.
    zIndex: '19',
    clipPath: cut.some((n) => n > 0.5) ? `inset(${cut.map((n) => `${Math.max(0, n)}px`).join(' ')})` : '',
  });
  host.attachShadow({ mode: 'closed' }).append(inlineCopy(el));
  doc.body.append(host);
  const a = run(host, [{ opacity: 1 }, { opacity: 0 }], entry, t, { fill: 'forwards' });
  if (!a) { host.remove(); return null; }
  const done = () => host.remove();
  a.finished.then(done, done);
  return a;
}

// The part of r (el's box) that shows: inside the viewport and every ancestor that clips el, or
// null when none of it does. An ancestor clips only what it contains: a positioned descendant
// escapes the static ones between, and a fixed one all of them.
function shown(el, r) {
  const doc = el.ownerDocument;
  const win = doc.defaultView;
  let box = { left: Math.max(r.left, 0), top: Math.max(r.top, 0), right: Math.min(r.right, win.innerWidth), bottom: Math.min(r.bottom, win.innerHeight) };
  let position = getComputedStyle(el).position;
  for (let a = el.parentElement; a && a !== doc.documentElement && position !== 'fixed'; a = a.parentElement) {
    const cs = getComputedStyle(a);
    const contains = cs.position !== 'static' || cs.transform !== 'none';
    if (position === 'absolute' && !contains) { continue; }
    position = cs.position;
    if (cs.overflowX === 'visible' && cs.overflowY === 'visible' && !/paint|content|strict/.test(cs.contain)) { continue; }
    const b = a.getBoundingClientRect();
    const left = b.left + a.clientLeft;
    const top = b.top + a.clientTop;
    const inner = a.clientWidth || a.clientHeight
      ? { left, top, right: left + a.clientWidth, bottom: top + a.clientHeight }
      : b;
    box = {
      left: Math.max(box.left, inner.left), top: Math.max(box.top, inner.top),
      right: Math.min(box.right, inner.right), bottom: Math.min(box.bottom, inner.bottom),
    };
  }
  return box.right > box.left && box.bottom > box.top ? box : null;
}

const SVG = 'http://www.w3.org/2000/svg';
const HTML = 'http://www.w3.org/1999/xhtml';
// The SVG a glyph or an icon is drawn with, and the attributes that draw it. Anything else in an
// SVG (a link, foreignObject, an image, a script) is left out.
const SHAPES = new Set(['svg', 'g', 'use', 'path', 'circle', 'ellipse', 'rect', 'line', 'polyline', 'polygon']);
const DRAWN = new Set(['viewBox', 'width', 'height', 'd', 'cx', 'cy', 'r', 'rx', 'ry', 'x', 'y', 'x1', 'y1', 'x2', 'y2', 'points', 'transform', 'fill', 'stroke', 'stroke-width', 'preserveAspectRatio']);
// A <use> refers only to the generated sprite (js/ui/glyph.js), never to anything else.
const SPRITE_REF = /^design\/sprite\.svg#[\w-]+$/;
// HTML kept as itself: line breaks, which a span can't stand in for.
const BREAKS = new Set(['br', 'wbr']);
// HTML whose children are a value or code rather than what it shows.
const OPAQUE = new Set(['textarea', 'select', 'script', 'style', 'noscript', 'template', 'iframe', 'object', 'embed', 'video', 'audio', 'canvas']);

function inlineCopy(el) {
  const doc = el.ownerDocument;
  const copy = (node) => {
    if (node.nodeType === 3) { return doc.createTextNode(node.data); }
    if (node.nodeType !== 1) { return null; }
    const cs = getComputedStyle(node);
    if (cs.display === 'none') { return null; }
    let c;
    if (node.namespaceURI === SVG) {
      if (!SHAPES.has(node.localName)) { return null; }
      const ref = node.localName === 'use' ? node.getAttribute('href') : null;
      if (node.localName === 'use' && !SPRITE_REF.test(ref ?? '')) { return null; }
      c = doc.createElementNS(SVG, node.localName);
      for (const a of node.attributes) { if (DRAWN.has(a.name)) { c.setAttribute(a.name, a.value); } }
      if (ref) { c.setAttribute('href', ref); }
    } else if (node.namespaceURI === HTML) {
      c = doc.createElement(BREAKS.has(node.localName) ? node.localName : cs.display.startsWith('inline') ? 'span' : 'div');
    } else {
      return null;
    }
    for (let i = 0; i < cs.length; i += 1) {
      const p = cs[i];
      if (p.startsWith('--') || p.startsWith('animation') || p.startsWith('transition')) { continue; }
      c.style.setProperty(p, cs.getPropertyValue(p));
    }
    if (node.namespaceURI === HTML && OPAQUE.has(node.localName)) { return c; }
    for (const child of node.childNodes) { const k = copy(child); if (k) { c.append(k); } }
    return c;
  };
  const top = copy(el) ?? doc.createElement('div');
  // The host is placed where el's box is drawn, which already counts any motion el is in (an
  // entrance, a reorder): the copy keeps none of its own, or it would move twice.
  for (const [p, v] of [['margin', '0'], ['position', 'static'], ['transform', 'none'], ['translate', 'none'], ['rotate', 'none'], ['scale', 'none']]) {
    top.style.setProperty(p, v);
  }
  return top;
}
