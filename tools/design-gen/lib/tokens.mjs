// design/tokens.json as the generator and its tests read it: every colour token once, with its
// dark, light and high-contrast value, named for each client.

import { readFileSync } from 'node:fs';
import path from 'node:path';
import { parseColor } from './color.mjs';

export function loadTokens(designDir) {
  return JSON.parse(readFileSync(path.join(designDir, 'tokens.json'), 'utf8'));
}

const pascal = (s) => s.charAt(0).toUpperCase() + s.slice(1);
const kebab = (s) => s.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();

// System colours a high-contrast mapping may name, for each client. CSS's are the
// forced-colors keywords (WindowText and Window are deprecated there, so CanvasText and
// Canvas); XAML's are the SystemColor*Color theme resources WinUI defines in high contrast.
export const SYSTEM_COLORS = {
  Highlight: { css: 'Highlight', xaml: 'SystemColorHighlightColor' },
  HighlightText: { css: 'HighlightText', xaml: 'SystemColorHighlightTextColor' },
  WindowText: { css: 'CanvasText', xaml: 'SystemColorWindowTextColor' },
  Window: { css: 'Canvas', xaml: 'SystemColorWindowColor' },
  GrayText: { css: 'GrayText', xaml: 'SystemColorGrayTextColor' },
  ButtonFace: { css: 'ButtonFace', xaml: 'SystemColorButtonFaceColor' },
  ButtonText: { css: 'ButtonText', xaml: 'SystemColorButtonTextColor' },
  Hotlight: { css: 'LinkText', xaml: 'SystemColorHotlightColor' },
};

function system(name, where) {
  if (!(name in SYSTEM_COLORS)) { throw new Error(`${where}: "${name}" is not a system colour the generator knows (${Object.keys(SYSTEM_COLORS).join(', ')})`); }
  return name;
}

// Every colour role and its variants: { role, variant ('' for the role itself), dark, light,
// hc, css, xaml }. A variant is a key pair like textDark/textLight; onDark/onLight is 'on'.
export function roleTokens(tokens) {
  const out = [];
  const hcVariants = tokens.color.hcVariants ?? {};
  for (const [role, def] of Object.entries(tokens.color.role)) {
    const where = `color.role.${role}`;
    if (!def.dark || !def.light) { throw new Error(`${where} needs dark and light`); }
    const roleHc = system(def.hc, `${where}.hc`);
    const variants = new Set();
    for (const k of Object.keys(def)) {
      const m = /^(\w+?)(Dark|Light)$/.exec(k);
      if (m) { variants.add(m[1]); }
    }
    for (const variant of ['', ...[...variants].sort()]) {
      const dark = variant ? def[`${variant}Dark`] : def.dark;
      const light = variant ? def[`${variant}Light`] : def.light;
      if (dark === undefined || light === undefined) { throw new Error(`${where}: ${variant}Dark and ${variant}Light come as a pair`); }
      parseColor(dark); parseColor(light);
      let hc = roleHc;
      if (variant) {
        const rule = hcVariants[variant];
        if (!rule) { throw new Error(`color.hcVariants has no high-contrast rule for ${role}'s "${variant}"`); }
        hc = rule === 'role' ? roleHc : system(rule, `color.hcVariants.${variant}`);
      }
      out.push({
        role, variant, dark, light, hc,
        css: `--ab-${kebab(role)}${variant ? `-${kebab(variant)}` : ''}`,
        xaml: `Ab${pascal(role)}${pascal(variant)}`,
      });
    }
  }
  return out;
}

// The Windows surfaces, which the PWA inherits: { name, dark, light, hc, css, xaml }.
export function surfaceTokens(tokens) {
  const win = tokens.surface.windows;
  if (tokens.surface.pwa?.inherits !== 'windows') { throw new Error('surface.pwa is expected to inherit windows'); }
  const names = Object.keys(win.dark);
  for (const n of Object.keys(win.light)) { if (!names.includes(n)) { throw new Error(`surface.windows.light.${n} has no dark value`); } }
  return names.map((name) => {
    const [dark, light] = [win.dark[name], win.light[name]];
    if (light === undefined) { throw new Error(`surface.windows.dark.${name} has no light value`); }
    if (!win.hc?.[name]) { throw new Error(`surface.windows.hc.${name} is missing: every surface needs a high-contrast mapping`); }
    parseColor(dark); parseColor(light);
    return { name, dark, light, hc: system(win.hc[name], `surface.windows.hc.${name}`), css: `--ab-${kebab(name)}`, xaml: `Ab${pascal(name)}` };
  });
}

export const allColorTokens = (tokens) => [...roleTokens(tokens), ...surfaceTokens(tokens)];

// The monospace stack, as each client spells it. CSS's generic names are CSS's alone: XAML has
// no ui-monospace, so it gets Windows' own monospace there instead.
export function monoFamilies(tokens, client) {
  const family = tokens.type.mono.family;
  if (client === 'css') { return family.map((f) => (/^(ui-)?monospace$/.test(f) ? f : `"${f}"`)).join(', '); }
  // XAML names a bundled font by its file: the first family, if it is ours, comes from the
  // app's Assets/Fonts (bundled by AgentBridge.App.csproj), then the rest by name.
  const own = monoFont(tokens);
  const named = family.filter((f) => !/^(ui-)?monospace$/.test(f))
    .map((f) => (own && f === own.family ? `ms-appx:///${own.appDir}/${own.faces[0].file}.ttf#${f}` : f));
  return [...named, 'Consolas'].join(', ');
}

const WEIGHT_NAMES = { 400: 'Regular', 600: 'SemiBold', 700: 'Bold' };

