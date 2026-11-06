import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { crc32 } from 'node:zlib';
import path from 'node:path';
import { repoRoot } from '../generate.mjs';
import { appIconSpec, appIconTargets, staleAppIcons, writeAppIcons, ICO_SIZES, rasterize, windowsContainer } from '../lib/app-icon.mjs';
import { render, encodePng, decodePng, readIco, pngSize } from '../lib/raster.mjs';

const scratch = path.join(repoRoot, 'tools', 'design-gen', '.scratch', 'app-icon');
after(() => rmSync(scratch, { recursive: true, force: true }));

// design/ in a copy the test may change, with the app icon generated into it.
function copy(name) {
  const root = path.join(scratch, name);
  rmSync(root, { recursive: true, force: true });
  cpSync(path.join(repoRoot, 'design'), path.join(root, 'design'), { recursive: true });
  writeAppIcons(root);
  return root;
}

const setTokens = (root, change) => {
  const file = path.join(root, 'design', 'tokens.json');
  const tokens = JSON.parse(readFileSync(file, 'utf8'));
  change(tokens);
  writeFileSync(file, JSON.stringify(tokens));
};

test('every app icon in the repository matches design/', () => {
  assert.deepEqual(staleAppIcons(repoRoot), [], 'run node tools/design-gen/generate.mjs and commit the result');
});

test('a changed plate colour makes every app icon stale but the plateless layers and glyphs, and generating again puts it back', () => {
  const root = copy('plate');
  assert.deepEqual(staleAppIcons(root), []);
  setTokens(root, (t) => { t.appIcon.plate = '#102A2E'; });
  const cat = 'design/apple/Assets.xcassets';
  const unchanged = ['design/app-icon/android-foreground.svg', 'design/app-icon/android-monochrome.svg',
    `${cat}/Contents.json`, `${cat}/AppIcon.appiconset/Contents.json`, `${cat}/AppIcon.appiconset/ios-1024-dark.png`, `${cat}/AppIcon.appiconset/ios-1024-tinted.png`,
    `${cat}/MenuBarIcon.imageset/Contents.json`, `${cat}/MenuBarIcon.imageset/menubar.png`, `${cat}/MenuBarIcon.imageset/menubar@2x.png`,
    'design/android/drawable/ic_launcher_foreground.xml', 'design/android/drawable/ic_launcher_monochrome.xml', 'design/android/drawable/ic_stat_slipgates.xml', 'design/android/mipmap-anydpi-v26/ic_launcher.xml',
    'pwa/icons/monochrome-192.png', 'pwa/icons/monochrome-512.png', 'pwa/icons/badge-96.png'];
  assert.deepEqual(staleAppIcons(root), Object.keys(appIconTargets(appIconSpec(path.join(root, 'design')))).filter((f) => !unchanged.includes(f)));
  writeAppIcons(root);
  assert.deepEqual(staleAppIcons(root), []);
});

