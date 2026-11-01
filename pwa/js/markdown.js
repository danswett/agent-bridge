// Markdown for agent responses and questions, without raw HTML.
//
// Why our own and not a vendored renderer plus a sanitizer: the usual pair (marked or
// markdown-it, then DOMPurify) turns Markdown into an HTML string and then parses that
// string into the page, so safety rests on the sanitizer keeping up with every parser
// quirk. Here no string is ever parsed as HTML. The parser produces a small tree of
// tags from a fixed list, the DOM is built with createElement and text nodes, and links
// keep only http, https and mailto. Raw HTML in the input stays text. It is also about a
// tenth of the size, with no third-party code to keep updated under the page's CSP.
//
// Covered: paragraphs (line breaks kept, as a terminal shows them), headings, emphasis,
// strong, strikethrough, code spans, fenced code (wrapped, with a Copy button), bullet,
// numbered and task lists (nested), block quotes, rules, GitHub tables, links, autolinks and
// bare URLs. Images become links, never loads. Not covered: reference links, footnotes,
// indented code, HTML.

const SAFE_PROTOCOLS = new Set(['http:', 'https:', 'mailto:']);
const MAX_DEPTH = 12;

// An absolute http(s) or mailto URL, normalised, or null. Relative URLs are refused too:
// they would resolve against this app, not wherever the agent meant.
export function safeHref(raw) {
  if (typeof raw !== 'string') { return null; }
  const s = raw.trim();
  // Control characters and whitespace are how scheme checks are usually dodged
  // ("java\tscript:"); the URL parser strips some of them, so refuse them outright.
  if (s === '' || /[\u0000-\u0020\u007f-\u009f]/.test(s)) { return null; }
  let url;
  try { url = new URL(s); } catch { return null; }
  return SAFE_PROTOCOLS.has(url.protocol) ? url.href : null;
}

const el = (tag, children = [], attrs) => (attrs ? { tag, attrs, children } : { tag, children });

// ---------- blocks ----------

const RE = {
  fence: /^ {0,3}(`{3,}|~{3,})(.*)$/,
  heading: /^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/,
  rule: /^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$/,
  quote: /^ {0,3}> ?(.*)$/,
  item: /^( {0,12})([-*+]|\d{1,9}[.)])([ \t]+(.*))?$/,
  tableSep: /^ {0,3}\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$/,
};

const blank = (line) => /^[ \t]*$/.test(line);
const indentOf = (line) => line.match(/^ */)[0].length;
const expandTabs = (line) => line.replace(/^\t+/, (t) => '    '.repeat(t.length));

// The raw line with its first n columns of indentation removed, as expandTabs counts them
// (each leading tab four columns), keeping the tabs after them: a fenced Makefile in a list
// item must copy with its recipe tabs, not spaces.
function dropColumns(raw, n) {
  const tabs = raw.match(/^\t*/)[0].length;
  if (n > 4 * tabs) { return raw.slice(tabs).slice(n - 4 * tabs); }
  // A tab only partly inside the columns becomes spaces, as it always did: what is left must
  // still differ from the expanded line only in its leading tabs.
  return n % 4 === 0 ? raw.slice(n / 4) : expandTabs(raw).slice(n);
}

function startsBlock(line, next) {
  // Only a list starting at 1 can interrupt a paragraph, so "in\n2026. we shipped" stays
  // one paragraph (CommonMark's rule).
  const item = isItem(line) && (!/^ *\d/.test(line) || /^ *1[.)]/.test(line));
  return RE.fence.test(line) || RE.heading.test(line) || RE.rule.test(line) || RE.quote.test(line)
    || item || (line.includes('|') && next !== undefined && isTableSep(next));
}

function isItem(line) {
  const m = RE.item.exec(line);
  // "1." alone or "-" alone are items too, but "-" followed by nothing is too ambiguous
  // with a setext underline to start one mid-paragraph; requiring content keeps "---"
  // and "2026." out of lists.
  return !!m && m[4] !== undefined && m[4] !== '';
}

function isTableSep(line) { return RE.tableSep.test(line) && line.includes('-') && line.includes('|'); }

export function parseMarkdown(src) {
  const text = typeof src === 'string' ? src : '';
  const raw = text.replace(/\r\n?/g, '\n').split('\n');
  return parseBlocks(raw.map(expandTabs), raw, 0);
}

// lines: what structure is read from, leading tabs expanded and blank lines in list items
// emptied. raw: the same lines as written, cut at the same columns; a fenced block's body is
// taken from it, so its tabs and whitespace are shown and copied as the agent wrote them.
function parseBlocks(lines, raw, depth) {
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (blank(line)) { i++; continue; }

    if (depth >= MAX_DEPTH) {
      // Pathological nesting: the rest is shown as plain text, not dropped.
      out.push(el('p', inlineLines(lines.slice(i).filter((l) => !blank(l)), depth)));
      break;
    }

    let m = RE.fence.exec(line);
    if (m) {
      const marker = m[1];
      const body = [];
      i++;
      while (i < lines.length) {
        const close = /^ {0,3}(`{3,}|~{3,})[ \t]*$/.exec(lines[i]);
        if (close && close[1][0] === marker[0] && close[1].length >= marker.length) { i++; break; }
        body.push(raw[i]);
        i++;
      }
      // An unclosed fence runs to the end, as in CommonMark; a cut response often ends so.
      out.push(el('pre', [el('code', [body.join('\n')])]));
      continue;
    }

    m = RE.heading.exec(line);
    if (m) {
      // Card titles are h3; anything inside a card sits below them.
      const level = Math.min(6, m[1].length + 3);
      out.push(el(`h${level}`, parseInline(m[2] ?? '', depth)));
      i++;
      continue;
    }

    if (RE.rule.test(line)) { out.push(el('hr')); i++; continue; }

    if (RE.quote.test(line)) {
      const inner = [];
      const innerRaw = [];
      while (i < lines.length && !blank(lines[i])) {
        const q = RE.quote.exec(lines[i]);
        inner.push(q ? q[1] : lines[i]);
        innerRaw.push(q ? q[1] : raw[i]);
        i++;
      }
      out.push(el('blockquote', parseBlocks(inner, innerRaw, depth + 1)));
      continue;
    }

    if (isItem(line)) {
      i = parseList(lines, raw, i, depth, out);
      continue;
    }

    if (line.includes('|') && i + 1 < lines.length && isTableSep(lines[i + 1])) {
      i = parseTable(lines, i, depth, out);
      continue;
    }

    const para = [line];
    i++;
    while (i < lines.length && !blank(lines[i]) && !startsBlock(lines[i], lines[i + 1])) { para.push(lines[i]); i++; }
    out.push(el('p', inlineLines(para, depth)));
  }
  return out;
}

