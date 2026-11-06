// The status glyphs and the icon set, drawn from the generated sprite (design/sprite.svg, by
// tools/design-gen from /design; design/README.md): never a path or a colour of this file's
// own. Colour is the role's, set in app.css from data-glyph; the moving part is its own layer
// so app.css can turn it, and reduced motion stops it there.
//
// A glyph is an HTML box holding its layers, each an <svg> of the same 16 grid: what stands
// still, the moving part in a box of its own, and the driven badge over both. The moving part
// used to be a group inside the glyph's one <svg>, and a browser turns an SVG child on the main
// thread: every working row cost a style recalculation and a layout each frame, 4.5 s of main
// thread a minute with nothing changing (#610, #505). An HTML box's rotation runs on the
// compositor.

import { enter, leave } from './motion.js';

const NS = 'http://www.w3.org/2000/svg';
const SPRITE = 'design/sprite.svg';

export const GLYPHS = Object.freeze(['idle', 'working', 'delegating', 'needs', 'done', 'failed', 'ended']);
export const GLYPH_SIZES = Object.freeze([12, 14, 16, 20, 24]);
// The part that moves, as the sprite splits it: glyph-<name>--still and glyph-<name>--<part>.
const MOVING = { working: 'spin', delegating: 'orbit' };

function use(id) {
  const u = document.createElementNS(NS, 'use');
  u.setAttribute('href', `${SPRITE}#${id}`);
  return u;
}

// One layer of a glyph: an <svg> of the glyph's grid, drawing the sprite's symbols.
function layer(cls, ...uses) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('focusable', 'false');
  svg.setAttribute('aria-hidden', 'true');
  if (cls) { svg.setAttribute('class', cls); }
  svg.append(...uses);
  return svg;
}

// A glyph, at one of the five sizes. label: its accessible name (ui.md §9), or none for a
// glyph whose state is said beside it. driven: the driven badge over it (rule 7).
export function glyph(name, options = {}) {
  return setGlyph(document.createElement('span'), name, options);
}

// Redraws a glyph in place, rebuilding its parts only when the glyph or badge changes, so a
// turning arc isn't restarted by every update.
export function setGlyph(el, name, { size = 16, label = null, driven = false } = {}) {
  const g = GLYPHS.includes(name) ? name : 'idle';
  const px = GLYPH_SIZES.includes(size) ? size : 16;
  const drawn = `${g}${driven ? '+driven' : ''}`;
  // A status glyph's state change (ui.md §5.3): the old glyph cross-fades into the new one, with
  // its colour. Not its first drawing, and only where it is on screen. The copy that fades out is
  // taken first, while the glyph still has the class and data-glyph that colour it.
  const change = !!el.dataset.drawn && el.dataset.drawn !== drawn && el.isConnected;
  if (change) { leave(el, 'glyph'); }
  el.setAttribute('class', `glyph${driven ? ' driven' : ''}`);
  el.dataset.size = String(px);
  el.dataset.glyph = g;
  if (label) {
    el.setAttribute('role', 'img');
    el.setAttribute('aria-label', label);
    el.removeAttribute('aria-hidden');
  } else {
    el.removeAttribute('role');
    el.removeAttribute('aria-label');
    el.setAttribute('aria-hidden', 'true');
  }
  if (el.dataset.drawn !== drawn) {
    el.dataset.drawn = drawn;
    const part = MOVING[g];
    const parts = [layer(null, use(part ? `glyph-${g}--still` : `glyph-${g}`))];
    if (part) {
      const moving = document.createElement('span');
      moving.setAttribute('class', `glyph-${part}`);
      moving.append(layer(null, use(`glyph-${g}--${part}`)));
      parts.push(moving);
    }
    if (driven) { parts.push(layer('glyph-badge', use('glyph-driven-badge'))); }
    el.replaceChildren(...parts);
    if (change) { enter(el, 'glyph'); }
  }
  return el;
}

export const ICON_SIZES = Object.freeze([16, 20, 22, 24]);

// The driven-badge diamond on its own (ui.md §2.1), for a line an agent's prompt heads (#489):
// the glyph's 16 grid cut down to the diamond, so it draws at the size given and is centred.
// Decorative: the words beside it say who sent the prompt.
export function drivenBadge(size = 12) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '9.8 9.8 7 7');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('class', 'driven-diamond');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  const u = use('glyph-driven-badge');
  u.setAttribute('width', '16');
  u.setAttribute('height', '16');
  svg.append(u);
  return svg;
}

// An icon from design/icons, at 16 or 20 on a desktop and 22 or 24 on a phone, its stroke
// scaled to look the same weight at each (design/README.md). Decorative: the control it sits
// in carries the name. With no size, the size and stroke come from app.css (cls).
export function designIcon(name, { size = null, cls = '' } = {}) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 20 20');
  const classes = ['ab-icon'];
  if (size !== null) {
    const px = ICON_SIZES.includes(size) ? size : 20;
    svg.setAttribute('width', String(px));
    svg.setAttribute('height', String(px));
    classes.push(`ab-icon-${px}`);
  }
  if (cls) { classes.push(cls); }
  svg.setAttribute('class', classes.join(' '));
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  svg.dataset.icon = name;
  svg.append(use(`icon-${name}`));
  return svg;
}