test('the mark is the colour tokens.json gives, or the dark value of the role it names', () => {
  const root = copy('bridge');
  setTokens(root, (t) => { t.appIcon.mark = '#EEF1F4'; });
  assert.ok(staleAppIcons(root).includes('pwa/icons/icon-192.png'));
  assert.match(appIconTargets(appIconSpec(path.join(root, 'design')))['pwa/icon.svg'].svg, /<path [^>]*fill="#EEF1F4"\/>/);
  setTokens(root, (t) => { t.appIcon.mark = 'bridge'; t.color.role.bridge.dark = '#3BE0CB'; });
  assert.match(appIconTargets(appIconSpec(path.join(root, 'design')))['pwa/icon.svg'].svg, /<path [^>]*fill="#3BE0CB"\/>/);
  setTokens(root, (t) => { t.appIcon.mark = 'nowhere'; });
  assert.throws(() => appIconSpec(path.join(root, 'design')), /neither a #RRGGBB colour nor a colour role/);
});

test('an icon compressed differently but with the same pixels is not stale', () => {
  const root = copy('recompress');
  const file = path.join(root, 'pwa', 'icons', 'icon-192.png');
  const before = readFileSync(file);
  writeFileSync(file, encodePng(decodePng(before), { level: 1 }));
  assert.notDeepEqual(readFileSync(file), before);
  assert.deepEqual(staleAppIcons(root), []);
});

test('the default iOS icon is written without an alpha channel, and one with alpha is stale', () => {
  const rel = 'design/apple/Assets.xcassets/AppIcon.appiconset/ios-1024.png';
  const shipped = readFileSync(path.join(repoRoot, rel));
  assert.equal(shipped[25], 2, 'IHDR colour type 2, RGB');
  assert.equal(decodePng(shipped).opaque, true);
  // The dark and tinted icons draw the mark on transparency, so they keep their alpha.
  assert.equal(readFileSync(path.join(repoRoot, rel.replace('ios-1024', 'ios-1024-dark')))[25], 6);
  const root = copy('opaque');
  const file = path.join(root, rel);
  writeFileSync(file, encodePng(decodePng(readFileSync(file))));
  assert.deepEqual(staleAppIcons(root), [rel]);
  writeAppIcons(root);
  assert.deepEqual(staleAppIcons(root), []);
});

test('a default iOS icon made transparent by a tRNS colour key is stale', () => {
  const rel = 'design/apple/Assets.xcassets/AppIcon.appiconset/ios-1024.png';
  const root = copy('trns');
  const file = path.join(root, rel);
  const png = readFileSync(file);
  // Key out the plate's own colour, the first pixel, after the 33-byte signature and IHDR.
  const plate = decodePng(png).pixels;
  const data = Buffer.from([0, plate[0], 0, plate[1], 0, plate[2]]);
  const head = Buffer.concat([Buffer.from('tRNS'), data]);
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(head));
  writeFileSync(file, Buffer.concat([png.subarray(0, 33), len, head, crc, png.subarray(33)]));
  assert.equal(decodePng(readFileSync(file)), null);
  assert.deepEqual(staleAppIcons(root), [rel]);
  writeAppIcons(root);
  assert.deepEqual(staleAppIcons(root), []);
});

test('an opaque PNG refuses a pixel that is not fully opaque', () => {
  const img = { width: 1, height: 1, pixels: Buffer.from([1, 2, 3, 254]) };
  assert.throws(() => encodePng(img, { opaque: true }), /no transparent pixel/);
});

test('a mark the rasterizer cannot draw exactly is refused rather than drawn wrong', () => {
  const root = copy('path');
  const mark = path.join(root, 'design', 'icons', 'mark.svg');
  const original = readFileSync(mark, 'utf8');
  const svg = (body) => `<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">${body}</svg>`;
  writeFileSync(mark, svg('<path d="M4 4L44 44" fill="none" stroke="currentColor"/>'));
  assert.throws(() => staleAppIcons(root), /fills a path in currentColor, without a stroke/);
  // An arc would need trigonometry, which isn't rounded alike on every machine.
  writeFileSync(mark, original.replace(/C([^C]*?)Z/, 'A4 4 0 0 1 10 10Z'));
  assert.throws(() => staleAppIcons(root), /M, L, C and Z only, not A/);
  writeFileSync(mark, svg('<rect x="4" y="4" width="40" height="40" fill="currentColor"/>'));
  assert.throws(() => staleAppIcons(root), /circles and paths only, not <rect>/);
  // A half turn about a point beside the circle keeps where it starts but moves its centre.
  writeFileSync(mark, svg('<circle transform="rotate(180 28.5 28)" cx="19" cy="28" r="9.5" fill="none" stroke="currentColor" stroke-width="3.4"/>'));
  assert.throws(() => staleAppIcons(root), /circles without a transform/);
  writeFileSync(mark, svg('<circle cx="19" cy="28" r="9.5" fill="none" stroke="currentColor" stroke-width="3.4" stroke-dasharray="2 2"/>'));
  assert.throws(() => staleAppIcons(root), /solid strokes only/);
});

