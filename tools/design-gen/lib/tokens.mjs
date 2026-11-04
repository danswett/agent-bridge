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

// CSS's generic families and system keywords, which only CSS knows.
const GENERIC = /^(ui-[\w-]+|system-ui|-apple-system|sans-serif|serif|monospace)$/;
// What XAML ends each stack on, past every named family: Windows' own face of that kind.
const XAML_LAST = { sans: 'Segoe UI', mono: 'Consolas' };

// A stack (type.sans or type.mono), as each client spells it. CSS's generic names are CSS's
// alone: XAML gets Windows' own face there instead.
export function fontFamilies(tokens, kind, client) {
  const family = tokens.type[kind].family;
  if (client === 'css') { return family.map((f) => (GENERIC.test(f) ? f : `"${f}"`)).join(', '); }
  // XAML names a bundled font by its file: the first family, if it is ours, is the collection
  // in the app's Assets/Fonts (bundled by AgentBridge.App.csproj), then the rest by name. One
  // collection holds every face, so DirectWrite sees the whole family rather than one file's
  // face, which it would embolden and slant for the rest (#544).
  const own = bundledFont(tokens, kind);
  const named = family.filter((f) => !GENERIC.test(f))
    .map((f) => (own && f === own.family ? `ms-appx:///${own.appDir}/${own.collectionFile}#${f}` : f));
  return [...new Set([...named, XAML_LAST[kind]])].join(', ');
}
export const monoFamilies = (tokens, client) => fontFamilies(tokens, 'mono', client);

export const WEIGHT_NAMES = Object.freeze({ 400: 'Regular', 500: 'Medium', 600: 'SemiBold', 700: 'Bold' });
const faceName = (weight, style) => (weight === 400 && style === 'italic' ? 'Italic' : `${WEIGHT_NAMES[weight]}${style === 'italic' ? 'Italic' : ''}`);

// A bundled family (type.sans or type.mono, with files), or null: its family name, where its
// files are, and its real faces, by weight then style, in the order design/fonts builds them
// into the collection (ttc/<Stem>.ttc), so a face's place in this list is its index there.
// Each face is named as design/fonts names it (SlipgatesMono-SemiBoldItalic). A variable family
// also has its upright and italic files (ttf/ and woff2/<Stem>.* and <Stem>-Italic.*), each
// covering every weight; a static one has a TTF and a WOFF2 per face.
export function bundledFont(tokens, kind) {
  const def = tokens.type[kind];
  if (!def?.files) { return null; }
  const family = def.family[0];
  const stem = family.replace(/\s+/g, '');
  const styles = def.italic ? ['normal', 'italic'] : ['normal'];
  const faces = [];
  for (const weight of def.weights ?? [400]) {
    if (!(weight in WEIGHT_NAMES)) { throw new Error(`type.${kind}.weights: no face name for weight ${weight}`); }
    for (const style of styles) { faces.push({ weight, style, file: `${stem}-${faceName(weight, style)}` }); }
  }
  return {
    kind, family, stem, dir: def.files, faces, variable: Boolean(def.variable),
    variableFiles: def.variable ? styles.map((style) => ({ style, file: style === 'italic' ? `${stem}-Italic` : stem })) : [],
    collection: `ttc/${stem}.ttc`, collectionFile: `${stem}.ttc`,
    appDir: 'Assets/Fonts', license: 'OFL.txt', bundledLicense: `${stem}-OFL.txt`,
  };
}
export const monoFont = (tokens) => bundledFont(tokens, 'mono');
export const sansFont = (tokens) => bundledFont(tokens, 'sans');
export const bundledFonts = (tokens) => ['sans', 'mono'].map((k) => bundledFont(tokens, k)).filter(Boolean);

// The weights and styles the ramps ask for (type.weights, type.styles), as numbers: what every
// bundled family must hold as real faces, so no client fakes one (type.synthesis).
const RAMP_WEIGHTS = { light: 300, regular: 400, medium: 500, semibold: 600, bold: 700, heavy: 800 };
export function rampFaces(tokens) {
  const weights = (tokens.type.weights ?? ['regular']).map((w) => {
    if (!(w in RAMP_WEIGHTS)) { throw new Error(`type.weights: "${w}" is not one of ${Object.keys(RAMP_WEIGHTS).join(', ')}`); }
    return RAMP_WEIGHTS[w];
  });
  return weights.flatMap((weight) => (tokens.type.styles ?? ['normal']).map((style) => ({ weight, style })));
}

