// The contrast rules of design/tokens.json (color.contrast; docs/design/ui.md §3, §9), as a
// list of every pair they cover in dark and light, each with its measured ratio.

import { contrast, over, parseColor } from './color.mjs';
import { roleTokens, surfaceTokens } from './tokens.mjs';

export function contrastPairs(tokens) {
  const rules = tokens.color.contrast;
  const roles = roleTokens(tokens);
  const surfaces = surfaceTokens(tokens);
  const out = [];
  for (const theme of ['dark', 'light']) {
    const bgs = rules.surfaces.map((name) => {
      const s = surfaces.find((x) => x.name === name);
      if (!s) { throw new Error(`color.contrast.surfaces names ${name}, which surface.windows lacks`); }
      if (parseColor(s[theme]).a !== 1) { throw new Error(`surface ${name} must be opaque to be measured against`); }
      return { name, colour: s[theme] };
    });
    const add = (fg, fgName, bg, bgName, kind) => out.push({
      theme, fg: fgName, bg: bgName, kind, min: rules[kind], ratio: contrast(fg, bg),
    });
    for (const role of new Set(roles.map((r) => r.role))) {
      const own = roles.filter((r) => r.role === role);
      const base = own.find((r) => r.variant === '');
      // A role's fill is a surface of its own for the role and its text: the question card.
      const fills = own.filter((r) => r.variant === 'fill').flatMap((f) => bgs.map((b) => ({ name: `${role}.fill on ${b.name}`, colour: over(f[theme], b.colour) })));
      const under = [...bgs, ...fills];
      for (const b of under) {
        add(base[theme], role, b.colour, b.name, 'graphic');
        if (rules.roleText.includes(role)) { add(base[theme], role, b.colour, b.name, 'text'); }
      }
      for (const v of own.filter((r) => /^(text|head)/.test(r.variant))) {
        for (const b of under) { add(v[theme], `${role}.${v.variant}`, b.colour, b.name, 'text'); }
      }
      for (const v of own.filter((r) => r.variant.startsWith('on'))) { add(v[theme], `${role}.${v.variant}`, base[theme], role, 'text'); }
      for (const v of own.filter((r) => r.variant.startsWith('ink'))) { add(v[theme], `${role}.${v.variant}`, base[theme], role, 'graphic'); }
    }
    for (const t of surfaces.filter((s) => /^text\d*$/.test(s.name))) {
      for (const b of bgs) { add(t[theme], `surface.${t.name}`, b.colour, b.name, 'text'); }
    }
  }
  return out;
}

export const failures = (pairs) => pairs.filter((p) => p.ratio < p.min);

export const describe = (p) => `${p.theme} ${p.fg} on ${p.bg} as ${p.kind}: ${p.ratio.toFixed(2)}:1, needs ${p.min}:1`;
