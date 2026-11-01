// Code blocks in agent text: a Copy button on each, and long lines wrapped rather than
// scrolled, so a long command can be copied whole from a phone.
import { test, expect } from '@playwright/test';

const CODEX = '019a1b2c-codex-example';
const later = () => new Date(Date.now() + 1000).toISOString();
const card = (page, id) => page.locator(`.session[data-id="${id}"]`);

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