// No synthesized styles (ui.md §4.2): every face the ramps use is one each bundled family has.
export function checkNoSynthesis(tokens) {
  for (const font of bundledFonts(tokens)) {
    const lacking = rampFaces(tokens).filter((r) => !font.faces.some((f) => f.weight === r.weight && f.style === r.style));
    if (lacking.length) { throw new Error(`type.${font.kind} lacks ${lacking.map((r) => `${r.weight} ${r.style}`).join(', ')}, which the ramps use: a platform would synthesize it`); }
  }
}

export function radii(tokens) {
  return Object.entries(tokens.radius.windows).map(([name, v]) => ({ name, value: v, css: `--ab-radius-${kebab(name)}`, xaml: `AbRadius${pascal(name)}` }));
}

// --- motion (docs/design/ui.md §5.3) ---

export const MOTION_PLATFORMS = ['windows', 'pwa', 'apple', 'android'];
export const MOTION_DURATIONS = ['instant', 'fast', 'standard', 'emphasis', 'crossFade'];
export const MOTION_EASINGS = ['standard', 'decelerate', 'accelerate'];
const REDUCED = ['crossFade', 'instant'];

function motionPlatform(tokens, name) {
  const all = tokens.motion.platform;
  let def = all[name];
  const seen = [name];
  while (def?.inherits) {
    if (seen.includes(def.inherits)) { throw new Error(`motion.platform.${name} inherits in a circle`); }
    seen.push(def.inherits);
    def = all[def.inherits];
  }
  if (!def) { throw new Error(`motion.platform.${name} is missing`); }
  return def;
}

// One platform's motion, every name resolved and checked: { limitMs, glyphs, durations, easings,
// distances, springs, catalogue }. A duration past the limit, a curve that overshoots, a spring
// that bounces or an entry naming a token the platform lacks is refused, so no client is generated
// a motion the spec rules out.
export function motion(tokens, platform) {
  const m = tokens.motion;
  const where = `motion.platform.${platform}`;
  const def = motionPlatform(tokens, platform);
  const limit = m.limitMs;
  // Without a limit every comparison below is false (333 > undefined), and nothing is refused.
  if (!Number.isInteger(limit) || limit <= 0) { throw new Error('motion.limitMs must be a positive whole number of ms'); }
  const durations = MOTION_DURATIONS.map((name) => {
    const ms = def.duration?.[name];
    // Whole milliseconds: WinUI's TimeSpan form has one fractional part, and CSS needs no more.
    if (!Number.isInteger(ms) || ms < 0) { throw new Error(`${where}.duration.${name} must be a whole number of ms`); }
    if (ms > limit) { throw new Error(`${where}.duration.${name} is ${ms} ms, past motion.limitMs (${limit})`); }
    return { name, ms };
  });
  const easings = MOTION_EASINGS.map((name) => {
    const curve = def.easing?.[name];
    if (!Array.isArray(curve) || curve.length !== 4 || !curve.every((v) => typeof v === 'number')) { throw new Error(`${where}.easing.${name} needs four numbers`); }
    // x outside 0-1 is no curve at all (and WinUI's KeySpline refuses it); y outside it overshoots.
    if (!curve.every((v) => v >= 0 && v <= 1)) { throw new Error(`${where}.easing.${name} leaves 0-1: no overshoot`); }
    return { name, curve };
  });
  const springs = Object.entries(def.spring ?? {}).filter(([k]) => !k.startsWith('$')).map(([name, s]) => {
    if (!(s.response > 0) || s.response * 1000 > limit) { throw new Error(`${where}.spring.${name}: response must be above 0 and within motion.limitMs`); }
    // A missing damping fraction is SwiftUI's default, which bounces; undefined < 1 is false.
    if (!Number.isFinite(s.dampingFraction) || s.dampingFraction < 1) { throw new Error(`${where}.spring.${name} bounces: dampingFraction must be at least 1`); }
    return { name, ...s };
  });
  const distances = Object.entries(m.distance).map(([name, px]) => ({ name, px }));
  const pick = (list, name, what) => {
    const found = list.find((x) => x.name === name);
    if (!found) { throw new Error(`motion.catalogue: no ${what} "${name}" on ${platform}`); }
    return found;
  };
  const catalogue = Object.entries(m.catalogue).filter(([k]) => !k.startsWith('$')).map(([id, e]) => {
    if (!REDUCED.includes(e.reduced)) { throw new Error(`motion.catalogue.${id}.reduced must be ${REDUCED.join(' or ')}`); }
    return {
      id,
      duration: pick(durations, e.duration, 'duration'),
      easing: pick(easings, e.easing, 'easing'),
      distance: e.distance === undefined ? null : pick(distances, e.distance, 'distance'),
      exit: e.exit ? { duration: pick(durations, e.exit.duration, 'duration'), easing: pick(easings, e.exit.easing, 'easing') } : null,
      reduced: e.reduced,
    };
  });
  const glyphs = Object.entries(m.glyph).filter(([, g]) => typeof g === 'object').map(([name, g]) => {
    if (!Number.isInteger(g.periodMs) || g.periodMs <= 0) { throw new Error(`motion.glyph.${name}.periodMs must be a whole number of ms`); }
    return { name, periodMs: g.periodMs };
  });
  return { limitMs: limit, glyphs, durations, easings, springs, distances, catalogue, crossFade: pick(durations, 'crossFade', 'duration') };
}