function inlineLines(lines, depth) {
  // Line breaks are kept: agents write for a terminal, where a newline is a newline.
  return parseInline(lines.map((l) => l.trim()).join('\n'), depth);
}

function parseList(lines, raw, i, depth, out) {
  const first = RE.item.exec(lines[i]);
  const base = first[1].length;
  const ordered = /\d/.test(first[2]);
  const delimiter = ordered ? first[2].slice(-1) : first[2];
  const items = [];
  while (i < lines.length) {
    const m = RE.item.exec(lines[i]);
    if (!m || m[1].length !== base || !isItem(lines[i]) || /\d/.test(m[2]) !== ordered || (ordered ? m[2].slice(-1) : m[2]) !== delimiter) { break; }
    const contentIndent = base + m[2].length + Math.min(4, m[3].length - m[4].length);
    const body = [m[4]];
    const bodyRaw = [m[4]];
    i++;
    while (i < lines.length) {
      const line = lines[i];
      if (blank(line)) {
        // A blank line continues the item only if what follows is indented under it.
        let j = i;
        while (j < lines.length && blank(lines[j])) { j++; }
        if (j < lines.length && indentOf(lines[j]) > base) {
          body.push('');
          // A blank line in a fence may still hold spaces or tabs that matter (a heredoc);
          // only the list's own indentation comes off.
          bodyRaw.push(dropColumns(raw[i], Math.min(indentOf(expandTabs(raw[i])), contentIndent)));
          i++;
          continue;
        }
        break;
      }
      const ind = indentOf(line);
      if (ind > base) {
        const cut = Math.min(ind, contentIndent);
        body.push(line.slice(cut));
        bodyRaw.push(dropColumns(raw[i], cut));
        i++;
        continue;
      }
      if (isItem(line) || startsBlock(line, lines[i + 1]) || body[body.length - 1] === '') { break; }
      body.push(line); // a lazy continuation of the item's paragraph
      bodyRaw.push(raw[i]);
      i++;
    }
    let blocks = parseBlocks(body, bodyRaw, depth + 1);
    // Tight items: the first paragraph's text sits in the <li> itself.
    if (blocks[0]?.tag === 'p') { blocks = [...blocks[0].children, ...blocks.slice(1)]; }
    const task = taskMarker(blocks);
    items.push(el('li', task ? [task, ...blocks] : blocks));
  }
  const start = ordered ? parseInt(first[2], 10) : null;
  out.push(el(ordered ? 'ol' : 'ul', items, ordered && start !== 1 ? { start: String(start) } : undefined));
  return i;
}

