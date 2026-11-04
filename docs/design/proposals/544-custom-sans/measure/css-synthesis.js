// Proposal #544 (not approved): proves CSS renders real faces, not synthesized ones, in headless Chromium.
//   NODE_PATH=<repo>/tools/design-mockups/node_modules node css-synthesis.js <repo root> > results/css-synthesis.md
// For each family it declares the complete @font-face set under one name with font-synthesis: none, then
// renders every weight and style the ramps use through ordinary CSS (font-weight, font-style, <strong>, <em>)
// and compares the pixels and the advance width with the same text set in that one real face, loaded alone
// under its own name. The control declares only the Regular file: with synthesis allowed, Chromium fakes
// bold and oblique (the pixels match neither face); with font-synthesis: none it falls back to Regular.
const fs = require('fs'), path = require('path');
const { chromium } = require('playwright');
const root = path.resolve(process.argv[2]);
const woff2 = p => `url(data:font/woff2;base64,${fs.readFileSync(path.join(root, p)).toString('base64')}) format('woff2')`;
const P = 'docs/design/proposals/544-custom-sans/fonts/woff2/';
const M = 'design/fonts/agent-bridge-mono/woff2/';
const NEED = [[400, 'normal'], [500, 'normal'], [600, 'normal'], [700, 'normal'], [400, 'italic'], [500, 'italic'], [600, 'italic'], [700, 'italic']];
const NAME = { 400: 'Regular', 500: 'Medium', 600: 'SemiBold', 700: 'Bold' };
const statics = (dir, stem, have = [400, 500, 600, 700]) => NEED.filter(([w]) => have.includes(w)).map(([w, s]) => ({
  w, s, src: dir + stem + '-' + (w === 400 && s === 'italic' ? 'Italic' : NAME[w] + (s === 'italic' ? 'Italic' : '')) + '.woff2',
}));
const families = [
  { label: 'Slipgates Sans A (8 statics)', faces: statics(P, 'SlipgatesSansA') },
  { label: 'Slipgates Sans B (8 statics)', faces: statics(P, 'SlipgatesSansB') },
  { label: 'Slipgates Sans B (2 variable files)', variable: true, faces: [
    { w: '100 900', s: 'normal', src: P + 'SlipgatesSansB[opsz,wght].woff2' },
    { w: '100 900', s: 'italic', src: P + 'SlipgatesSansB-Italic[opsz,wght].woff2' }] },
  { label: 'Agent Bridge Mono (6 statics, as bundled)', faces: statics(M, 'AgentBridgeMono', [400, 600, 700]) },
  { label: 'Control: Slipgates Sans A, Regular file only', control: true, faces: statics(P, 'SlipgatesSansA', [400]).filter(f => f.s === 'normal') },
];
const TEXT = 'Hamburgefonstiv 0123 Il1|';
// CSS Fonts 4 §5.2 font matching for static faces: style first (italic falls back to normal), then weight.
function cssMatch(faces, w, s) {
  const pool = faces.filter(f => f.s === s).length ? faces.filter(f => f.s === s) : faces.filter(f => f.s === 'normal');
  const ws = pool.map(f => f.w);
  const asc = ws.slice().sort((a, b) => a - b), desc = asc.slice().reverse();
  const order = w > 500 ? [...asc.filter(x => x >= w), ...desc.filter(x => x < w)]
    : w < 400 ? [...desc.filter(x => x <= w), ...asc.filter(x => x > w)]
    : [...asc.filter(x => x >= w && x <= 500), ...desc.filter(x => x < w), ...asc.filter(x => x > 500)];
  return pool.find(f => f.w === order[0]);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 900, height: 200 }, deviceScaleFactor: 2 });
  const out = ['# CSS synthesis check (headless Chromium, DPR 2, 40 px)', '',
    'Each row renders the text through ordinary CSS on the full family and compares it with the reference: the real face for that weight and style, loaded alone. "Real face" means identical pixels and advance width.', ''];
  for (const fam of families) {
    out.push(`## ${fam.label}`, '', '| Asked | Via | font-synthesis | Result | Width (px) | Reference width |', '|---|---|---|---|---|---|');
    for (const synth of fam.control ? ['none', 'auto'] : ['none']) {
      for (const [w, s] of NEED) {
        // The reference is the face a correct matcher should pick, set alone so nothing can be synthesized.
        const ref = fam.variable ? fam.faces.find(f => f.s === s) : fam.faces.find(f => f.w === w && f.s === s);
        const regular = fam.faces.find(f => (f.w === 400 || f.variable) && f.s === 'normal') || fam.faces[0];
        const refFace = ref || cssMatch(fam.faces, w, s);
        const css = fam.faces.map(f => `@font-face{font-family:Fam;src:${woff2(f.src)};font-weight:${f.w};font-style:${f.s}}`).join('') +
          `@font-face{font-family:Ref;src:${woff2(refFace.src)};font-weight:${fam.variable ? '100 900' : '400'};font-style:normal}` +
          `@font-face{font-family:Reg;src:${woff2(regular.src)};font-weight:400;font-style:normal}` +
          `body{margin:0}div{font-size:40px;line-height:60px;height:60px;width:880px;padding:0 10px;white-space:pre;font-kerning:normal}` +
          `#t{font-family:Fam;font-synthesis:${synth}}#r{font-family:Ref;font-synthesis:none}#g{font-family:Reg;font-synthesis:none}`;
        const via = w === 700 && s === 'italic' ? '<strong><em>' : w === 700 ? '<strong>' : s === 'italic' && w === 400 ? '<em>' : 'font-weight/font-style';
        const t = via === '<strong><em>' ? `<strong><em>${TEXT}</em></strong>` : via === '<strong>' ? `<strong>${TEXT}</strong>` : via === '<em>' ? `<em>${TEXT}</em>`
          : `<span style="font-weight:${w};font-style:${s}">${TEXT}</span>`;
        const refWeight = fam.variable ? w : 400;
        await page.setContent(`<style>${css}</style><div id=t>${t}</div><div id=r><span style="font-weight:${refWeight}">${TEXT}</span></div><div id=g><span>${TEXT}</span></div>`);
        await page.evaluate(() => document.fonts.ready);
        const shot = async id => page.locator(id).screenshot();
        const [pt, pr, pg] = [await shot('#t'), await shot('#r'), await shot('#g')];
        const width = id => page.evaluate(id => document.querySelector(id).firstElementChild.getBoundingClientRect().width.toFixed(2), id);
        const [wt, wr] = [await width('#t'), await width('#r')];
        let result;
        if (ref && pt.equals(pr) && wt === wr) result = `real face (${path.basename(ref.src)}${fam.variable ? ` at wght ${w}` : ''})`;
        else if (!ref && pt.equals(pr)) result = `no ${w} ${s} face: CSS matching picks ${path.basename(refFace.src)}; not synthesized`;
        else if (!ref) result = `no ${w} ${s} face: SYNTHESIZED (differs from ${path.basename(refFace.src)}, the face CSS matching picks)`;
        else if (pt.equals(pg)) result = 'fell back to Regular';
        else result = 'DIFFERS from the real face (synthesized or wrong face)';
        out.push(`| ${w} ${s} | ${via.replace(/</g, '&lt;')} | ${synth} | ${result} | ${wt} | ${ref ? wr : '–'} |`);
      }
    }
    out.push('');
  }
  await browser.close();
  process.stdout.write(out.join('\n') + '\n');
})();