// How a token's name is spelled in each client: listItem is --ab-motion-list-item and AbMotionListItem.
export { kebab as cssCase, pascal as xamlCase };

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

// The Windows type ramp (type.roles.windows, ui.md §4.3): "20/28 semibold" is size 20, line
// height 28, semibold.
export function typeRamp(tokens) {
  const win = tokens.type.roles.windows;
  return Object.entries(win).filter(([k]) => k !== 'family').map(([role, spec]) => {
    const m = /^(\d+)\/(\d+)(?: (semibold))?$/.exec(spec);
    if (!m) { throw new Error(`type.roles.windows.${role}: "${spec}" is not size/line-height [semibold]`); }
    return { role, size: Number(m[1]), lineHeight: Number(m[2]), semibold: Boolean(m[3]), xaml: `AbType${pascal(role)}` };
  });
}

// A face's x-height as a fraction of its em, from a TrueType or OpenType file's OS/2 table
// (sxHeight, version 2 and later) and its head table (unitsPerEm).
export function fontXHeight(bytes) {
  const b = Buffer.from(bytes);
  const at = b.toString('latin1', 0, 4) === 'ttcf' ? b.readUInt32BE(12) : 0;
  const tables = {};
  for (let i = 0, n = b.readUInt16BE(at + 4); i < n; i++) {
    const r = at + 12 + i * 16;
    tables[b.toString('latin1', r, r + 4)] = b.readUInt32BE(r + 8);
  }
  if (tables.head === undefined || tables['OS/2'] === undefined) { throw new Error('no head or OS/2 table'); }
  const os2 = tables['OS/2'];
  if (b.readUInt16BE(os2) < 2) { throw new Error('OS/2 table older than version 2: no sxHeight'); }
  return { xHeight: b.readInt16BE(os2 + 86), unitsPerEm: b.readUInt16BE(tables.head + 18) };
}

const round = (v, places = 4) => Math.round(v * 10 ** places) / 10 ** places;
const ratio = (face, where) => {
  if (!(face?.xHeight > 0) || !(face?.unitsPerEm > 0)) { throw new Error(`${where} needs xHeight and unitsPerEm`); }
  return face.xHeight / face.unitsPerEm;
};

// Inline mono (docs/design/ui.md §4.2, type.inlineMono): per platform, the text face's x-height
// ratio (x, what CSS font-size-adjust is given) and the scale mono takes beside it (text / mono),
// so the two faces' x-heights match at any size: { mono, min, platforms: [{ name, face, x, scale }] }.
export function inlineMono(tokens) {
  const im = tokens.type.inlineMono;
  if (!im) { throw new Error('type.inlineMono is missing'); }
  const mono = ratio(im.mono, 'type.inlineMono.mono');
  if (!im.text?.windows) { throw new Error('type.inlineMono.text needs windows, the default'); }
  const platforms = Object.entries(im.text).map(([name, t]) => {
    const x = ratio(t, `type.inlineMono.text.${name}`);
    return { name, face: t.face, x: round(x), scale: round(x / mono) };
  });
  return { mono: round(mono), min: im.minSize ?? 11, platforms };
}

// The inline mono size for each role of a platform's ramp (type.roles.<platform>, "14/20" and
// the like): the role's size times the platform's scale, two places, never under the minimum.
// Code has its own size, and a title holds no mono.
export function inlineMonoSizes(tokens, platform = 'windows') {
  const { min, platforms } = inlineMono(tokens);
  const p = platforms.find((x) => x.name === platform);
  if (!p) { throw new Error(`type.inlineMono.text has no ${platform}`); }
  const ramp = tokens.type.roles[platform];
  return ['row', 'reading', 'meta', 'sectionHeader'].map((role) => {
    const size = Number(/^(\d+(?:\.\d+)?)\//.exec(ramp?.[role] ?? '')?.[1]);
    if (!Number.isFinite(size)) { throw new Error(`type.roles.${platform}.${role} has no size`); }
    return { role, size, value: Math.max(min, round(size * p.scale, 2)), xaml: `AbInlineMono${pascal(role)}` };
  });
}
