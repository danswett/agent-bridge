// Renders the design canvas boards (.dc.html) to PNG, offline.
// Implements only what the boards use: {{holes}}, sc-if, sc-for, dc-import, and a
// DCLogic class with props, state and renderVals(). Usage: node render.js <projectDir> <outDir>
const fs = require('fs'), path = require('path');
const { chromium } = require('playwright');
const [projectDir, outDir] = process.argv.slice(2);
const index = JSON.parse(fs.readFileSync(path.join(projectDir, 'canvas.json'), 'utf8'));
const files = {};
for (const f of fs.readdirSync(projectDir)) if (f.endsWith('.dc.html')) files[f] = fs.readFileSync(path.join(projectDir, f), 'utf8');

const nm = p => path.join(__dirname, 'node_modules', p);
// @font-face can't redefine generic families, so system-ui, sans-serif and -apple-system
// would resolve to whatever the rendering machine has installed (a fontconfig alias only
// worked where Inter was installed system-wide). The renderer rewrites them to this
// embedded alias instead, in the rendered DOM only; the canvas source keeps its stacks.
const STAND_IN = 'Agent Bridge Stand-in Sans';
// Self-contained, because its source is also evaluated inside the page.
function standIn(css) {
  const generic = /(^|,)\s*(system-ui|-apple-system|BlinkMacSystemFont|sans-serif)\s*(?=,|$)/gi;
  return css.replace(/font-family\s*:\s*([^;}]+)/gi, (_, list) =>
    'font-family: ' + list.trim().replace(generic, "$1 'Agent Bridge Stand-in Sans'").trim());
}
const font = (fam, file, extra = '') => `@font-face{font-family:'${fam}';src:url(data:font/woff2;base64,${fs.readFileSync(nm(file)).toString('base64')}) format('woff2');font-weight:100 900;${extra}}`;
const inter = '@fontsource-variable/inter/files/inter-latin-wght-normal.woff2';
const fontCss = [
  // Stand-ins: Segoe UI and SF Pro are not licensed for this machine, so Inter takes their place.
  ...['Segoe UI Variable Text', 'Segoe UI Variable Display', 'Segoe UI', 'SF Pro Text', 'SF Pro Display', 'Helvetica Neue', 'Google Sans', STAND_IN].map(f => font(f, inter)),
  font('Roboto Flex', '@fontsource-variable/roboto-flex/files/roboto-flex-latin-full-normal.woff2'),
  font('Roboto', '@fontsource-variable/roboto-flex/files/roboto-flex-latin-full-normal.woff2'),
  // The real monospace, from the repository (design/fonts/agent-bridge-mono).
  ...[['Regular', 400, 'normal'], ['SemiBold', 600, 'normal'], ['Bold', 700, 'normal'], ['Italic', 400, 'italic']].map(([n, w, st]) => `@font-face{font-family:'Agent Bridge Mono';src:url(data:font/woff2;base64,${fs.readFileSync(path.join(__dirname, '../../design/fonts/agent-bridge-mono/woff2/AgentBridgeMono-' + n + '.woff2')).toString('base64')}) format('woff2');font-weight:${w};font-style:${st}}`),
  ...['Cascadia Mono', 'SF Mono', 'Roboto Mono'].map(f => `@font-face{font-family:'${f}';src:url(data:font/woff2;base64,${fs.readFileSync(nm('@fontsource/cascadia-mono/files/cascadia-mono-latin-400-normal.woff2')).toString('base64')}) format('woff2');font-weight:400}@font-face{font-family:'${f}';src:url(data:font/woff2;base64,${fs.readFileSync(nm('@fontsource/cascadia-mono/files/cascadia-mono-latin-600-normal.woff2')).toString('base64')}) format('woff2');font-weight:600}`)
].join('\n');