test('a PNG cut short or failing its checksum is stale, and generating again replaces it', () => {
  const root = copy('corrupt');
  const file = path.join(root, 'pwa', 'icons', 'maskable-192.png');
  const good = readFileSync(file);
  writeFileSync(file, good.subarray(0, 20));
  assert.deepEqual(staleAppIcons(root), ['pwa/icons/maskable-192.png']);
  const flipped = Buffer.from(good);
  flipped[good.length - 5] ^= 0xff; // IEND's CRC
  writeFileSync(file, flipped);
  assert.deepEqual(staleAppIcons(root), ['pwa/icons/maskable-192.png']);
  writeAppIcons(root);
  assert.deepEqual(staleAppIcons(root), []);
});

test('a plate the mark reaches under 3:1 against is refused', () => {
  const root = copy('contrast');
  setTokens(root, (t) => { t.appIcon.plate = '#20B8A5'; });
  assert.throws(() => staleAppIcons(root), /under 3:1/);
});

test('a drawn circle covers its area and nothing outside it', () => {
  const white = { r: 255, g: 255, b: 255 };
  const { pixels } = render({ grid: 100, shapes: [{ kind: 'circle', cx: 50, cy: 50, r: 30, fill: white }] }, 100);
  let area = 0;
  for (let i = 3; i < pixels.length; i += 4) { area += pixels[i] / 255; }
  assert.ok(Math.abs(area - Math.PI * 30 * 30) < 10, `covers ${area}`);
  assert.equal(pixels[3], 0, 'the corner is transparent');
  assert.equal(pixels[(50 * 100 + 50) * 4 + 3], 255, 'the centre is opaque');
});

test('the Windows icon holds every size Windows asks for, a native cut of the mark at 24 px and under', () => {
  const entries = readIco(readFileSync(path.join(repoRoot, 'src/AgentBridge.App/Assets/AgentBridge.ico')));
  assert.deepEqual(entries.map((e) => e.width), ICO_SIZES);
  for (const e of entries) { assert.deepEqual(pngSize(e.png), { width: e.width, height: e.width }); }
  const spec = appIconSpec(path.join(repoRoot, 'design'));
  assert.deepEqual([16, 20, 24, 30].map((n) => windowsContainer(spec, n)), ['windowsSmall', 'windows20', 'windows24', 'windows']);
  // A native cut's strokes sit on whole pixels, so far fewer of its mark's pixels are partly
  // covered than the display mark's scaled to the same size.
  const blurred = (img) => { let part = 0, any = 0; for (let i = 0; i < img.pixels.length; i += 4) { const g = img.pixels[i + 1]; if (g > 40) { any++; if (g < 200) { part++; } } } return part / any; };
  for (const [n, name] of [[16, 'windowsSmall'], [20, 'windows20'], [24, 'windows24']]) {
    assert.ok(blurred(rasterize(spec, name, n)) < blurred(rasterize(spec, 'windows', n)) / 2, `${name} at ${n} px`);
  }
});

