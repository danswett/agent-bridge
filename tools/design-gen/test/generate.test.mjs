import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { generate, staleFiles, write, repoRoot } from '../generate.mjs';
import { parseXml, parsePathData, formatCommands } from '../lib/svg.mjs';

const scratch = path.join(repoRoot, 'tools', 'design-gen', '.scratch', 'generate');
after(() => rmSync(scratch, { recursive: true, force: true }));

// What the generator reads, in a copy it may change.
function copy(name) {
  const root = path.join(scratch, name);
  rmSync(root, { recursive: true, force: true });
  cpSync(path.join(repoRoot, 'design'), path.join(root, 'design'), { recursive: true });
  mkdirSync(path.join(root, 'pwa'), { recursive: true });
  for (const f of ['index.html', 'manifest.webmanifest']) { cpSync(path.join(repoRoot, 'pwa', f), path.join(root, 'pwa', f)); }
  write(root);
  return root;
}

const XAML = 'src/AgentBridge.App/Design/DesignResources.xaml';
const read = (root, rel) => readFileSync(path.join(root, rel), 'utf8');

test('every generated file in the repository matches design/', () => {
  assert.deepEqual(staleFiles(), [], 'run node tools/design-gen/generate.mjs and commit the result');
});

test('a hand-edited generated file is reported stale, and generating again puts it back', () => {
  const root = copy('stale');
  assert.deepEqual(staleFiles(root), []);
  writeFileSync(path.join(root, 'pwa/design/tokens.css'), read(root, 'pwa/design/tokens.css').replace('--ab-bridge: #0B7A6E;', '--ab-bridge: #0969DA;'));
  writeFileSync(path.join(root, XAML), `${read(root, XAML)}\n`);
  assert.deepEqual(staleFiles(root), ['pwa/design/tokens.css', XAML]);
  write(root);
  assert.deepEqual(staleFiles(root), []);
});

test('a file checked out with Windows line endings is not stale', () => {
  const root = copy('crlf');
  writeFileSync(path.join(root, XAML), read(root, XAML).replace(/\n/g, '\r\n'));
  assert.deepEqual(staleFiles(root), []);
});

test('a changed token reaches the PWA\'s CSS, the app\'s XAML and accent palette, and the PWA\'s browser chrome', () => {
  const root = copy('token');
  const tokens = JSON.parse(read(root, 'design/tokens.json'));
  tokens.color.role.bridge.light = '#0A6B60';
  tokens.surface.windows.dark.shell = '#1C1F22';
  writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify(tokens));
  // DesignKeys.cs too: light bridge is WinUI's SystemAccentColorDark1, which the self-test checks.
  assert.deepEqual(staleFiles(root).sort(), ['pwa/design/tokens.css', 'pwa/index.html', 'pwa/manifest.webmanifest', XAML, 'src/AgentBridge.App/Design/DesignKeys.cs'].sort());
  const out = generate(root);
  assert.match(out['pwa/design/tokens.css'], /^ {2}--ab-bridge: #0A6B60;$/m);
  assert.match(out[XAML], /<ResourceDictionary x:Key="Light">[\s\S]*?<SolidColorBrush x:Key="AbBridge" Color="#0A6B60" \/>/);
  assert.match(out[XAML], /<Color x:Key="SystemAccentColorDark1">#0A6B60<\/Color>/);
  assert.match(out['src/AgentBridge.App/Design/DesignKeys.cs'], /\("SystemAccentColorDark1", "#0A6B60"\)/);
  assert.match(out['pwa/index.html'], /content="#1C1F22" media="\(prefers-color-scheme: dark\)"/);
  assert.match(out['pwa/manifest.webmanifest'], /"theme_color": "#1C1F22"/);
});

test('a surface without a high-contrast mapping is refused rather than left to the default theme', () => {
  const root = copy('nohc');
  const tokens = JSON.parse(read(root, 'design/tokens.json'));
  delete tokens.surface.windows.hc.card;
  writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify(tokens));
  assert.throws(() => generate(root), /surface\.windows\.hc\.card is missing/);
});

// --- the app's resources, read as the XAML they are ---