// "[ ] " or "[x] " at the start of an item: shown as a box, never a live checkbox.
function taskMarker(blocks) {
  if (typeof blocks[0] !== 'string') { return null; }
  const m = /^\[([ xX])\][ \t]+/.exec(blocks[0]);
  if (!m) { return null; }
  blocks[0] = blocks[0].slice(m[0].length);
  return m[1] === ' ' ? '☐ ' : '☑ ';
}

function splitRow(line) {
  let s = line.trim();
  if (s.startsWith('|')) { s = s.slice(1); }
  if (s.endsWith('|') && !s.endsWith('\\|')) { s = s.slice(0, -1); }
  const cells = [];
  let cur = '';
  for (let k = 0; k < s.length; k++) {
    if (s[k] === '\\' && s[k + 1] === '|') { cur += '|'; k++; continue; }
    if (s[k] === '|') { cells.push(cur.trim()); cur = ''; continue; }
    cur += s[k];
  }
  cells.push(cur.trim());
  return cells;
}

function parseTable(lines, i, depth, out) {
  const head = splitRow(lines[i]);
  const align = splitRow(lines[i + 1]).map((c) => (c.startsWith(':') && c.endsWith(':') ? 'al-c' : c.endsWith(':') ? 'al-r' : null));
  i += 2;
  const rows = [];
  while (i < lines.length && !blank(lines[i]) && lines[i].includes('|')) { rows.push(splitRow(lines[i])); i++; }
  const cell = (tag, text, k) => el(tag, parseInline(text, depth), align[k] ? { class: align[k] } : undefined);
  const table = el('table', [
    el('thead', [el('tr', head.map((c, k) => cell('th', c, k)))]),
    el('tbody', rows.map((r) => el('tr', head.map((_, k) => cell('td', r[k] ?? '', k))))),
  ]);
  // Wide tables scroll sideways inside the card instead of widening the page.
  out.push(el('div', [table], { class: 'md-table' }));
  return i;
}

// ---------- inline ----------

