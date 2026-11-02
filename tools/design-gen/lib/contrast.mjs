// The contrast rules of design/tokens.json (color.contrast; docs/design/ui.md §3, §9), as a
// list of every pair they cover in dark and light, each with its measured ratio.

import { contrast, over, parseColor } from './color.mjs';
import { roleTokens, surfaceTokens } from './tokens.mjs';

export function contrastPairs(tokens) {
  const rules = tokens.color.contrast;
  const notOnFills = new Set(rules.notOnFills ?? []);
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
    // A role's fill is a surface of its own (the question card), over each of the others.
    const fills = roles.filter((r) => r.variant === 'fill')
      .flatMap((f) => bgs.map((b) => ({ name: `${f.role}.fill on ${b.name}`, colour: over(f[theme], b.colour) })));
    // A tint (inline code, a read-only field, a machine row, a badge) over the surfaces it sits
    // on, measured only with the words color.contrast.tints lets on it.
    const tintRules = rules.tints ?? {};
    const tints = Object.keys(tintRules).filter((k) => k !== 'over' && !k.startsWith('$')).flatMap((name) => {
      const t = surfaces.find((x) => x.name === name);
      if (!t) { throw new Error(`color.contrast.tints names ${name}, which surface.windows lacks`); }
      return bgs.filter((b) => tintRules.over.includes(b.name))
        .map((b) => ({ name: `${name} on ${b.name}`, colour: over(t[theme], b.colour), allows: tintRules[name] }));
    });
    const add = (fg, fgName, bg, bgName, kind) => out.push({
      theme, fg: fgName, bg: bgName, kind, min: rules[kind], ratio: contrast(fg, bg),
    });
    const everywhere = (fg, fgName, kind) => {
      for (const b of [...bgs, ...(notOnFills.has(fgName) ? [] : fills), ...tints.filter((t) => t.allows.includes(fgName))]) {
        add(fg, fgName, b.colour, b.name, kind);
      }
    };
    for (const role of new Set(roles.map((r) => r.role))) {
      const own = roles.filter((r) => r.role === role);
      const base = own.find((r) => r.variant === '');
      everywhere(base[theme], role, 'graphic');
      if (rules.roleText.includes(role)) { everywhere(base[theme], role, 'text'); }
      for (const v of own.filter((r) => /^(text|head)/.test(r.variant))) { everywhere(v[theme], `${role}.${v.variant}`, 'text'); }
      for (const v of own.filter((r) => r.variant.startsWith('on'))) { add(v[theme], `${role}.${v.variant}`, base[theme], role, 'text'); }
      for (const v of own.filter((r) => r.variant.startsWith('ink'))) { add(v[theme], `${role}.${v.variant}`, base[theme], role, 'graphic'); }
    }
    for (const t of surfaces.filter((s) => /^text\d*$/.test(s.name))) { everywhere(t[theme], `surface.${t.name}`, 'text'); }
  }
  return out;
}

export const failures = (pairs) => pairs.filter((p) => p.ratio < p.min);

export const describe = (p) => `${p.theme} ${p.fg} on ${p.bg} as ${p.kind}: ${p.ratio.toFixed(2)}:1, needs ${p.min}:1`;
