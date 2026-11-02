import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { generate, repoRoot } from '../generate.mjs';
import { MOTION_PLATFORMS, MOTION_DURATIONS, MOTION_EASINGS, motion } from '../lib/tokens.mjs';
import { parseXml } from '../lib/svg.mjs';

// Motion: docs/design/ui.md §5.3, design/tokens.json motion, and what the generator makes of it.

// Line endings aside: a Windows checkout may hold any of these with CRLF.
const read = (rel) => readFileSync(path.join(repoRoot, rel), 'utf8').replace(/\r\n/g, '\n');
const tokens = JSON.parse(read('design/tokens.json'));
const entries = Object.keys(tokens.motion.catalogue).filter((k) => !k.startsWith('$'));
const css = read('pwa/design/tokens.css');
const XAML = 'src/AgentBridge.App/Design/DesignResources.xaml';

const scratch = path.join(repoRoot, 'tools', 'design-gen', '.scratch', 'motion');
after(() => rmSync(scratch, { recursive: true, force: true }));

// The generator's inputs, with tokens.json changed by edit.
function generateWith(name, edit) {
  const root = path.join(scratch, name);
  rmSync(root, { recursive: true, force: true });
  cpSync(path.join(repoRoot, 'design'), path.join(root, 'design'), { recursive: true });
  mkdirSync(path.join(root, 'pwa'), { recursive: true });
  for (const f of ['index.html', 'manifest.webmanifest']) { cpSync(path.join(repoRoot, 'pwa', f), path.join(root, 'pwa', f)); }
  const t = structuredClone(tokens);
  edit(t);
  writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify(t));
  return () => generate(root);
}

