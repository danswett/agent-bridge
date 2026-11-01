// Reads the design's SVGs (design/glyphs, design/icons): a deliberately small subset of SVG,
// so every client can be generated from it exactly. Anything outside the subset is refused
// rather than half-converted: svg, g, path, circle, rect and line; rotate() and translate()
// transforms; and colour only as currentColor, none, or a hard-coded colour on a part whose
// class says which token replaces it (design/README.md, "Colour comes from the role").

import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';

const SHAPES = new Set(['path', 'circle', 'rect', 'line']);
// The classes design/README.md defines. A class outside it would be silently drawn as currentColor.
export const PART_CLASSES = ['track', 'spin', 'orbit', 'ink', 'knockout'];
// Parts drawn from a token rather than from currentColor: the hard-coded colour in the file is
// only how it looks in a plain SVG viewer.
export const TOKEN_CLASSES = { ink: 'ink', knockout: 'knockout' };
// Parts that move: clients get them separately so they can turn them (README, "Motion").
export const MOVING_CLASSES = ['spin', 'orbit'];

// --- a tokenizer for the XML these files use: elements, attributes, no text ---

export function parseXml(source, file = 'svg', { text = false } = {}) {
  const stack = [{ tag: '#root', attrs: {}, children: [] }];
  const re = /<!--[\s\S]*?-->|<\?[\s\S]*?\?>|<\/([\w:.-]+)\s*>|<([\w:.-]+)((?:\s+[\w:.-]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>|([^<]+)/g;
  let m;
  while ((m = re.exec(source))) {
    // XML forbids -- inside a comment, and a browser then drops the whole file, silently.
    if (m[0].startsWith('<!--') && m[0].slice(4, -3).includes('--')) { throw new Error(`${file}: a comment holds --, which XML forbids`); }
    if (m[0].startsWith('<!--') || m[0].startsWith('<?')) { continue; }
    if (m[5] !== undefined) {
      if (m[5].trim() && !text) { throw new Error(`${file}: text content is not part of the design subset: ${m[5].trim().slice(0, 40)}`); }
      if (m[5].trim()) { stack[stack.length - 1].text = (stack[stack.length - 1].text ?? '') + m[5].trim(); }
      continue;
    }
    if (m[1]) {
      const open = stack.pop();
      if (open.tag !== m[1]) { throw new Error(`${file}: </${m[1]}> closes <${open.tag}>`); }
      continue;
    }
    const attrs = {};
    for (const a of m[3].matchAll(/([\w:-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g)) { attrs[a[1]] = a[2] ?? a[3]; }
    const node = { tag: m[2], attrs, children: [] };
    stack[stack.length - 1].children.push(node);
    if (!m[4]) { stack.push(node); }
  }
  if (stack.length !== 1) { throw new Error(`${file}: <${stack[stack.length - 1].tag}> is never closed`); }
  if (stack[0].children.length !== 1) { throw new Error(`${file}: expected one root element`); }
  return stack[0].children[0];
}

// --- path data ---

const ARGS = { M: 2, L: 2, H: 1, V: 1, C: 6, S: 4, Q: 4, T: 2, A: 7, Z: 0 };

// SVG path data into absolute commands: [{ c: 'M', p: [x, y] }, …]. H and V become L; the
// smooth and quadratic forms are refused, since no design file uses them and XAML spells them
// differently enough to deserve a test of their own when one does.
export function parsePathData(d, file = 'path') {
  const out = [];
  let i = 0;
  const skip = () => { while (i < d.length && /[\s,]/.test(d[i])) { i++; } };
  const number = () => {
    skip();
    const m = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/.exec(d.slice(i));
    if (!m) { throw new Error(`${file}: expected a number at ${i} in "${d}"`); }
    i += m[0].length;
    return Number(m[0]);
  };
  const flag = () => {
    skip();
    if (d[i] !== '0' && d[i] !== '1') { throw new Error(`${file}: expected an arc flag at ${i} in "${d}"`); }
    return d[i++] === '1' ? 1 : 0;
  };
  let x = 0, y = 0, sx = 0, sy = 0, cmd = null;
  skip();
  while (i < d.length) {
    if (/[a-zA-Z]/.test(d[i])) { cmd = d[i++]; } else if (!cmd) { throw new Error(`${file}: expected a command at ${i} in "${d}"`); } else if (cmd === 'M') { cmd = 'L'; } else if (cmd === 'm') { cmd = 'l'; }
    const C = cmd.toUpperCase();
    if (!(C in ARGS) || 'STQ'.includes(C)) { throw new Error(`${file}: path command ${cmd} is not in the design subset`); }
    const rel = cmd !== C;
    // Z takes no numbers, so one after it is an error, not a Z repeated without end.
    if (C === 'Z') { out.push({ c: 'Z', p: [] }); x = sx; y = sy; cmd = null; skip(); continue; }
    if (C === 'A') {
      const [rx, ry, rot] = [number(), number(), number()];
      const [large, sweep] = [flag(), flag()];
      let [ex, ey] = [number(), number()];
      if (rel) { ex += x; ey += y; }
      out.push({ c: 'A', p: [rx, ry, rot, large, sweep, ex, ey] });
      [x, y] = [ex, ey];
    } else if (C === 'H' || C === 'V') {
      const v = number();
      if (C === 'H') { x = rel ? x + v : v; } else { y = rel ? y + v : v; }
      out.push({ c: 'L', p: [x, y] });
    } else {
      const p = [];
      for (let k = 0; k < ARGS[C]; k++) { p.push(number()); }
      if (rel) { for (let k = 0; k < p.length; k += 2) { p[k] += x; p[k + 1] += y; } }
      [x, y] = [p[p.length - 2], p[p.length - 1]];
      if (C === 'M') { [sx, sy] = [x, y]; }
      out.push({ c: C, p });
    }
    skip();
  }
  return out;
}

// --- shapes as path commands, starting where SVG starts them, so dashes fall the same way ---

function shapeCommands(node, file) {
  const n = (k, dflt) => {
    const v = node.attrs[k];
    if (v === undefined) {
      if (dflt === undefined) { throw new Error(`${file}: <${node.tag}> needs ${k}`); }
      return dflt;
    }
    return Number(v);
  };
  switch (node.tag) {
    case 'path': return parsePathData(node.attrs.d ?? '', file);
    case 'line': return [{ c: 'M', p: [n('x1', 0), n('y1', 0)] }, { c: 'L', p: [n('x2', 0), n('y2', 0)] }];
    case 'circle': {
      const [cx, cy, r] = [n('cx', 0), n('cy', 0), n('r')];
      // From 3 o'clock, clockwise, as SVG draws a circle.
      return [
        { c: 'M', p: [cx + r, cy] },
        { c: 'A', p: [r, r, 0, 0, 1, cx - r, cy] },
        { c: 'A', p: [r, r, 0, 0, 1, cx + r, cy] },
        { c: 'Z', p: [] },
      ];
    }
    case 'rect': {
      const [x, y, w, h] = [n('x', 0), n('y', 0), n('width'), n('height')];
      let rx = node.attrs.rx !== undefined ? Number(node.attrs.rx) : node.attrs.ry !== undefined ? Number(node.attrs.ry) : 0;
      let ry = node.attrs.ry !== undefined ? Number(node.attrs.ry) : rx;
      rx = Math.min(rx, w / 2); ry = Math.min(ry, h / 2);
      if (!rx || !ry) {
        return [{ c: 'M', p: [x, y] }, { c: 'L', p: [x + w, y] }, { c: 'L', p: [x + w, y + h] }, { c: 'L', p: [x, y + h] }, { c: 'Z', p: [] }];
      }
      const arc = (ex, ey) => ({ c: 'A', p: [rx, ry, 0, 0, 1, ex, ey] });
      return [
        { c: 'M', p: [x + rx, y] }, { c: 'L', p: [x + w - rx, y] }, arc(x + w, y + ry),
        { c: 'L', p: [x + w, y + h - ry] }, arc(x + w - rx, y + h),
        { c: 'L', p: [x + rx, y + h] }, arc(x, y + h - ry),
        { c: 'L', p: [x, y + ry] }, arc(x + rx, y), { c: 'Z', p: [] },
      ];
    }
    default: throw new Error(`${file}: <${node.tag}> is not in the design subset`);
  }
}

// rotate(a [cx cy]) and translate(x [y]), applied left to right as SVG composes them.
function parseTransform(t, file) {
  const ops = [];
  for (const m of t.matchAll(/(\w+)\s*\(([^)]*)\)/g)) {
    const a = m[2].split(/[\s,]+/).filter(Boolean).map(Number);
    if (m[1] === 'rotate') { ops.push({ rotate: a[0], cx: a[1] ?? 0, cy: a[2] ?? 0 }); } else if (m[1] === 'translate') { ops.push({ tx: a[0], ty: a[1] ?? 0 }); } else { throw new Error(`${file}: transform ${m[1]}() is not in the design subset`); }
  }
  if (t.replace(/(\w+)\s*\(([^)]*)\)/g, '').trim()) { throw new Error(`${file}: cannot read transform "${t}"`); }
  return ops;
}

function transformPoint([x, y], ops) {
  for (const op of [...ops].reverse()) {
    if ('rotate' in op) {
      const r = (op.rotate * Math.PI) / 180;
      const [dx, dy] = [x - op.cx, y - op.cy];
      [x, y] = [op.cx + dx * Math.cos(r) - dy * Math.sin(r), op.cy + dx * Math.sin(r) + dy * Math.cos(r)];
    } else {
      [x, y] = [x + op.tx, y + op.ty];
    }
  }
  return [x, y];
}

function transformCommands(cmds, ops) {
  if (!ops.length) { return cmds; }
  const turn = ops.reduce((s, op) => s + (op.rotate ?? 0), 0);
  return cmds.map(({ c, p }) => {
    if (c === 'Z') { return { c, p }; }
    if (c === 'A') {
      const [ex, ey] = transformPoint([p[5], p[6]], ops);
      return { c, p: [p[0], p[1], (p[2] + turn) % 360, p[3], p[4], ex, ey] };
    }
    const q = [];
    for (let k = 0; k < p.length; k += 2) { q.push(...transformPoint([p[k], p[k + 1]], ops)); }
    return { c, p: q };
  });
}

export const fmt = (v) => {
  const r = Math.round(v * 1000) / 1000;
  return Object.is(r, -0) ? '0' : String(r);
};

// Path commands in the syntax SVG and XAML's path markup both read: absolute, explicit
// separators, no concatenated arc flags.
export function formatCommands(cmds) {
  return cmds.map(({ c, p }) => {
    if (c === 'Z') { return 'Z'; }
    if (c === 'A') { return `A${fmt(p[0])},${fmt(p[1])} ${fmt(p[2])} ${p[3]} ${p[4]} ${fmt(p[5])},${fmt(p[6])}`; }
    const pts = [];
    for (let k = 0; k < p.length; k += 2) { pts.push(`${fmt(p[k])},${fmt(p[k + 1])}`); }
    return `${c}${pts.join(' ')}`;
  }).join(' ');
}

// --- a design SVG as data ---

const INHERITED = ['fill', 'stroke', 'stroke-width', 'stroke-linecap', 'stroke-linejoin', 'stroke-dasharray'];
const ALLOWED = new Set([...INHERITED, 'class', 'transform', 'opacity', 'stroke-opacity', 'fill-opacity',
  'd', 'cx', 'cy', 'r', 'x', 'y', 'width', 'height', 'rx', 'ry', 'x1', 'y1', 'x2', 'y2']);

function checkColour(value, classes, file, what) {
  if (value === undefined || value === 'none' || value === 'currentColor') { return; }
  if (classes.some((c) => c in TOKEN_CLASSES)) { return; }
  throw new Error(`${file}: ${what}="${value}" on a part with no token class; colour comes from the role (design/README.md)`);
}

// { name, kind, size, root: { attrs }, tree: [{ tag: 'g', name, classes, children } | part] }
// where a part is { tag, attrs (its own, as written), style (inherited and own presentation),
// classes (its own and its groups'), commands (as absolute path commands, transformed) }.
export function readDesignSvg(file, kind) {
  const source = readFileSync(file, 'utf8');
  const name = path.basename(file, '.svg');
  const root = parseXml(source, file);
  if (root.tag !== 'svg') { throw new Error(`${file}: the root must be <svg>`); }
  const vb = (root.attrs.viewBox ?? '').split(/[\s,]+/).map(Number);
  if (vb.length !== 4 || vb[0] !== 0 || vb[1] !== 0 || vb[2] !== vb[3]) { throw new Error(`${file}: needs a square viewBox from 0 0`); }
  const rootStyle = {};
  for (const k of INHERITED) { if (root.attrs[k] !== undefined) { rootStyle[k] = root.attrs[k]; } }
  checkColour(root.attrs.fill, [], file, 'fill');
  checkColour(root.attrs.stroke, [], file, 'stroke');

  const walk = (node, inherited, classes, ops) => {
    for (const k of Object.keys(node.attrs)) { if (!ALLOWED.has(k)) { throw new Error(`${file}: attribute ${k} is not in the design subset`); } }
    const own = (node.attrs.class ?? '').split(/\s+/).filter(Boolean);
    for (const c of own) { if (!PART_CLASSES.includes(c)) { throw new Error(`${file}: class "${c}" is not one design/README.md defines`); } }
    const all = [...classes, ...own];
    const style = { ...inherited };
    for (const k of [...INHERITED, 'opacity', 'stroke-opacity', 'fill-opacity']) { if (node.attrs[k] !== undefined) { style[k] = node.attrs[k]; } }
    checkColour(node.attrs.fill, all, file, 'fill');
    checkColour(node.attrs.stroke, all, file, 'stroke');
    const myOps = node.attrs.transform ? [...ops, ...parseTransform(node.attrs.transform, file)] : ops;
    if (node.tag === 'g') {
      if (node.attrs.opacity !== undefined) { throw new Error(`${file}: opacity on a group is not in the design subset`); }
      return { tag: 'g', attrs: node.attrs, classes: own, allClasses: all, children: node.children.map((c) => walk(c, style, all, myOps)) };
    }
    if (!SHAPES.has(node.tag)) { throw new Error(`${file}: <${node.tag}> is not in the design subset`); }
    if (node.children.length) { throw new Error(`${file}: <${node.tag}> may not have children`); }
    // SVG fills a shape black unless told otherwise; only an open line may leave fill unsaid.
    if (style.fill === undefined && (node.tag === 'circle' || node.tag === 'rect' || /z/i.test(node.attrs.d ?? ''))) {
      throw new Error(`${file}: a closed <${node.tag}> needs a fill (currentColor or none)`);
    }
    for (const k of ['opacity', 'stroke-opacity', 'fill-opacity']) { delete style[k]; }
    // XAML has one opacity per Path. Paint opacities inherit from groups; opacity does not.
    const paints = ['fill', 'stroke'].filter((p) => style[p] !== undefined && style[p] !== 'none');
    const paintOpacity = paints.map((p) => Number(node.attrs[`${p}-opacity`] ?? inherited[`${p}-opacity`] ?? 1));
    if (paintOpacity.length === 2 && paintOpacity[0] !== paintOpacity[1]) {
      throw new Error(`${file}: a part's fill and stroke opacities must match, since XAML has one opacity per path`);
    }
    const opacity = Number(node.attrs.opacity ?? 1) * (paintOpacity[0] ?? 1);
    return { tag: node.tag, attrs: node.attrs, style, opacity, classes: all, ownClasses: own, commands: transformCommands(shapeCommands(node, file), myOps) };
  };
  const tree = root.children.map((c) => walk(c, rootStyle, [], []));
  return { name, kind, size: vb[2], file, rootStyle, tree };
}

export function readDesignDir(dir, kind) {
  return readdirSync(dir).filter((f) => f.endsWith('.svg')).sort().map((f) => readDesignSvg(path.join(dir, f), kind));
}

export function parts(svg) {
  const out = [];
  const walk = (n) => { if (n.tag === 'g') { n.children.forEach(walk); } else { out.push(n); } };
  svg.tree.forEach(walk);
  return out;
}