test('a filled path covers its area by the nonzero rule, leaving a hole wound the other way empty', () => {
  const white = { r: 255, g: 255, b: 255 };
  const square = (x0, y0, x1, y1, cw) => (cw
    ? [{ c: 'M', p: [x0, y0] }, { c: 'L', p: [x1, y0] }, { c: 'L', p: [x1, y1] }, { c: 'L', p: [x0, y1] }, { c: 'Z', p: [] }]
    : [{ c: 'M', p: [x0, y0] }, { c: 'L', p: [x0, y1] }, { c: 'L', p: [x1, y1] }, { c: 'L', p: [x1, y0] }, { c: 'Z', p: [] }]);
  const commands = [...square(10, 10, 90, 90, true), ...square(30, 30, 50, 50, false),
    { c: 'M', p: [60, 30] }, { c: 'C', p: [60, 30, 85, 30, 85, 50] }, { c: 'L', p: [60, 50] }, { c: 'Z', p: [] }];
  const { pixels } = render({ grid: 100, shapes: [{ kind: 'path', commands, fill: white }] }, 100);
  const alpha = (x, y) => pixels[(y * 100 + x) * 4 + 3];
  let area = 0;
  for (let i = 3; i < pixels.length; i += 4) { area += pixels[i] / 255; }
  assert.ok(Math.abs(area - (80 * 80 - 20 * 20)) < 4, `covers ${area}`);
  assert.equal(alpha(40, 40), 0, 'the hole is empty');
  assert.equal(alpha(20, 20), 255, 'the square is filled');
  assert.equal(alpha(5, 5), 0, 'outside is empty');
  // The same subpath wound the same way again stays filled: nonzero, not even-odd.
  assert.equal(alpha(70, 40), 255);
  // Its edges lie inside the square, so they are no boundary and leave no see-through seam.
  for (let x = 58; x <= 86; x++) {
    for (const y of [29, 30, 49, 50]) { assert.equal(alpha(x, y), 255, `(${x}, ${y})`); }
  }
  for (let y = 28; y <= 52; y++) { assert.equal(alpha(59, y), 255, `(59, ${y})`); assert.equal(alpha(60, y), 255, `(60, ${y})`); }
});

test('an edge two oppositely wound subpaths share cancels, leaving no line across the notch it opens', () => {
  const white = { r: 255, g: 255, b: 255 };
  const pts = (list) => [...list.map(([x, y], i) => ({ c: i ? 'L' : 'M', p: [x, y] })), { c: 'Z', p: [] }];
  const scene = (commands) => ({ grid: 100, shapes: [{ kind: 'path', commands, fill: white }] });
  const cut = render(scene([...pts([[10, 10.4], [90, 10.4], [90, 90], [10, 90]]), ...pts([[30, 10.4], [30, 50], [70, 50], [70, 10.4]])]), 100).pixels;
  const one = render(scene(pts([[10, 10.4], [30, 10.4], [30, 50], [70, 50], [70, 10.4], [90, 10.4], [90, 90], [10, 90]])), 100).pixels;
  for (let x = 31; x <= 69; x++) { assert.equal(cut[(10 * 100 + x) * 4 + 3], 0, `(${x}, 10)`); }
  assert.deepEqual(cut, one);
});

test('the Apple asset catalog lists every icon it ships, iOS in its three appearances, and the menu bar image is a template drawn in black', () => {
  const targets = appIconTargets(appIconSpec(path.join(repoRoot, 'design')));
  const set = 'design/apple/Assets.xcassets/AppIcon.appiconset';
  const contents = JSON.parse(targets[`${set}/Contents.json`].text);
  for (const image of contents.images) { assert.ok(targets[`${set}/${image.filename}`], image.filename); }
  assert.deepEqual(contents.images.filter((i) => i.platform === 'ios').map((i) => i.appearances?.[0].value ?? 'any'), ['any', 'dark', 'tinted']);
  assert.deepEqual(contents.images.filter((i) => i.idiom === 'mac').map((i) => `${i.size}@${i.scale}`), ['16x16@1x', '16x16@2x', '32x32@1x', '32x32@2x', '128x128@1x', '128x128@2x', '256x256@1x', '256x256@2x', '512x512@1x', '512x512@2x']);
  const bar = 'design/apple/Assets.xcassets/MenuBarIcon.imageset';
  assert.equal(JSON.parse(targets[`${bar}/Contents.json`].text).properties['template-rendering-intent'], 'template');
  const img = decodePng(readFileSync(path.join(repoRoot, bar, 'menubar.png')));
  assert.equal(img.width, 16);
  for (let i = 0; i < img.pixels.length; i += 4) { if (img.pixels[i + 3]) { assert.deepEqual([...img.pixels.subarray(i, i + 3)], [0, 0, 0]); } }
});

