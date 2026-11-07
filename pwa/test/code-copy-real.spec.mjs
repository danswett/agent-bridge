// #1090: Copy on a code block, against the engine's real clipboard rather than a stub
// (code-copy.spec.mjs stubs navigator.clipboard and execCommand, which is how a Copy that put
// nothing on the clipboard went unseen: WebKit's Clipboard API resolved writeText with nothing
// written, and the button said Copied). Each test pastes into a field, as a person does, and
// compares what arrived with what the block draws.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const CODEX = '019a1b2c-codex-example';
const later = () => new Date(Date.now() + 1000).toISOString();

async function openDemo(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, CODEX);
  // Where a paste lands: a field of the test's own, not the app's markup.
  await page.evaluate(() => {
    const field = document.createElement('textarea');
    field.id = 'paste-target';
    field.setAttribute('aria-label', 'Paste here');
    field.style.cssText = 'position:fixed;bottom:0;left:0;width:200px;height:40px;z-index:99999';
    document.body.append(field);
  });
}

async function respond(page, response, code) {
  await page.evaluate(({ id, response, t }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: t, activity: { ...s.activity, response } });
  }, { id: CODEX, response, t: later() });
  const r = view(page, CODEX).locator('.response');
  await expect(r.locator('pre code')).toHaveText(code);
  return r;
}

// What a paste gives now, waiting a moment for an engine that fills its clipboard late.
async function pasted(page, field = page.locator('#paste-target')) {
  let got = '';
  for (let i = 0; i < 10 && !got; i++) {
    await field.fill('');
    await field.focus();
    await page.keyboard.press('ControlOrMeta+V');
    got = await field.inputValue();
    if (!got) { await page.waitForTimeout(150); }
  }
  return got;
}

// A 1000-character base64 token: the shape of a pwsh -EncodedCommand.
const BLOB = 'QUJD'.repeat(244) + 'ZGVmZ2g=';
const SAMPLES = {
  'a multi-line command with indentation, tabs and trailing spaces': 'git log --format="%h %s" -- "src/app.js"  \n\tmake -C build all   \n    echo \'done\' | tee out.txt',
  'special characters': 'echo "$HOME" `date` <b>&amp;</b> \\n %s ${x} \u00e9\u4e2d\ud83d\ude00',
  'a long encoded command': `pwsh -NoProfile -EncodedCommand ${BLOB}\n  second line`,
};

for (const [what, code] of Object.entries(SAMPLES)) {
  test(`Copy puts ${what} on the clipboard exactly, and says Copied`, async ({ page }) => {
    await openDemo(page);
    const r = await respond(page, `Run:\n\n\`\`\`sh\n${code}\n\`\`\``, code);
    const button = r.getByRole('button', { name: 'Copy code' });
    await button.click();
    await expect(button).toHaveText('Copied');
    expect(await pasted(page)).toBe(code);
  });
}

test('Copy puts the sign-in code on the clipboard, with its sheet open, and says Code copied', async ({ page }) => {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.getByRole('button', { name: 'Sign in to Copilot on DEV-VM1' }).click();
  const sheet = page.locator('dialog.sign-in-sheet');
  await expect(sheet.locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  // The sheet is modal: the page behind it is inert, so the paste field is inside it.
  await sheet.evaluate((d) => {
    const field = document.createElement('textarea');
    field.id = 'paste-target';
    field.setAttribute('aria-label', 'Paste here');
    d.append(field);
  });
  await sheet.getByRole('button', { name: 'Copy code for Copilot on DEV-VM1' }).click();
  await expect(sheet.locator('.sign-in-copied')).toHaveText('Code copied');
  expect(await pasted(page, sheet.locator('#paste-target'))).toBe('WDJB-MJHT');
});

test('Copy leaves the text the person had selected in the conversation selected', async ({ page }) => {
  await openDemo(page);
  const r = await respond(page, 'Some words to select.\n\n```sh\nnpm test\n```', 'npm test');
  const picked = await r.locator('p').first().evaluate((p) => {
    const range = document.createRange();
    range.selectNodeContents(p);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    return getSelection().toString();
  });
  expect(picked).toBe('Some words to select.');
  const button = r.getByRole('button', { name: 'Copy code' });
  // By keyboard: a click would end the selection itself.
  await button.focus();
  await page.keyboard.press('Enter');
  await expect(button).toHaveText('Copied');
  expect(await page.evaluate(() => getSelection().toString())).toBe(picked);
});

test('Copy takes the block as it is now after the response is redrawn with new text', async ({ page }) => {
  await openDemo(page);
  await respond(page, '```sh\nfirst run\n```', 'first run');
  const r = await respond(page, 'Now:\n\n```sh\nsecond run\n  --flag\n```', 'second run\n  --flag');
  const button = r.getByRole('button', { name: 'Copy code' });
  await expect(button).toHaveCount(1);
  await button.click();
  await expect(button).toHaveText('Copied');
  expect(await pasted(page)).toBe('second run\n  --flag');
});