const xaml = parseXml(read(repoRoot, XAML), XAML, { text: true });
const themes = Object.fromEntries(xaml.children.find((c) => c.tag === 'ResourceDictionary.ThemeDictionaries').children.map((d) => [d.attrs['x:Key'], d.children]));
const keysCs = read(repoRoot, 'src/AgentBridge.App/Design/DesignKeys.cs');
const listed = (name) => [...new RegExp(`${name} =\\s*\\[([^\\]]*)\\]`).exec(keysCs)[1].matchAll(/"([\w./-]+)"/g)].map((m) => m[1]);

test('the app\'s Light, Dark and HighContrast dictionaries each define every brush DesignKeys lists', () => {
  assert.deepEqual(Object.keys(themes), ['Light', 'Dark', 'HighContrast']);
  for (const [theme, brushes] of Object.entries(themes)) {
    assert.deepEqual(brushes.map((b) => b.attrs['x:Key']), listed('Brushes'), theme);
  }
  for (const b of themes.HighContrast) {
    assert.match(b.attrs.Color, /^\{ThemeResource SystemColor\w+Color\}$/, `${b.attrs['x:Key']} is a system colour in high contrast`);
  }
});

test('every glyph and icon is a style whose template draws only with brushes the dictionaries define', () => {
  const styles = xaml.children.filter((c) => c.tag === 'Style' && c.attrs.TargetType === 'ContentControl');
  assert.deepEqual(styles.map((s) => s.attrs['x:Key']), [...listed('Glyphs'), ...listed('Icons')]);
  const brushes = new Set(listed('Brushes'));
  const source = read(repoRoot, XAML).replace(/<!--[\s\S]*?-->/g, '');
  for (const [, key] of source.matchAll(/\{ThemeResource (Ab\w+)\}/g)) { assert.ok(brushes.has(key), `${key} is defined in every theme`); }
  assert.ok(!/\{StaticResource /.test(source));
});

test('every path is markup WinUI reads: absolute M, L, C, A and Z, nonzero fill, explicit separators', () => {
  const paths = [...read(repoRoot, XAML).matchAll(/ Data="([^"]*)"/g)].map((m) => m[1]);
  assert.ok(paths.length > 40);
  for (const d of paths) {
    assert.match(d, /^F1 M/);
    const body = d.slice(3);
    assert.match(body, /^[MLCAZ0-9., -]+$/, d);
    assert.equal(formatCommands(parsePathData(body)), body, 'reads back as written');
  }
});

test('the sprite has a symbol for every glyph and icon, and each moving part on its own', () => {
  const sprite = parseXml(read(repoRoot, 'pwa/design/sprite.svg'), 'sprite.svg');
  const ids = sprite.children.map((s) => s.attrs.id);
  for (const id of ['glyph-needs', 'glyph-working', 'glyph-working--still', 'glyph-working--spin', 'glyph-delegating--orbit', 'glyph-driven-badge', 'icon-send', 'icon-mark']) {
    assert.ok(ids.includes(id), id);
  }
  const needs = read(repoRoot, 'pwa/design/sprite.svg').match(/<symbol id="glyph-needs"[\s\S]*?<\/symbol>/)[0];
  assert.ok(!/#[0-9A-Fa-f]{6}/.test(needs), 'the ink comes from --ab-glyph-ink, not the file');
  assert.match(needs, /var\(--ab-glyph-ink\)/);
});

test('the PWA gets an @font-face with font-display swap for every face of the monospace, then the fallback stack', () => {
  const css = read(repoRoot, 'pwa/design/tokens.css');
  const faces = [...css.matchAll(/@font-face \{([^}]*)\}/g)].map((m) => m[1]);
  assert.equal(faces.length, 6, 'regular, semibold and bold, each with an italic');
  for (const f of faces) {
    assert.match(f, /font-family: "Agent Bridge Mono";/);
    assert.match(f, /font-display: swap;/);
  }
  assert.ok(faces.some((f) => /SemiBoldItalic\.woff2/.test(f) && /font-weight: 600;/.test(f) && /font-style: italic;/.test(f)));
  assert.match(css, /--ab-font-mono: "Agent Bridge Mono", "Cascadia Mono", "SF Mono", "Roboto Mono", ui-monospace, monospace;/);
});

test('the app\'s MonoFontFamily is the bundled TTF, then the fallback stack, and DesignKeys lists the files to bundle', () => {
  assert.match(read(repoRoot, XAML), /<FontFamily x:Key="MonoFontFamily">ms-appx:\/\/\/Assets\/Fonts\/AgentBridgeMono-Regular\.ttf#Agent Bridge Mono, Cascadia Mono, SF Mono, Roboto Mono, Consolas<\/FontFamily>/);
  const files = listed('FontFiles');
  assert.deepEqual(files.filter((f) => f.endsWith('.ttf')).length, 6);
  assert.ok(files.includes('Assets/Fonts/OFL.txt'));
});

test('a face the tokens promise but design/fonts lacks is refused', () => {
  const root = copy('nofont');
  rmSync(path.join(root, 'design/fonts/agent-bridge-mono/woff2/AgentBridgeMono-BoldItalic.woff2'));
  assert.throws(() => generate(root), /lacks woff2\/AgentBridgeMono-BoldItalic\.woff2/);
});

test('a mitred corner bevels in XAML where SVG bevels it, rather than at WinUI\'s limit of 10', () => {
  // M4 14L8 2L9 14 has a miter ratio of about 4.97: SVG's limit of 4 bevels its apex.
  const root = copy('miter');
  writeFileSync(path.join(root, 'design/glyphs/zz-spike.svg'), '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><path d="M4 14L8 2L9 14" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M2 2L14 2" stroke="currentColor" stroke-linejoin="round"/></svg>');
  const xaml = generate(root)[XAML];
  const spike = /<Style x:Key="AbGlyphZzSpike"[\s\S]*?<\/Style>/.exec(xaml)?.[0] ?? '';
  assert.match(spike, /Data="F1 M4,14 L8,2 L9,14"[^>]*StrokeMiterLimit="4"/);
  assert.match(spike, /Data="F1 M2,2 L14,2"[^>]*StrokeLineJoin="Round"/);
  assert.doesNotMatch(spike.split('M2,2')[1], /StrokeMiterLimit/);
});

// --- WinUI's accent, the type ramp and the layout sizes (#180) ---

test('WinUI\'s accent palette is Bridge: light2 is the role\'s dark colour, dark1 its light one, every shade a Color the app defines', () => {
  const tokens = JSON.parse(read(repoRoot, 'design/tokens.json'));
  const colours = Object.fromEntries(xaml.children.filter((c) => c.tag === 'Color').map((c) => [c.attrs['x:Key'], c.text?.trim()]));
  assert.equal(colours.SystemAccentColorLight2, tokens.color.role.bridge.dark);
  assert.equal(colours.SystemAccentColorDark1, tokens.color.role.bridge.light);
  for (const k of ['SystemAccentColor', 'SystemAccentColorLight1', 'SystemAccentColorLight3', 'SystemAccentColorDark2', 'SystemAccentColorDark3']) { assert.match(colours[k] ?? '', /^#[0-9A-F]{6}$/, k); }
  assert.deepEqual(Object.keys(colours).sort(), [...keysCs.matchAll(/\("(SystemAccentColor\w*)", "(#[0-9A-F]{6})"\)/g)].map((m) => m[1]).sort());
  // Out of the theme dictionaries: the palette is the same in every theme, as Windows' own is, and
  // high contrast's controls read system colours instead.
  assert.ok(!Object.values(themes).flat().some((b) => /^SystemAccentColor/.test(b.attrs['x:Key'])));
});

test('hyperlinks are words in Bridge, in every theme, and the system\'s link colour in high contrast', () => {
  const brush = (theme, key) => themes[theme].find((b) => b.attrs['x:Key'] === key)?.attrs.Color;
  const tokens = JSON.parse(read(repoRoot, 'design/tokens.json'));
  for (const key of ['SystemControlHyperlinkTextBrush', 'HyperlinkButtonForeground', 'AbLink']) {
    assert.equal(brush('Dark', key), tokens.color.role.bridge.dark, key);
    assert.equal(brush('Light', key), tokens.color.role.bridge.light, key);
    assert.equal(brush('HighContrast', key), '{ThemeResource SystemColorHotlightColor}', key);
  }
  assert.ok(listed('Brushes').includes('HyperlinkButtonForegroundPressed'));
});

test('a shade other than the palette\'s, or light2 and dark1 set apart from the bridge role, is refused', () => {
  for (const [shade, why] of [['light2', /light2 and dark1 are the bridge role's/], ['dark4', /expected light3/]]) {
    const root = copy(`accent-${shade}`);
    const tokens = JSON.parse(read(root, 'design/tokens.json'));
    tokens.color.accent.windows[shade] = '#123456';
    writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify(tokens));
    assert.throws(() => generate(root), why);
  }
});

test('the Windows type ramp is a TextBlock style per role: its size, line height and tabular figures, the title in Display and code in the mono', () => {
  const styles = Object.fromEntries(xaml.children.filter((c) => c.tag === 'Style' && c.attrs.TargetType === 'TextBlock')
    .map((s) => [s.attrs['x:Key'], Object.fromEntries(s.children.map((x) => [x.attrs.Property, x.attrs.Value]))]));
  assert.deepEqual(Object.keys(styles), listed('TypeStyles'));
  assert.deepEqual(Object.keys(styles), ['AbTypeTitle', 'AbTypeRow', 'AbTypeReading', 'AbTypeMeta', 'AbTypeSectionHeader', 'AbTypeCode']);
  assert.equal(styles.AbTypeTitle.FontSize, '20');
  assert.equal(styles.AbTypeTitle.LineHeight, '28');
  assert.equal(styles.AbTypeTitle.FontWeight, 'SemiBold');
  assert.match(styles.AbTypeTitle.FontFamily, /^Segoe UI Variable Display, /);
  assert.equal(styles.AbTypeReading.FontSize, '15');
  assert.equal(styles.AbTypeReading.LineHeight, '24');
  assert.equal(styles.AbTypeSectionHeader.FontWeight, 'SemiBold');
  assert.equal(styles.AbTypeRow.FontWeight, undefined, 'regular: semibold means unread activity');
  assert.match(styles.AbTypeCode.FontFamily, /#Agent Bridge Mono, /);
  for (const s of Object.values(styles)) {
    assert.equal(s['Typography.NumeralAlignment'], 'Tabular');
    assert.equal(s.LineStackingStrategy, 'BlockLineHeight');
    assert.equal(s.CharacterSpacing, undefined, 'no tracking added to Segoe UI');
  }
});

test('a type role the generator cannot read is refused rather than guessed', () => {
  const root = copy('type');
  const tokens = JSON.parse(read(root, 'design/tokens.json'));
  tokens.type.roles.windows.meta = 'twelve on sixteen';
  writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify(tokens));
  assert.throws(() => generate(root), /type\.roles\.windows\.meta/);
});

test('the layout sizes reach the app\'s XAML as doubles and its view models as constants, from the same tokens', () => {
  const tokens = JSON.parse(read(repoRoot, 'design/tokens.json'));
  const cs = read(repoRoot, 'src/AgentBridge.App.Core/DesignLayout.cs');
  const doubles = Object.fromEntries(xaml.children.filter((c) => c.tag === 'x:Double').map((c) => [c.attrs['x:Key'], Number(c.text)]));
  assert.match(cs, new RegExp(`public const double ThreePane = ${tokens.layout.breakpoints.threePane};`));
  assert.match(cs, new RegExp(`public const double TwoPane = ${tokens.layout.breakpoints.twoPane};`));
  for (const [name, v] of Object.entries(tokens.layout.windows).filter(([k]) => !k.startsWith('$'))) {
    const pascal = name.charAt(0).toUpperCase() + name.slice(1);
    assert.equal(doubles[`AbLayout${pascal}`], v, name);
    assert.match(cs, new RegExp(`public const double ${pascal} = ${v};`), name);
  }
  assert.deepEqual(listed('LayoutSizes'), Object.keys(doubles).filter((k) => k.startsWith('AbLayout')));
});