// The declarations in one block of the CSS, as a Map, comments aside.
function declarations(block) {
  return new Map([...block.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/(--ab-motion-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
}
const rootBlock = css.match(/^:root \{([\s\S]*?)^\}/m)[1];
const reducedBlock = css.match(/@media \(prefers-reduced-motion: reduce\) \{\s*:root \{([\s\S]*?)\}\s*\}/)?.[1] ?? '';
const msOf = (v) => Number(/^(\d+)ms$/.exec(v)?.[1] ?? NaN);

test('every catalogued motion has its duration, easing and distance on every platform', () => {
  assert.ok(entries.length >= 15, 'the catalogue covers ui.md §5.3');
  for (const p of MOTION_PLATFORMS) {
    const m = motion(tokens, p);
    assert.deepEqual(m.durations.map((d) => d.name), MOTION_DURATIONS, p);
    assert.deepEqual(m.easings.map((e) => e.name), MOTION_EASINGS, p);
    assert.deepEqual(m.catalogue.map((c) => c.id), entries, p);
  }
  // Apple moves things on springs: one for every duration an entry moves at.
  const springs = motion(tokens, 'apple').springs.map((s) => s.name);
  const used = new Set(entries.flatMap((id) => [tokens.motion.catalogue[id].duration, tokens.motion.catalogue[id].exit?.duration]).filter(Boolean));
  for (const d of used) { assert.ok(springs.includes(d), `apple has a ${d} spring`); }
});

test('an entry naming a token a platform lacks is refused, not generated without it', () => {
  const run = generateWith('missing', (t) => { delete t.motion.platform.android.easing.decelerate; });
  assert.throws(() => run(), /motion\.platform\.android\.easing\.decelerate/);
  assert.throws(() => motion({ motion: { ...tokens.motion, catalogue: { x: { duration: 'slow', easing: 'standard', reduced: 'instant' } } } }, 'windows'), /no duration "slow"/);
});

test('ui.md\'s motion catalogue and tokens.json\'s list the same motions', () => {
  const section = read('docs/design/ui.md').split('### 5.3 Motion')[1].split(/\n## /)[0];
  const table = section.split('**Catalogue.**')[1].split('\n\n')[1];
  const rows = table.split('\n').slice(2).filter((l) => l.startsWith('|'));
  const named = rows.map((r) => /`(\w+)`:/.exec(r.split('|')[5])?.[1]);
  assert.deepEqual(named, entries);
});

test('nothing but the glyphs runs longer than 300 ms, in the tokens or in either client', () => {
  const limit = tokens.motion.limitMs;
  assert.equal(limit, 300);
  for (const p of MOTION_PLATFORMS) {
    const m = motion(tokens, p);
    for (const d of m.durations) { assert.ok(d.ms <= limit, `${p} ${d.name}`); }
    for (const s of m.springs) { assert.ok(s.response * 1000 <= limit, `${p} spring ${s.name}`); }
  }
  for (const [k, v] of declarations(rootBlock)) {
    if (!/^\d+ms$/.test(v) || /-glyph-\w+-period$/.test(k)) { continue; }
    assert.ok(msOf(v) <= limit, `${k}: ${v}`);
  }
  const spans = [...read(XAML).matchAll(/<x:String x:Key="(AbMotion\w+)">(\d\d):(\d\d):(\d\d)\.(\d{3})<\/x:String>/g)];
  assert.ok(spans.length > 20);
  for (const [, key, h, m, s, frac] of spans) {
    if (/^AbMotionGlyph\w+Period$/.test(key)) { continue; }
    assert.ok(((Number(h) * 60 + Number(m)) * 60 + Number(s)) * 1000 + Number(frac) <= limit, key);
  }
  const run = generateWith('slow', (t) => { t.motion.platform.windows.duration.emphasis = 333; });
  assert.throws(() => run(), /emphasis is 333 ms, past motion\.limitMs/);
});

test('no curve overshoots and no spring bounces', () => {
  for (const p of MOTION_PLATFORMS) {
    for (const e of motion(tokens, p).easings) { assert.ok(e.curve.every((v) => v >= 0 && v <= 1), `${p} ${e.name}`); }
    for (const s of motion(tokens, p).springs) { assert.ok(s.dampingFraction >= 1, `${p} ${s.name}`); }
  }
  assert.throws(generateWith('overshoot', (t) => { t.motion.platform.windows.easing.decelerate = [0.34, 1.56, 0.64, 1]; }), /leaves 0-1/);
  assert.throws(generateWith('bounce', (t) => { t.motion.platform.apple.spring.fast.dampingFraction = 0.7; }), /bounces/);
  assert.throws(generateWith('undamped', (t) => { delete t.motion.platform.apple.spring.fast.dampingFraction; }), /bounces/);
});

test('a duration in fractions of a millisecond is refused, since WinUI\'s TimeSpan form can\'t carry it', () => {
  assert.throws(generateWith('fraction', (t) => { t.motion.platform.windows.duration.fast = 83.5; }), /duration\.fast must be a whole number of ms/);
  assert.throws(generateWith('glyph-fraction', (t) => { t.motion.glyph.working.periodMs = 1200.5; }), /glyph\.working\.periodMs must be a whole number/);
});

test('the PWA\'s reduced-motion block redefines every motion duration and distance: 0, or the cross-fade for an entry that cross-fades', () => {
  const root = declarations(rootBlock);
  const reduced = declarations(reducedBlock);
  const crossFade = root.get('--ab-motion-cross-fade');
  assert.equal(crossFade, '83ms');
  const timed = [...root.keys()].filter((k) => /^\d+(ms|px)$/.test(root.get(k)) && !/-glyph-\w+-period$/.test(k));
  assert.ok(timed.length > 30);
  assert.deepEqual(timed.filter((k) => !reduced.has(k)), [], 'every duration and distance is in the reduced block');
  for (const [k, v] of reduced) {
    if (k === '--ab-motion-travel') { assert.equal(v, '0', 'travel no distance measures stays put'); continue; }
    if (k.endsWith('distance') || k.startsWith('--ab-motion-distance-')) { assert.equal(v, '0px', k); continue; }
    if (k === '--ab-motion-cross-fade') { assert.equal(v, crossFade); continue; }
    const entry = entries.find((id) => k.startsWith(`--ab-motion-${id.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase()}-`));
    const want = entry && tokens.motion.catalogue[entry].reduced === 'crossFade' ? crossFade : '0ms';
    assert.equal(v, want, k);
  }
  assert.equal(reduced.get('--ab-motion-push-duration'), crossFade);
  assert.equal(reduced.get('--ab-motion-reorder-duration'), '0ms');
  assert.equal(root.get('--ab-motion-travel'), '1');
  assert.equal(reduced.get('--ab-motion-sheet-duration'), crossFade, 'the sheet cross-fades, and travel is what keeps it from rising');
});

test('the app\'s motion resources parse, sit outside the theme dictionaries, and read as WinUI\'s TimeSpans and KeySplines', () => {
  const source = read(XAML);
  const xaml = parseXml(source, XAML, { text: true });
  const themes = xaml.children.find((c) => c.tag === 'ResourceDictionary.ThemeDictionaries');
  assert.ok(!JSON.stringify(themes).includes('AbMotion'), 'not theme-specific, so not in a theme dictionary');
  const resources = xaml.children.filter((c) => c.attrs['x:Key']?.startsWith('AbMotion'));
  const keysCs = read('src/AgentBridge.App/Design/DesignKeys.cs');
  const listed = [.../Motion =\s*\[([^\]]*)\]/.exec(keysCs)[1].matchAll(/"(\w+)"/g)].map((m) => m[1]);
  assert.deepEqual(resources.map((r) => r.attrs['x:Key']), listed);
  for (const r of resources) {
    const key = r.attrs['x:Key'];
    assert.equal(r.children.length, 0, key);
    if (/Distance\w*$/.test(key)) {
      assert.equal(r.tag, 'x:Double', key);
      assert.match(r.text, /^\d+(\.\d+)?$/, key);
    } else if (/Ease\w*$/.test(key)) {
      assert.equal(r.tag, 'x:String', key);
      const points = r.text.split(',').map(Number);
      assert.equal(points.length, 4, key);
      assert.ok(points.every((v) => v >= 0 && v <= 1), `${key}: KeySpline points lie within 0-1`);
    } else if (/Reduced$/.test(key)) {
      assert.ok(['CrossFade', 'Instant'].includes(r.text), key);
    } else {
      assert.equal(r.tag, 'x:String', key);
      assert.match(r.text, /^\d\d:\d\d:\d\d\.\d{3}$/, key);
    }
  }
  for (const id of entries) {
    const p = `AbMotion${id.charAt(0).toUpperCase()}${id.slice(1)}`;
    for (const k of ['Duration', 'Ease', 'Reduced']) { assert.ok(listed.includes(`${p}${k}`), `${p}${k}`); }
  }
  assert.match(source, /<x:String x:Key="AbMotionStandard">00:00:00\.167<\/x:String>/);
  assert.match(source, /<x:String x:Key="AbMotionEaseDecelerate">0,0,0,1<\/x:String>/);
});
