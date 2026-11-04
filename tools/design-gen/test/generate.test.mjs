import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
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

test('a generated file design-gen no longer writes is reported stale, and generating again deletes it', () => {
  const root = copy('retired');
  const old = 'src/AgentBridge.Host.Core/DesignBrand.cs';
  mkdirSync(path.join(root, path.dirname(old)), { recursive: true });
  writeFileSync(path.join(root, old), 'namespace AgentBridge.Host.Core;\n');
  assert.deepEqual(staleFiles(root), [old]);
  write(root);
  assert.deepEqual(staleFiles(root), []);
  assert.equal(existsSync(path.join(root, old)), false);
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

test('a rename is one change to the brand token, reaching the Windows app and host, the PWA, Apple and Android', () => {
  const root = copy('brand');
  const tokens = JSON.parse(read(root, 'design/tokens.json'));
  tokens.brand = { name: 'Gatehouse', shortName: 'Gatehouse', supportEmail: 'help@example.org' };
  writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify(tokens));
  const brandFiles = ['src/AgentBridge.Protocol/DesignBrand.cs', 'pwa/design/brand.js', 'design/apple/Brand.swift', 'design/android/values/brand.xml'];
  assert.deepEqual(staleFiles(root).sort(), [...brandFiles, XAML].sort());
  const out = generate(root);
  assert.match(out['src/AgentBridge.Protocol/DesignBrand.cs'], /public const string Name = "Gatehouse";/);
  assert.match(out['pwa/design/brand.js'], /supportEmail: 'help@example\.org',/);
  assert.match(out['design/apple/Brand.swift'], /public static let shortName = "Gatehouse"/);
  assert.match(out['design/android/values/brand.xml'], /<string name="brand_name" translatable="false">Gatehouse<\/string>/);
  assert.match(out[XAML], /<x:String x:Key="AbBrandName">Gatehouse<\/x:String>/);
});

test('a brand short name a home screen would clip, a value needing escaping, or a malformed address is refused', () => {
  const root = copy('badbrand');
  const tokens = JSON.parse(read(root, 'design/tokens.json'));
  const attempt = (b) => {
    writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify({ ...tokens, brand: { ...tokens.brand, ...b } }));
    return () => generate(root);
  };
  assert.throws(attempt({ shortName: 'Slipgates Desktop' }), /a home screen shows 12/);
  assert.throws(attempt({ name: 'Slip "gates"' }), /would need escaping/);
  assert.throws(attempt({ supportEmail: 'support.slipgates.com' }), /not an address/);
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

test('a glyph\'s track stays faint by default, and a session row can draw it solid in trackRow (rule D)', () => {
  const working = read(repoRoot, 'pwa/design/sprite.svg').match(/<symbol id="glyph-mark-working"[\s\S]*?<\/symbol>/)[0];
  assert.match(working, /class="track" fill="var\(--ab-glyph-track, currentColor\)" fill-opacity="var\(--ab-glyph-track-opacity, 0\.3\)"/);
  const xaml = read(repoRoot, XAML).match(/<Style x:Key="AbGlyphMarkWorking"[\s\S]*?<\/Style>/)[0];
  const paths = [...xaml.matchAll(/<Path [^>]*\/>/g)].map((m) => m[0]);
  assert.equal(paths.filter((p) => /Fill="\{TemplateBinding Foreground\}" Opacity="0\.3"/.test(p)).length, 1, 'the faint track');
  const solid = paths.filter((p) => p.includes('{TemplateBinding BorderBrush}'));
  assert.equal(solid.length, 1, 'the solid track, drawn only when BorderBrush is set');
  assert.ok(!solid[0].includes('Opacity='));
});

test('the PWA gets an @font-face with font-display swap for every face of both families, synthesis off, then the fallback stacks', () => {
  const css = read(repoRoot, 'pwa/design/tokens.css');
  const faces = [...css.matchAll(/@font-face \{([^}]*)\}/g)].map((m) => m[1]);
  assert.equal(faces.length, 10, 'the sans\' variable pair, and the mono\'s four weights, each with an italic');
  for (const f of faces) {
    assert.match(f, /font-family: "Slipgates (Sans|Mono)";/);
    assert.match(f, /font-display: swap;/);
  }
  const sans = faces.filter((f) => /Slipgates Sans/.test(f));
  assert.deepEqual(sans.map((f) => /font-style: (\w+);/.exec(f)[1]), ['normal', 'italic']);
  for (const f of sans) { assert.match(f, /font-weight: 400 700;/, 'the variable files cover every weight the ramps use'); }
  assert.ok(faces.some((f) => /SlipgatesMono-MediumItalic\.woff2/.test(f) && /font-weight: 500;/.test(f) && /font-style: italic;/.test(f)));
  assert.match(css, /--ab-font-sans: "Slipgates Sans", "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, "Roboto", sans-serif;/);
  assert.match(css, /--ab-font-mono: "Slipgates Mono", "Cascadia Mono", "SF Mono", "Roboto Mono", ui-monospace, monospace;/);
  assert.match(css, /:root \{[^}]*font-synthesis: none;/, 'no browser fakes a weight or italic (ui.md §4.2)');
});

test('the app names each family by its collection of real faces, sets the sans on all text, and DesignKeys lists the files and faces to check', () => {
  const source = read(repoRoot, XAML);
  assert.match(source, /<FontFamily x:Key="MonoFontFamily">ms-appx:\/\/\/Assets\/Fonts\/SlipgatesMono\.ttc#Slipgates Mono, Cascadia Mono, SF Mono, Roboto Mono, Consolas<\/FontFamily>/);
  const sans = 'ms-appx:///Assets/Fonts/SlipgatesSans.ttc#Slipgates Sans, Segoe UI Variable Text, Segoe UI, Roboto';
  assert.ok(source.includes(`<FontFamily x:Key="SansFontFamily">${sans}</FontFamily>`));
  assert.ok(source.includes(`<FontFamily x:Key="ContentControlThemeFontFamily">${sans}</FontFamily>`), 'controls take the sans too');
  for (const target of ['TextBlock', 'RichTextBlock']) {
    const implicit = xaml.children.find((c) => c.tag === 'Style' && c.attrs.TargetType === target && !c.attrs['x:Key']);
    assert.equal(implicit?.children[0].attrs.Value, sans, target);
  }
  assert.deepEqual(listed('FontFiles'), ['Assets/Fonts/SlipgatesSans.ttc', 'Assets/Fonts/SlipgatesSans-OFL.txt', 'Assets/Fonts/SlipgatesMono.ttc', 'Assets/Fonts/SlipgatesMono-OFL.txt']);
  assert.equal([...keysCs.matchAll(/^\s+\((\d{3}), (true|false)\),$/gm)].length, 8, 'every weight the ramps use, upright and italic');
  assert.equal([...keysCs.matchAll(/^\s+\((\d{3}), (true|false), (\d+)\),$/gm)].length, 8, 'a probe advance for each sans face');
});

test('a face the tokens promise but design/fonts lacks is refused', () => {
  const root = copy('nofont');
  rmSync(path.join(root, 'design/fonts/slipgates-mono/woff2/SlipgatesMono-BoldItalic.woff2'));
  assert.throws(() => generate(root), /lacks woff2\/SlipgatesMono-BoldItalic\.woff2/);
});

test('a weight the ramps use that a bundled family has no real face for is refused, not left to be faked', () => {
  const root = copy('nosynth');
  const tokens = JSON.parse(read(root, 'design/tokens.json'));
  tokens.type.weights.push('heavy');
  writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify(tokens));
  assert.throws(() => generate(root), /type\.sans lacks 800 normal, 800 italic, which the ramps use: a platform would synthesize it/);
});

test('a family whose faces.json or collection disagrees with the tokens is refused', () => {
  const root = copy('badmanifest');
  const file = path.join(root, 'design/fonts/slipgates-sans/faces.json');
  const manifest = JSON.parse(readFileSync(file, 'utf8'));
  manifest.faces = manifest.faces.filter((f) => !(f.weight === 500 && f.style === 'italic'));
  writeFileSync(file, JSON.stringify(manifest));
  assert.throws(() => generate(root), /faces\.json has faces/);
});

test('Apple and Android get every real face by name: PostScript names and Info.plist keys, and a font-family XML', () => {
  const out = generate();
  const swift = out['design/apple/SlipgatesFonts.swift'];
  for (const name of ['"SlipgatesSans"', '"SlipgatesSansItalic-SemiBold"', '"SlipgatesMono-MediumItalic"']) { assert.ok(swift.includes(name), name); }
  assert.match(swift, /UIFontMetrics\(forTextStyle: textStyle\)\.scaledFont/);
  const plist = out['design/apple/SlipgatesFonts.plist'];
  assert.match(plist, /<key>UIAppFonts<\/key>/);
  assert.match(plist, /<key>ATSApplicationFontsPath<\/key>\s*<string>Fonts<\/string>/);
  assert.equal([...plist.matchAll(/<string>Fonts\/[\w-]+\.ttf<\/string>/g)].length, 10);
  for (const [family, n] of [['slipgates_sans', 8], ['slipgates_mono', 8]]) {
    const x = out[`design/android/font/${family}.xml`];
    const fonts = [...x.matchAll(/<font android:font="@font\/(\w+)" android:fontWeight="(\d+)" android:fontStyle="(\w+)"/g)];
    assert.equal(fonts.length, n, family);
    for (const [, res] of fonts) { assert.ok(Buffer.isBuffer(out[`design/android/font/${res}.ttf`]), res); }
  }
  assert.match(out['design/android/font/slipgates_sans.xml'], /android:fontWeight="600" android:fontStyle="italic" android:fontVariationSettings="'wght' 600"/);
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

test('the Windows type ramp is a TextBlock style per role: its size, line height and tabular figures, Slipgates Sans, and code in the mono', () => {
  const styles = Object.fromEntries(xaml.children.filter((c) => c.tag === 'Style' && c.attrs.TargetType === 'TextBlock' && c.attrs['x:Key'])
    .map((s) => [s.attrs['x:Key'], Object.fromEntries(s.children.map((x) => [x.attrs.Property, x.attrs.Value]))]));
  assert.deepEqual(Object.keys(styles), listed('TypeStyles'));
  assert.deepEqual(Object.keys(styles), ['AbTypeTitle', 'AbTypeRow', 'AbTypeReading', 'AbTypeMeta', 'AbTypeSectionHeader', 'AbTypeCode']);
  assert.equal(styles.AbTypeTitle.FontSize, '20');
  assert.equal(styles.AbTypeTitle.LineHeight, '28');
  assert.equal(styles.AbTypeTitle.FontWeight, 'SemiBold');
  assert.equal(styles.AbTypeReading.FontSize, '15');
  assert.equal(styles.AbTypeReading.LineHeight, '24');
  assert.equal(styles.AbTypeSectionHeader.FontWeight, 'SemiBold');
  assert.equal(styles.AbTypeRow.FontWeight, undefined, 'regular: semibold means unread activity');
  for (const [key, s] of Object.entries(styles)) {
    assert.match(s.FontFamily, key === 'AbTypeCode' ? /SlipgatesMono\.ttc#Slipgates Mono, / : /SlipgatesSans\.ttc#Slipgates Sans, /, key);
    assert.equal(s['Typography.NumeralAlignment'], 'Tabular');
    assert.equal(s.LineStackingStrategy, 'BlockLineHeight');
    assert.equal(s.CharacterSpacing, key === 'AbTypeTitle' ? '20' : undefined, `${key}: only the title is tracked, +2%`);
  }
});

test('tracking reaches every client for the title and the wordmark: CSS, WinUI, Apple and Android', () => {
  const out = generate();
  const css = out['pwa/design/tokens.css'];
  assert.match(css, /--ab-tracking-title: 0\.02em;/);
  assert.match(css, /--ab-tracking-wordmark: 0\.02em;/);
  assert.match(out['design/apple/SlipgatesFonts.swift'], /public static let title: CGFloat = 0\.02\n\s*public static let wordmark: CGFloat = 0\.02/);
  const android = out['design/android/values/slipgates_type.xml'];
  assert.match(android, /<item name="slipgates_tracking_title" format="float" type="dimen">0\.02<\/item>/);
  assert.match(android, /<item name="slipgates_tracking_wordmark" format="float" type="dimen">0\.02<\/item>/);
});

test('tracking body text is refused, so the reading measure holds', () => {
  for (const role of ['reading', 'row', 'meta', 'code']) {
    const root = copy(`track-${role}`);
    const tokens = JSON.parse(read(root, 'design/tokens.json'));
    tokens.type.tracking[role] = 0.01;
    writeFileSync(path.join(root, 'design/tokens.json'), JSON.stringify(tokens));
    assert.throws(() => generate(root), /body text stays untracked/, role);
  }
});

// The text cut everywhere (#544 §11): with an opsz axis, CoreText and Android choose Inter's
// narrower display cut for large titles by themselves.
test('the Apple and Android variable files carry weight as their only axis: optical size is pinned at the text cut', () => {
  const out = generate();
  for (const res of ['slipgates_sans_upright', 'slipgates_sans_italic']) {
    const b = out[`design/android/font/${res}.ttf`];
    let fvar;
    for (let i = 0, n = b.readUInt16BE(4); i < n; i++) {
      if (b.toString('latin1', 12 + i * 16, 16 + i * 16) === 'fvar') { fvar = b.readUInt32BE(20 + i * 16); }
    }
    assert.ok(fvar !== undefined, `${res} is variable`);
    const axes = b.readUInt16BE(fvar + 4) === 16 ? b.readUInt16BE(fvar + 8) : -1;
    const tags = Array.from({ length: axes }, (_, i) => b.toString('latin1', fvar + 16 + i * 20, fvar + 20 + i * 20));
    assert.deepEqual(tags, ['wght'], res);
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
