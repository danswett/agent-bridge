import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseMarkdown, safeHref, textOf } from '../../js/markdown.js';

// Every element and link the parser would put on the page.
function walk(nodes, out = { tags: [], hrefs: [], text: '' }) {
  for (const n of nodes) {
    if (typeof n === 'string') { out.text += n; continue; }
    out.tags.push(n.tag);
    if (n.attrs?.href !== undefined) { out.hrefs.push(n.attrs.href); }
    walk(n.children ?? [], out);
  }
  return out;
}

const ALLOWED = new Set(['p', 'h4', 'h5', 'h6', 'strong', 'em', 'del', 'code', 'pre', 'ul', 'ol', 'li', 'blockquote', 'hr', 'br', 'a', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'div']);

test('raw HTML stays text and never becomes an element', () => {
  const md = '<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n<a href="javascript:alert(1)">x</a> <iframe src="https://evil.example"></iframe>';
  const r = walk(parseMarkdown(md));
  assert.ok(r.tags.every((t) => ALLOWED.has(t)), r.tags.join());
  assert.ok(r.text.includes('<script>alert(1)</script>'));
  assert.ok(r.text.includes('<img src=x onerror=alert(1)>'));
  assert.ok(r.hrefs.every((h) => h.startsWith('https://')), r.hrefs.join());
});

test('links keep only http, https and mailto, however the scheme is disguised', () => {
  const bad = [
    'javascript:alert(1)', 'JaVaScRiPt:alert(1)', ' javascript:alert(1)', 'java\tscript:alert(1)', 'java\nscript:alert(1)',
    'javascript&#58;alert(1)', '&#x6A;avascript:alert(1)', 'data:text/html,<script>alert(1)</script>', 'vbscript:msgbox(1)',
    'file:///etc/passwd', '//evil.example/x', '/relative', 'relative/path', '\u0000javascript:alert(1)', 'blob:https://x/y',
  ];
  for (const href of bad) {
    assert.equal(safeHref(href), null, JSON.stringify(href));
    const r = walk(parseMarkdown(`[click](${href})`));
    assert.ok(r.hrefs.every((h) => /^(https?|mailto):/.test(h)), `${JSON.stringify(href)} -> ${r.hrefs}`);
  }
  assert.equal(safeHref('https://example.com/a?b=c#d'), 'https://example.com/a?b=c#d');
  assert.equal(safeHref('mailto:a@example.com'), 'mailto:a@example.com');
});

test('a link with an unsafe target keeps its text and loses the link', () => {
  const nodes = parseMarkdown('see [the docs](javascript:alert(1)) now');
  assert.equal(textOf({ children: nodes }), 'see the docs now');
  assert.deepEqual(walk(nodes).hrefs, []);
});

test('autolinks and bare URLs link only safe schemes', () => {
  const r = walk(parseMarkdown('<javascript:alert(1)> <https://ok.example/x> and https://github.com/a/b.'));
  assert.deepEqual(r.hrefs, ['https://ok.example/x', 'https://github.com/a/b']);
});

test('images are never loaded: they become links, or text when unsafe', () => {
  const r = walk(parseMarkdown('![alt](https://img.example/a.png) ![x](javascript:alert(1))'));
  assert.ok(!r.tags.includes('img'));
  assert.deepEqual(r.hrefs, ['https://img.example/a.png']);
});

test('paragraphs, emphasis, code and lists render as their elements', () => {
  const nodes = parseMarkdown('A **bold** and *em* and `code` and ~~gone~~.\nNext line.\n\n- one\n- two\n  1. nested\n\n```\n<b>raw</b>\n```');
  assert.deepEqual(nodes.map((n) => n.tag), ['p', 'ul', 'pre']);
  const r = walk(nodes);
  for (const tag of ['strong', 'em', 'code', 'del', 'br', 'li', 'ol']) { assert.ok(r.tags.includes(tag), tag); }
  assert.ok(r.text.includes('<b>raw</b>'), 'code blocks keep their text as text');
});

test('snake_case words are not emphasis', () => {
  const r = walk(parseMarkdown('call read_session_list and _this_'));
  assert.deepEqual(r.tags.filter((t) => t === 'em').length, 1);
  assert.ok(r.text.includes('read_session_list'));
});

test('headings sit below the card title, and tables are built cell by cell', () => {
  const nodes = parseMarkdown('# Top\n\n| a | b |\n|---|--:|\n| 1 | 2 |');
  assert.equal(nodes[0].tag, 'h4');
  const r = walk(nodes);
  assert.equal(r.tags.filter((t) => t === 'td').length, 2);
});

test('pathological input finishes quickly', () => {
  const inputs = ['*a '.repeat(6000), '['.repeat(16000), '[]('.repeat(5000), '`'.repeat(16000), '_x'.repeat(8000), '> '.repeat(8000) + 'x', '- '.repeat(4000) + 'x', '**'.repeat(8000), `${'['.repeat(4000)}${']'.repeat(4000)}`];
  for (const s of inputs) {
    const t0 = performance.now();
    parseMarkdown(s);
    const ms = performance.now() - t0;
    assert.ok(ms < 1500, `${JSON.stringify(s.slice(0, 12))}… took ${ms.toFixed(0)} ms`);
  }
});

test('a button written in the text stays text, in a paragraph and in a code block', () => {
  const md = '<button onclick="window.__xss=1">Copy</button>\n\n```\n<button>x</button>\n```';
  const r = walk(parseMarkdown(md));
  assert.ok(!r.tags.includes('button'), r.tags.join());
  assert.ok(r.text.includes('<button onclick="window.__xss=1">Copy</button>'));
  assert.ok(r.text.includes('<button>x</button>'));
});

test('a code block keeps its text, tabs and indentation as written, with no newline added', () => {
  const body = '  indented\n\tpwsh -EncodedCommand AAAA\n';
  const [pre] = parseMarkdown(`\`\`\`\n${body}\`\`\``);
  assert.equal(pre.tag, 'pre');
  assert.equal(textOf(pre), '  indented\n\tpwsh -EncodedCommand AAAA');
});

test('a Makefile in a list item keeps its recipe tab', () => {
  const [list] = parseMarkdown('- the Makefile:\n\n  ```make\n  all:\n  \techo hello\n  ```\n- done');
  const pre = list.children[0].children.find((n) => n.tag === 'pre');
  assert.equal(textOf(pre), 'all:\n\techo hello');
});

test('whitespace-only lines in a list item\'s code block keep what is past the list\'s indentation', () => {
  const [list] = parseMarkdown('- a heredoc:\n\n  ```\n  cat <<EOF\n  \t\n      \n  EOF\n  ```');
  const pre = list.children[0].children.find((n) => n.tag === 'pre');
  assert.equal(textOf(pre), 'cat <<EOF\n\t\n    \nEOF');
});

test('anything that is not a string renders as nothing', () => {
  assert.deepEqual(parseMarkdown(null), []);
  assert.deepEqual(parseMarkdown({ toString: () => '<b>' }), []);
});