const PUNCT = /[!-/:-@[-`{-~]/;
const WORD = /[\p{L}\p{N}]/u;

export function parseInline(text, depth = 0) {
  const out = [];
  let buf = '';
  const flush = () => { if (buf) { out.push(buf); buf = ''; } };
  const push = (node) => { flush(); out.push(node); };
  // Delimiters known to have no closer from here on: cached so a long run of unmatched
  // "*" or "[" stays linear rather than rescanning the rest of the text each time.
  const noCloser = new Set();
  const n = text.length;
  let i = 0;
  while (i < n) {
    const c = text[i];

    if (c === '\\' && i + 1 < n && PUNCT.test(text[i + 1])) { buf += text[i + 1]; i += 2; continue; }
    if (c === '\n') { push(el('br')); i++; continue; }

    if (c === '`') {
      let run = 1;
      while (text[i + run] === '`') { run++; }
      const fence = '`'.repeat(run);
      let close = noCloser.has(fence) ? -1 : text.indexOf(fence, i + run);
      while (close !== -1 && text[close + run] === '`') {
        let k = close; while (text[k] === '`') { k++; }
        close = text.indexOf(fence, k);
      }
      if (close === -1) { noCloser.add(fence); buf += fence; i += run; continue; }
      let code = text.slice(i + run, close).replace(/\n/g, ' ');
      if (code.length > 1 && code.startsWith(' ') && code.endsWith(' ') && code.trim() !== '') { code = code.slice(1, -1); }
      push(el('code', [code]));
      i = close + run;
      continue;
    }

    if (c === '<') {
      const m = /^<([a-zA-Z][a-zA-Z0-9+.-]{1,31}:[^<>\s]*)>/.exec(text.slice(i, i + 2048));
      if (m) {
        const href = safeHref(m[1]);
        push(href ? el('a', [m[1]], { href }) : m[1]);
        i += m[0].length;
        continue;
      }
    }

    if ((c === '[' || (c === '!' && text[i + 1] === '[')) && depth < MAX_DEPTH && !noCloser.has('[')) {
      const image = c === '!';
      const link = parseLink(text, image ? i + 1 : i);
      if (link === null) { noCloser.add('['); } else if (link) {
        const href = safeHref(link.dest);
        const label = parseInline(link.label, depth + 1);
        if (image) {
          // Never loaded: an image is a request to wherever the agent pointed.
          push(href ? el('a', [link.label || link.dest], { href }) : (link.label || ''));
        } else if (href) {
          push(el('a', label, link.title ? { href, title: link.title } : { href }));
        } else {
          flush();
          out.push(...label);
        }
        i = link.end;
        continue;
      }
    }

    if ((c === '*' || c === '_' || c === '~') && depth < MAX_DEPTH) {
      const span = parseEmphasis(text, i, noCloser);
      if (span) {
        push(el(span.tag, parseInline(span.inner, depth + 1)));
        i = span.end;
        continue;
      }
    }

    if ((c === 'h' || c === 'H') && (i === 0 || !WORD.test(text[i - 1]))) {
      const m = /^https?:\/\/[^\s<>]+/i.exec(text.slice(i, i + 2048));
      if (m) {
        let url = m[0].replace(/[.,:;!?'"*_~]+$/, '');
        // A ")" closing a parenthesis the URL did not open belongs to the sentence.
        while (url.endsWith(')') && (url.match(/\(/g) ?? []).length < (url.match(/\)/g) ?? []).length) { url = url.slice(0, -1); }
        const href = safeHref(url);
        if (href) { push(el('a', [url], { href })); i += url.length; continue; }
      }
    }

    buf += c;
    i++;
  }
  flush();
  return out;
}

// [label](dest "title"). Returns {label, dest, title, end}, false if this "[" is not a
// link, or null if no "]" follows at all (so the caller stops looking).
function parseLink(text, open) {
  let depthB = 0, k = open;
  for (; k < text.length; k++) {
    const ch = text[k];
    if (ch === '\\') { k++; continue; }
    if (ch === '[') { depthB++; } else if (ch === ']') { depthB--; if (depthB === 0) { break; } }
  }
  if (k >= text.length) { return null; }
  if (text[k + 1] !== '(') { return false; }
  const label = text.slice(open + 1, k);
  let p = k + 2;
  while (text[p] === ' ' || text[p] === '\n') { p++; }
  let dest = '';
  if (text[p] === '<') {
    const end = text.indexOf('>', p);
    if (end === -1) { return false; }
    dest = text.slice(p + 1, end);
    p = end + 1;
  } else {
    let parens = 0;
    const start = p;
    for (; p < text.length; p++) {
      const ch = text[p];
      if (ch === '\\' && p + 1 < text.length) { p++; continue; }
      if (ch === '(') { parens++; } else if (ch === ')') { if (parens === 0) { break; } parens--; } else if (/\s/.test(ch)) { break; }
    }
    dest = text.slice(start, p);
  }
  while (text[p] === ' ' || text[p] === '\n') { p++; }
  let title = null;
  if (text[p] === '"' || text[p] === "'") {
    const q = text[p];
    const end = text.indexOf(q, p + 1);
    if (end === -1) { return false; }
    title = text.slice(p + 1, end);
    p = end + 1;
    while (text[p] === ' ') { p++; }
  }
  if (text[p] !== ')') { return false; }
  return { label, dest, title, end: p + 1 };
}

function parseEmphasis(text, i, noCloser) {
  const c = text[i];
  let run = 1;
  while (text[i + run] === c) { run++; }
  let len;
  if (c === '~') { if (run !== 2) { return null; } len = 2; } else { len = Math.min(run, 2); }
  const delim = c.repeat(len);
  const after = text[i + len];
  // An opener must be followed by non-space; "_" must also not sit inside a word
  // (snake_case_names stay as written).
  if (after === undefined || /\s/.test(after)) { return null; }
  if (c === '_' && i > 0 && WORD.test(text[i - 1])) { return null; }
  if (noCloser.has(delim)) { return null; }
  let from = i + len;
  for (;;) {
    const close = text.indexOf(delim, from);
    if (close === -1) { noCloser.add(delim); return null; }
    const before = text[close - 1];
    const next = text[close + len];
    const inner = text.slice(i + len, close);
    const okClose = close > i + len && !/\s/.test(before) && !inner.includes('\n\n')
      && !(c === '_' && next !== undefined && WORD.test(next))
      // "**" is not closed by the first half of a "***" run meant for an inner "*".
      && !(len === 1 && next === c);
    if (okClose) {
      return { tag: c === '~' ? 'del' : len === 2 ? 'strong' : 'em', inner, end: close + len };
    }
    from = close + 1;
  }
}

// ---------- DOM ----------

const TAGS = new Set(['p', 'h4', 'h5', 'h6', 'strong', 'em', 'del', 'code', 'pre', 'ul', 'ol', 'li',
  'blockquote', 'hr', 'br', 'a', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'div']);
const CLASSES = new Set(['al-c', 'al-r', 'md-table']);

// Builds the tree with createElement and text nodes only. Each tag and attribute is checked
// again here, so nothing a parser bug could produce reaches the page unchecked.
export function toDom(nodes, doc = document) {
  const frag = doc.createDocumentFragment();
  for (const node of nodes) { frag.append(build(node, doc)); }
  return frag;
}

function build(node, doc) {
  if (typeof node === 'string') { return doc.createTextNode(node); }
  if (!node || !TAGS.has(node.tag)) { return doc.createTextNode(''); }
  const e = doc.createElement(node.tag);
  const a = node.attrs ?? {};
  if (node.tag === 'a') {
    const href = safeHref(a.href);
    if (!href) { return doc.createTextNode(textOf(node)); }
    e.setAttribute('href', href);
    e.setAttribute('target', '_blank');
    e.setAttribute('rel', 'noopener noreferrer nofollow');
    if (typeof a.title === 'string') { e.setAttribute('title', a.title); }
  }
  if (node.tag === 'ol' && /^\d{1,9}$/.test(a.start ?? '')) { e.setAttribute('start', a.start); }
  if (CLASSES.has(a.class)) { e.className = a.class; }
  for (const child of node.children ?? []) { e.append(build(child, doc)); }
  // The Copy button is made here and never by the parser: 'button' is not in TAGS, so no
  // reply text can produce one, and the only handler on it is ours.
  if (node.tag === 'pre') { return codeBlock(e, textOf(node), doc); }
  return e;
}

const COPY_RESET_MS = 1500;

// A code block with a Copy button in its top-right corner. On a phone, a long command (a
// 1000-character pwsh -EncodedCommand, say) is all but impossible to select by hand. What
// is copied is the block's text as the agent wrote it, not as it is wrapped on screen.
// The button sits beside the <pre>, not in it, so the block's own text stays the code.
function codeBlock(pre, text, doc) {
  const label = doc.createElement('span');
  label.textContent = 'Copy';
  const button = doc.createElement('button');
  button.type = 'button';
  button.className = 'md-copy';
  button.setAttribute('aria-label', 'Copy code');
  button.append(label);
  // The button's name stays "Copy code"; what happened is announced separately.
  const status = doc.createElement('span');
  status.className = 'sr-only';
  status.setAttribute('role', 'status');
  let timer = null;
  button.addEventListener('click', async () => {
    const ok = await copyText(text, doc);
    const said = ok ? 'Copied' : 'Copy failed';
    label.textContent = said;
    status.textContent = said;
    button.dataset.copy = ok ? 'done' : 'failed';
    clearTimeout(timer);
    timer = setTimeout(() => {
      label.textContent = 'Copy';
      status.textContent = '';
      delete button.dataset.copy;
    }, COPY_RESET_MS);
  });
  const wrap = doc.createElement('div');
  wrap.className = 'md-code';
  wrap.append(button, pre, status);
  return wrap;
}

// Puts text on the clipboard; true if it got there. The Clipboard API first. It is missing
// in older WebViews, and can reject where it exists (iOS Home Screen apps have refused it),
// so a hidden textarea and execCommand('copy') follow, as clipboard.js does it.
export async function copyText(text, doc = document) {
  const clipboard = doc.defaultView?.navigator?.clipboard;
  if (typeof clipboard?.writeText === 'function') {
    try {
      await clipboard.writeText(text);
      return true;
    } catch { /* refused here; the older way may still be allowed */ }
  }
  return copyBySelection(text, doc);
}

function copyBySelection(text, doc) {
  const area = doc.createElement('textarea');
  area.className = 'md-copy-buffer';
  area.value = text;
  // Read-only, so focusing it never raises the keyboard.
  area.setAttribute('readonly', '');
  area.setAttribute('aria-hidden', 'true');
  area.tabIndex = -1;
  const before = doc.activeElement;
  doc.body.append(area);
  try {
    area.focus({ preventScroll: true });
    area.select();
    // iOS ignores select() on a read-only field; a selection range it honours.
    area.setSelectionRange(0, text.length);
    return doc.execCommand('copy') === true;
  } catch {
    return false;
  } finally {
    area.remove();
    before?.focus?.({ preventScroll: true });
  }
}

export function textOf(node) {
  if (typeof node === 'string') { return node; }
  return (node?.children ?? []).map(textOf).join('');
}

export function renderMarkdown(text, doc = document) {
  return toDom(parseMarkdown(text), doc);
}