async function renderBoard(page, file, w, h) {
  return page.evaluate(({ files, file, fontCss, standInSrc }) => {
    const standIn = new Function('return (' + standInSrc + ')')();
    class DCLogic { constructor(props) { this.props = props || {}; this.state = {}; } setState() {} forceUpdate() {} }
    const styles = new Set();
    const parse = (src) => {
      const doc = new DOMParser().parseFromString(src, 'text/html');
      const xdc = doc.querySelector('x-dc');
      const helmet = xdc.querySelector('helmet');
      if (helmet) { helmet.querySelectorAll('style').forEach(s => styles.add(s.textContent)); helmet.remove(); }
      const script = doc.querySelector('script[data-dc-script]');
      const props = JSON.parse(script.getAttribute('data-props') || '{}');
      const Comp = new Function('DCLogic', script.textContent + '\n;return Component;')(DCLogic);
      return { xdc, props, Comp };
    };
    const lookup = (scope, expr) => {
      expr = expr.trim();
      if (expr === 'true') return true; if (expr === 'false') return false;
      if (/^-?\d+(\.\d+)?$/.test(expr)) return Number(expr);
      return expr.split('.').reduce((o, k) => (o == null ? undefined : o[k]), scope);
    };
    const whole = /^\{\{([^}]+)\}\}$/;
    const interp = (s, scope) => s.replace(/\{\{([^}]+)\}\}/g, (_, e) => { const v = lookup(scope, e); return v == null ? '' : String(v); });
    const camel = s => s.replace(/-([a-z])/g, (_, c) => c.toUpperCase());

    function component(name, passed) {
      const { xdc, props, Comp } = parse(files[name + '.dc.html'] || files[name]);
      const merged = {};
      for (const [k, v] of Object.entries(props)) if (k !== '$preview' && v && 'default' in v) merged[k] = v.default;
      Object.assign(merged, passed);
      const inst = new Comp(merged); inst.props = merged;
      const vals = inst.renderVals();
      const frag = document.createDocumentFragment();
      for (const child of [...xdc.childNodes]) { const r = walk(child, vals); if (r) frag.appendChild(r); }
      return frag;
    }

    function walk(node, scope) {
      if (node.nodeType === 3) return document.createTextNode(interp(node.textContent, scope));
      if (node.nodeType !== 1) return null;
      const tag = node.tagName.toLowerCase();
      if (tag === 'sc-if') {
        const v = lookup(scope, (node.getAttribute('value') || '').replace(/^\{\{|\}\}$/g, ''));
        if (!v) return null;
        const f = document.createDocumentFragment();
        for (const c of [...node.childNodes]) { const r = walk(c, scope); if (r) f.appendChild(r); }
        return f;
      }
      if (tag === 'sc-for') {
        const list = lookup(scope, (node.getAttribute('list') || '').replace(/^\{\{|\}\}$/g, '')) || [];
        const as = node.getAttribute('as') || 'item';
        const f = document.createDocumentFragment();
        list.forEach((item, i) => { for (const c of [...node.childNodes]) { const r = walk(c, { ...scope, [as]: item, $index: i }); if (r) f.appendChild(r); } });
        return f;
      }
      if (tag === 'dc-import') {
        const passed = {};
        for (const a of node.attributes) {
          if (a.name === 'name' || a.name.startsWith('hint-')) continue;
          const m = a.value.match(whole);
          passed[camel(a.name)] = m ? lookup(scope, m[1]) : interp(a.value, scope);
        }
        return component(node.getAttribute('name'), passed);
      }
      // HTML elements must be created by lower-case name, or the browser treats them as unknown inline elements.
      const el = node.namespaceURI === 'http://www.w3.org/1999/xhtml' ? document.createElement(node.localName) : document.createElementNS(node.namespaceURI, node.localName);
      for (const a of node.attributes) {
        if (/^on[A-Z]/.test(a.name) || /^on[a-z]+$/.test(a.name)) continue;
        const m = a.value.match(whole);
        const v = m ? lookup(scope, m[1]) : interp(a.value, scope);
        const s = v == null ? '' : String(v);
        el.setAttribute(a.name, a.name === 'style' ? standIn(s) : s);
      }
      for (const c of [...node.childNodes]) { const r = walk(c, scope); if (r) el.appendChild(r); }
      return el;
    }

    document.head.innerHTML = '';
    document.body.innerHTML = '';
    const root = component(file, {});
    const st = document.createElement('style');
    st.textContent = fontCss + '\n' + standIn([...styles].join('\n')) + '\nhtml,body{margin:0}';
    document.head.appendChild(st);
    const wrap = document.createElement('div'); wrap.id = 'board'; wrap.style.display = 'inline-block';
    wrap.appendChild(root); document.body.appendChild(wrap);
    return true;
  }, { files, file, fontCss, standInSrc: standIn.toString() });
}

(async () => {
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await chromium.launch();
  const skip = new Set(['Glyph.dc.html', 'Icon.dc.html']);
  for (const file of index.order) {
    if (skip.has(file)) continue;
    const b = index.boards[file];
    const phone = b.w < 500;
    const page = await browser.newPage({ viewport: { width: b.w, height: b.h }, deviceScaleFactor: phone ? 2 : 1 });
    await page.setContent('<!doctype html><html><head></head><body></body></html>');
    await renderBoard(page, file, b.w, b.h);
    await page.evaluate(() => document.fonts.ready);
    const out = path.join(outDir, file.replace('.dc.html', '').toLowerCase() + '.png');
    await page.screenshot({ path: out, clip: { x: 0, y: 0, width: b.w, height: b.h } });
    console.log('wrote', path.basename(out), `${b.w}x${b.h}${phone ? '@2x' : ''}`);
    await page.close();
  }
  await browser.close();
})();