test('Android gets the adaptive icon with a monochrome layer for themed icons, and a white notification icon', () => {
  const targets = appIconTargets(appIconSpec(path.join(repoRoot, 'design')));
  assert.match(targets['design/android/mipmap-anydpi-v26/ic_launcher.xml'].text, /<monochrome android:drawable="@drawable\/ic_launcher_monochrome"\/>/);
  const stat = targets['design/android/drawable/ic_stat_slipgates.xml'].text;
  assert.match(stat, /android:viewportWidth="24"/);
  assert.deepEqual([...new Set(stat.match(/fillColor="[^"]*"/g))], ['fillColor="#FFFFFF"']);
});

// The app finds its icon by these paths: the exe's icon, the window's and the tray's, the toast
// logo, and Velopack's setup and shortcut icon. Each must be a generated file. The project names
// them once (AppIconFile, AppLogoFile), for the build and through build/app-channels.ps1 the
// installer; the app's code reads them from AppAssets (#537).
test('the app project, its window, tray, toasts and installer use the generated icon', () => {
  const targets = Object.keys(appIconTargets(appIconSpec(path.join(repoRoot, 'design'))));
  const read = (rel) => readFileSync(path.join(repoRoot, rel), 'utf8');
  const csproj = read('src/AgentBridge.App/AgentBridge.App.csproj');
  assert.match(csproj, /<AppIconFile>Assets\\AgentBridge\.ico<\/AppIconFile>/);
  assert.match(csproj, /<AppLogoFile>Assets\\AgentBridge\.png<\/AppLogoFile>/);
  assert.match(csproj, /<ApplicationIcon>\$\(AppIconFile\)<\/ApplicationIcon>/);
  assert.match(csproj, /<Content Include="\$\(AppIconFile\)" CopyToOutputDirectory="PreserveNewest" \/>/);
  assert.match(csproj, /<Content Include="\$\(AppLogoFile\)" CopyToOutputDirectory="PreserveNewest" \/>/);
  const assets = read('src/AgentBridge.App.Core/AppAssets.cs');
  assert.match(assets, /Icon = Path\.Combine\("Assets", "AgentBridge\.ico"\)/);
  assert.match(assets, /Logo = Path\.Combine\("Assets", "AgentBridge\.png"\)/);
  assert.match(read('src/AgentBridge.App/MainWindow.xaml.cs'), /AppWindow\.SetIcon\(Path\.Combine\(AppContext\.BaseDirectory, AppAssets\.Icon\)\)/);
  assert.match(read('src/AgentBridge.App/App.xaml.cs'), /new TrayIcon\([^)]*AppAssets\.Icon\)/);
  assert.match(read('src/AgentBridge.App/App.xaml.cs'), /ToastActivator\.Register\([^)]*AppAssets\.Logo\)/);
  assert.match(read('build/app-channels.ps1'), /\$AppIconFile = Get-AppProjectProperty 'AppIconFile'/);
  assert.match(read('build/pack-app.ps1'), /--icon \(Join-Path \$publish \$AppIconFile\)/);
  for (const f of ['src/AgentBridge.App/Assets/AgentBridge.ico', 'src/AgentBridge.App/Assets/AgentBridge.png']) { assert.ok(targets.includes(f), f); }
  assert.deepEqual(pngSize(readFileSync(path.join(repoRoot, 'src/AgentBridge.App/Assets/AgentBridge.png'))), { width: 256, height: 256 });
});

test('the design keeps an SVG of each platform\'s container, Android\'s as its three layers', () => {
  const svgs = Object.keys(appIconTargets(appIconSpec(path.join(repoRoot, 'design')))).filter((f) => f.startsWith('design/app-icon/'));
  assert.deepEqual(svgs.map((f) => path.basename(f, '.svg')).sort(), ['android-background', 'android-foreground', 'android-monochrome', 'ios', 'macos', 'maskable', 'windows', 'windows-20', 'windows-24', 'windows-small']);
});
