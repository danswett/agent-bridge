// The status glyphs and the icon set, drawn from the generated sprite (design/sprite.svg, by
// tools/design-gen from /design; design/README.md): never a path or a colour of this file's
// own. Colour is the role's, set in app.css from data-glyph; the moving part is its own group
// so app.css can turn it, and reduced motion stops it there.

const NS = 'http://www.w3.org/2000/svg';
const SPRITE = 'design/sprite.svg';

export const GLYPHS = Object.freeze(['idle', 'working', 'delegating', 'needs', 'done', 'failed', 'ended']);
export const GLYPH_SIZES = Object.freeze([12, 14, 16, 20, 24]);
// The part that moves, as the sprite splits it: glyph-<name>--still and glyph-<name>--<part>.
const MOVING = { working: 'spin', delegating: 'orbit' };

function use(id, cls = null) {
  const u = document.createElementNS(NS, 'use');
  u.setAttribute('href', `${SPRITE}#${id}`);
  if (cls) { u.setAttribute('class', cls); }
  return u;
}

// A glyph, at one of the five sizes. label: its accessible name (ui.md §9), or none for a
// glyph whose state is said beside it. driven: the driven badge over it (rule 7).
export function glyph(name, options = {}) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('focusable', 'false');
  return setGlyph(svg, name, options);
}

// Redraws a glyph in place, rebuilding its parts only when the glyph or badge changes, so a
// turning arc isn't restarted by every update.
export function setGlyph(svg, name, { size = 16, label = null, driven = false } = {}) {
  const g = GLYPHS.includes(name) ? name : 'idle';
  const px = GLYPH_SIZES.includes(size) ? size : 16;
  svg.setAttribute('class', `glyph${driven ? ' driven' : ''}`);
  svg.setAttribute('width', String(px));
  svg.setAttribute('height', String(px));
  svg.dataset.glyph = g;
  if (label) {
    svg.setAttribute('role', 'img');
    svg.setAttribute('aria-label', label);
    svg.removeAttribute('aria-hidden');
  } else {
    svg.removeAttribute('role');
    svg.removeAttribute('aria-label');
    svg.setAttribute('aria-hidden', 'true');
  }
  const drawn = `${g}${driven ? '+driven' : ''}`;
  if (svg.dataset.drawn !== drawn) {
    svg.dataset.drawn = drawn;
    const part = MOVING[g];
    const parts = [use(part ? `glyph-${g}--still` : `glyph-${g}`)];
    if (part) {
      const moving = document.createElementNS(NS, 'g');
      moving.setAttribute('class', `glyph-${part}`);
      moving.append(use(`glyph-${g}--${part}`));
      parts.push(moving);
    }
    if (driven) { parts.push(use('glyph-driven-badge', 'glyph-badge')); }
    svg.replaceChildren(...parts);
  }
  return svg;
}

export const ICON_SIZES = Object.freeze([16, 20, 22, 24]);

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