// The bundled monospace (type.mono.files), if there is one: its family, where its files are,
// and one face per weight and style, named as design/fonts builds them
// (AgentBridgeMono-SemiBoldItalic). The Regular face comes first.
export function monoFont(tokens) {
  const mono = tokens.type.mono;
  if (!mono.files) { return null; }
  const family = mono.family[0];
  const stem = family.replace(/\s+/g, '');
  const faces = [];
  for (const style of mono.italic ? ['normal', 'italic'] : ['normal']) {
    for (const weight of mono.weights ?? [400]) {
      if (!(weight in WEIGHT_NAMES)) { throw new Error(`type.mono.weights: no file name for weight ${weight}`); }
      const name = weight === 400 && style === 'italic' ? 'Italic' : `${WEIGHT_NAMES[weight]}${style === 'italic' ? 'Italic' : ''}`;
      faces.push({ weight, style, file: `${stem}-${name}` });
    }
  }
  return { family, dir: mono.files, faces, appDir: 'Assets/Fonts', license: 'OFL.txt' };
}

export function radii(tokens) {
  return Object.entries(tokens.radius.windows).map(([name, v]) => ({ name, value: v, css: `--ab-radius-${kebab(name)}`, xaml: `AbRadius${pascal(name)}` }));
}

export function spaces(tokens) {
  const unit = tokens.space.unit;
  return tokens.space.scale.filter((v) => v > 0).map((v) => {
    if (v % unit) { throw new Error(`space.scale ${v} is not a multiple of the ${unit} px unit`); }
    return { value: v, css: `--ab-space-${v / unit}`, xaml: `AbSpace${v / unit}` };
  });
}

const SHADES = ['light3', 'light2', 'light1', 'base', 'dark1', 'dark2', 'dark3'];

// WinUI's accent palette (color.accent.windows): { shade, key, value } for SystemAccentColor and
// each shade, light2 and dark1 being the bridge role's dark and light colours.
export function accentShades(tokens) {
  const own = tokens.color.accent?.windows;
  if (!own) { return []; }
  const bridge = tokens.color.role.bridge;
  const all = { ...own, light2: bridge.dark, dark1: bridge.light };
  for (const k of Object.keys(own)) {
    if (!SHADES.includes(k) || k === 'light2' || k === 'dark1') { throw new Error(`color.accent.windows.${k}: expected ${SHADES.filter((s) => s !== 'light2' && s !== 'dark1').join(', ')} (light2 and dark1 are the bridge role's)`); }
  }
  return SHADES.map((shade) => {
    if (all[shade] === undefined) { throw new Error(`color.accent.windows lacks ${shade}`); }
    parseColor(all[shade]);
    const suffix = shade === 'base' ? '' : pascal(shade);
    return { shade, key: `SystemAccentColor${suffix}`, value: all[shade] };
  });
}

// The hyperlink brushes WinUI's text and HyperlinkButton read, as theme brushes like the roles':
// { name, xaml, keys, dark, light, hc }. keys: every WinUI resource the brush stands for.
export function hyperlinkTokens(tokens) {
  const link = tokens.color.accent?.hyperlink;
  if (!link) { return []; }
  const shades = Object.fromEntries(accentShades(tokens).map((s) => [s.shade, s.value]));
  const pick = (state, theme) => {
    const name = link[state]?.[theme];
    if (name === 'bridge') { return tokens.color.role.bridge[theme]; }
    if (!(name in shades)) { throw new Error(`color.accent.hyperlink.${state}.${theme}: "${name}" is neither bridge nor a shade`); }
    return shades[name];
  };
  const hc = system(link.hc, 'color.accent.hyperlink.hc');
  // SystemControlHyperlinkTextBrush is what a Hyperlink in a TextBlock or RichTextBlock draws in.
  return [
    { name: 'link', state: 'rest', keys: ['SystemControlHyperlinkTextBrush', 'HyperlinkButtonForeground'] },
    { name: 'linkPointerOver', state: 'pointerOver', keys: ['HyperlinkButtonForegroundPointerOver'] },
    { name: 'linkPressed', state: 'pressed', keys: ['HyperlinkButtonForegroundPressed'] },
  ].map((l) => ({ ...l, dark: pick(l.state, 'dark'), light: pick(l.state, 'light'), hc }));
}

// The Windows app's sizes (layout.breakpoints and layout.windows): { name, value, xaml, cs }.
export function layoutSizes(tokens) {
  const out = [];
  for (const [name, v] of Object.entries(tokens.layout.breakpoints)) { out.push({ name, value: v }); }
  for (const [name, v] of Object.entries(tokens.layout.windows ?? {})) {
    if (name.startsWith('$')) { continue; }
    out.push({ name, value: v });
  }
  for (const s of out) {
    if (!Number.isInteger(s.value) || s.value <= 0) { throw new Error(`layout ${s.name}: ${s.value} is not a size in px`); }
    s.xaml = `AbLayout${pascal(s.name)}`;
    s.cs = pascal(s.name);
  }
  return out;
}

// The Windows type ramp (type.roles.windows, ui.md §4.3): "20/28 semibold (Display optical size)"
// is size 20, line height 28, semibold, in the Display optical size.
export function typeRamp(tokens) {
  const win = tokens.type.roles.windows;
  return Object.entries(win).filter(([k]) => k !== 'family').map(([role, spec]) => {
    const m = /^(\d+)\/(\d+)(?: (semibold))?(?: \((Display) optical size\))?$/.exec(spec);
    if (!m) { throw new Error(`type.roles.windows.${role}: "${spec}" is not size/line-height [semibold] [(Display optical size)]`); }
    return { role, size: Number(m[1]), lineHeight: Number(m[2]), semibold: Boolean(m[3]), display: Boolean(m[4]), xaml: `AbType${pascal(role)}` };
  });
}
