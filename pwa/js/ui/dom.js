// Small DOM helpers. Text always goes in as text nodes; nothing here parses HTML.

// h('p', { class: 'x', dataset: { id } , onclick }, 'text', child, [more])
export function h(tag, props = null, ...children) {
  const e = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v === null || v === undefined || v === false) { continue; }
      if (k === 'class') { e.className = v; } else if (k === 'dataset') { Object.assign(e.dataset, v); } else if (k.startsWith('on') && typeof v === 'function') { e.addEventListener(k.slice(2), v); } else if (k === 'text') { e.textContent = v; } else if (v === true) { e.setAttribute(k, ''); } else { e.setAttribute(k, String(v)); }
    }
  }
  append(e, children);
  return e;
}

function append(e, children) {
  for (const c of children) {
    if (c === null || c === undefined || c === false) { continue; }
    if (Array.isArray(c)) { append(e, c); } else if (c instanceof Node) { e.append(c); } else { e.append(document.createTextNode(String(c))); }
  }
}

const SVG = 'http://www.w3.org/2000/svg';

// Icons are drawn here, not loaded: the CSP allows images from this site only, and these
// are the app's own simple marks, not the agents' trademarks.
const ICONS = {
  // Copilot: a pair of goggles.
  copilot: [['rect', { x: 3, y: 7, width: 8, height: 7, rx: 3 }], ['rect', { x: 13, y: 7, width: 8, height: 7, rx: 3 }], ['path', { d: 'M11 10h2M6 18c3 2 9 2 12 0' }]],
  // Claude: a sparkle.
  claude: [['path', { d: 'M12 3v18M3 12h18M5.6 5.6l12.8 12.8M18.4 5.6L5.6 18.4' }]],
  // Codex: a terminal prompt.
  codex: [['rect', { x: 3, y: 4, width: 18, height: 16, rx: 3 }], ['path', { d: 'M7 9l3 3-3 3M12 15h5' }]],
  unknown: [['circle', { cx: 12, cy: 12, r: 8 }], ['path', { d: 'M12 8v5M12 16v.5' }]],
  chevron: [['path', { d: 'M6 9l6 6 6-6' }]],
  machine: [['rect', { x: 3, y: 4, width: 18, height: 12, rx: 2 }], ['path', { d: 'M8 20h8M12 16v4' }]],
  paperclip: [['path', { d: 'M20 11.5l-7.8 7.8a5 5 0 01-7.1-7.1L13 4.4a3.3 3.3 0 014.7 4.7l-7.8 7.8a1.7 1.7 0 01-2.4-2.4l7.1-7.1' }]],
  file: [['path', { d: 'M14 3H7a2 2 0 00-2 2v14a2 2 0 002 2h10a2 2 0 002-2V8z' }], ['path', { d: 'M14 3v5h5M9 13h6M9 17h6' }]],
};

export function icon(name, cls = 'icon') {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', cls);
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  for (const [tag, attrs] of ICONS[name] ?? ICONS.unknown) {
    const s = document.createElementNS(SVG, tag);
    for (const [k, v] of Object.entries(attrs)) { s.setAttribute(k, String(v)); }
    svg.append(s);
  }
  return svg;
}
