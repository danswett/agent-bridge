import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { repoRoot } from '../generate.mjs';
import { appIconSpec, appIconTargets, staleAppIcons, writeAppIcons, ICO_SIZES, rasterize } from '../lib/app-icon.mjs';
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

test('a changed plate colour makes every app icon stale but the plateless Android layers, and generating again puts it back', () => {
  const root = copy('plate');
  assert.deepEqual(staleAppIcons(root), []);
  setTokens(root, (t) => { t.appIcon.plate = '#102A2E'; });
  const unchanged = ['design/app-icon/android-foreground.svg', 'design/app-icon/android-monochrome.svg'];
  assert.deepEqual(staleAppIcons(root), Object.keys(appIconTargets(appIconSpec(path.join(root, 'design')))).filter((f) => !unchanged.includes(f)));
  writeAppIcons(root);
  assert.deepEqual(staleAppIcons(root), []);
});

test('the mark takes the Bridge role\'s dark value from tokens.json', () => {
  const root = copy('bridge');
  setTokens(root, (t) => { t.color.role.bridge.dark = '#3BE0CB'; });
  assert.ok(staleAppIcons(root).includes('pwa/icons/icon-192.png'));
  assert.match(appIconTargets(appIconSpec(path.join(root, 'design')))['pwa/icon.svg'].svg, /<circle [^>]*fill="#3BE0CB"\/>/);
});

test('an icon compressed differently but with the same pixels is not stale', () => {
  const root = copy('recompress');
  const file = path.join(root, 'pwa', 'icons', 'icon-192.png');
  const before = readFileSync(file);
  writeFileSync(file, encodePng(decodePng(before), { level: 1 }));
  assert.notDeepEqual(readFileSync(file), before);
  assert.deepEqual(staleAppIcons(root), []);
});

test('a mark the rasterizer cannot draw exactly is refused rather than drawn wrong', () => {
  const root = copy('path');
  const mark = path.join(root, 'design', 'icons', 'mark.svg');
  const original = readFileSync(mark, 'utf8');
  writeFileSync(mark, '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48"><path d="M4 4L44 44" fill="none" stroke="currentColor"/></svg>');
  assert.throws(() => staleAppIcons(root), /draws circles only, not <path>/);
  // A half turn about a point beside the ring keeps where it starts but moves its centre.
  writeFileSync(mark, original.replace('<circle cx="19"', '<circle transform="rotate(180 28.5 28)" cx="19"'));
  assert.throws(() => staleAppIcons(root), /circles without a transform/);
  writeFileSync(mark, original.replace('stroke-width="3.4"', 'stroke-width="3.4" stroke-dasharray="2 2"'));
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

test('the Windows icon holds every size Windows asks for, the small tile at 24 px and under', () => {
  const entries = readIco(readFileSync(path.join(repoRoot, 'src/AgentBridge.App/Assets/AgentBridge.ico')));
  assert.deepEqual(entries.map((e) => e.width), ICO_SIZES);
  for (const e of entries) { assert.deepEqual(pngSize(e.png), { width: e.width, height: e.width }); }
  // The small tile's mark fills more of it, so its centre pixel row has more Bridge in it.
  const spec = appIconSpec(path.join(repoRoot, 'design'));
  const bridgeIn = (img) => { let n = 0; for (let i = 0; i < img.pixels.length; i += 4) { n += img.pixels[i + 1] > 150 ? 1 : 0; } return n / (img.width * img.height); };
  assert.ok(bridgeIn(rasterize(spec, 'windowsSmall', 24)) > bridgeIn(rasterize(spec, 'windows', 24)));
});

// The app finds its icon by these paths: the exe's icon, the window's and the tray's, the toast
// logo, and Velopack's setup and shortcut icon. Each must be a generated file.
test('the app project, its window, tray, toasts and installer use the generated icon', () => {
  const targets = Object.keys(appIconTargets(appIconSpec(path.join(repoRoot, 'design'))));
  const read = (rel) => readFileSync(path.join(repoRoot, rel), 'utf8');
  const csproj = read('src/AgentBridge.App/AgentBridge.App.csproj');
  assert.match(csproj, /<ApplicationIcon>Assets\\AgentBridge\.ico<\/ApplicationIcon>/);
  assert.match(csproj, /<Content Include="Assets\\AgentBridge\.ico" CopyToOutputDirectory="PreserveNewest" \/>/);
  assert.match(csproj, /<Content Include="Assets\\AgentBridge\.png" CopyToOutputDirectory="PreserveNewest" \/>/);
  assert.match(read('src/AgentBridge.App/MainWindow.xaml.cs'), /AppWindow\.SetIcon\(Path\.Combine\(AppContext\.BaseDirectory, "Assets", "AgentBridge\.ico"\)\)/);
  assert.match(read('src/AgentBridge.App/App.xaml.cs'), /new TrayIcon\([^)]*"Assets", "AgentBridge\.ico"\)/);
  assert.match(read('src/AgentBridge.App/App.xaml.cs'), /ToastActivator\.Register\([^)]*"Assets", "AgentBridge\.png"\)/);
  assert.match(read('build/pack-app.ps1'), /--icon \(Join-Path \$publish 'Assets\\AgentBridge\.ico'\)/);
  for (const f of ['src/AgentBridge.App/Assets/AgentBridge.ico', 'src/AgentBridge.App/Assets/AgentBridge.png']) { assert.ok(targets.includes(f), f); }
  assert.deepEqual(pngSize(readFileSync(path.join(repoRoot, 'src/AgentBridge.App/Assets/AgentBridge.png'))), { width: 256, height: 256 });
});

test('the design keeps an SVG of each platform\'s container, Android\'s as its three layers', () => {
  const svgs = Object.keys(appIconTargets(appIconSpec(path.join(repoRoot, 'design')))).filter((f) => f.startsWith('design/app-icon/'));
  assert.deepEqual(svgs.map((f) => path.basename(f, '.svg')).sort(), ['android-background', 'android-foreground', 'android-monochrome', 'ios', 'macos', 'maskable', 'windows', 'windows-small']);
});
