// Attaching files and images to a reply, in demo mode: the simulated host takes them as a
// real one does (attachment.start and attachment.chunk, checked against their sha256) and
// types them as @ mentions ahead of the words. Picking, pasting, removing, the size limit and
// shrinking a large image, progress, a refusal that keeps them, and a host that takes none.
import { test, expect } from '@playwright/test';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const STAGED = /^@C:\\Users\\demo\\AppData\\Local\\AgentBridge\\default\\attachments\\[0-9a-f]{32}-/;

const box = (page) => page.locator(`.session[data-id="${IDLE}"] form.reply`);
const typed = (page) => page.evaluate(() => window.agentBridgeDemo.host.typedReplies);
const ops = (page) => page.evaluate(() => window.agentBridgeDemo.host.attachmentLog.map((l) => l.op));

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&answerDelay=200${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

// Pastes an image drawn in the page, as a screenshot pasted from the clipboard arrives: a
// file item with a generic name. noise: random pixels, which no image format compresses.
function pasteImage(page, { width = 24, height = 16, noise = false, name = 'image.png' } = {}) {
  return box(page).getByRole('textbox', { name: 'Reply' }).evaluate(async (input, { width, height, noise, name }) => {
    const canvas = Object.assign(document.createElement('canvas'), { width, height });
    const ctx = canvas.getContext('2d');
    const pixels = ctx.createImageData(width, height);
    for (let i = 0; i < pixels.data.length; i += 4) {
      pixels.data.set(noise ? [Math.random() * 256, Math.random() * 256, Math.random() * 256, 255] : [40, 120, (i / 4) % 256, 255], i);
    }
    ctx.putImageData(pixels, 0, 0);
    const blob = await new Promise((r) => canvas.toBlob(r, 'image/png'));
    const data = new DataTransfer();
    data.items.add(new File([blob], name, { type: 'image/png' }));
    input.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  }, { width, height, noise, name });
}

const pick = (page, ...files) => box(page).locator('input.file-picker').setInputFiles(files);
const textFile = (name, size = 40, mimeType = 'text/markdown') => ({ name, mimeType, buffer: Buffer.alloc(size, 'a') });

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  test('a picked file and a pasted screenshot show as chips, go first, and reach the session as @ mentions ahead of the words', async ({ page }) => {
    await openDemo(page);
    const reply = box(page);
    await expect(reply.getByRole('button', { name: 'Attach a file or image' })).toBeVisible();

    await pick(page, textFile('plan notes.md'));
    await pasteImage(page);
    const chips = reply.getByRole('list', { name: 'Attachments' }).getByRole('listitem');
    await expect(chips).toHaveCount(2);
    await expect(chips.nth(0).locator('.attachment-name')).toHaveText('plan notes.md');
    await expect(chips.nth(0).locator('.attachment-size')).toHaveText('40 bytes');
    await expect(chips.nth(1).locator('.attachment-name')).toHaveText('image.png');
    // The screenshot's thumbnail is drawn from its own bytes.
    await expect(chips.nth(1).locator('img.attachment-thumb')).toHaveAttribute('src', /^blob:/);
    expect(await chips.nth(1).locator('img.attachment-thumb').evaluate((img) => img.complete && img.naturalWidth)).toBe(24);
    await expect(reply.getByRole('textbox', { name: 'Reply' })).toHaveValue('');

    await reply.getByRole('textbox', { name: 'Reply' }).fill('What do you\nmake of these?');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toHaveText('Delivered. The session has your reply.');

    const [sent] = await typed(page);
    const words = sent.said.split(' ');
    expect(words[0]).toMatch(new RegExp(`${STAGED.source}plan-notes\\.md$`));
    expect(words[1]).toMatch(new RegExp(`${STAGED.source}image\\.png$`));
    expect(words.slice(2).join(' ')).toBe('What do you make of these?');
    expect(sent.files.map((f) => f.name)).toEqual(['plan notes.md', 'image.png']);
    // Each went as a start and one chunk, before the reply named them.
    expect(await ops(page)).toEqual(['attachment.start', 'attachment.chunk', 'attachment.start', 'attachment.chunk']);
    await expect(chips).toHaveCount(0);
    await expect(reply.getByRole('list', { name: 'Attachments' })).toBeHidden();
  });

  test('files alone are a reply: Send needs words or a file', async ({ page }) => {
    await openDemo(page);
    const reply = box(page);
    await expect(reply.getByRole('button', { name: 'Send' })).toBeDisabled();
    await pick(page, textFile('log.txt', 10, 'text/plain'));
    await expect(reply.getByRole('button', { name: 'Send' })).toBeEnabled();
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    expect((await typed(page))[0].said).toMatch(new RegExp(`${STAGED.source}log\\.txt$`));
  });

  test('a file can be taken out before sending, and only the rest go', async ({ page }) => {
    await openDemo(page);
    const reply = box(page);
    await pick(page, textFile('keep.md'), textFile('drop.md'));
    await reply.getByRole('button', { name: 'Remove drop.md' }).tap();
    await expect(reply.locator('.attachment-name')).toHaveText(['keep.md']);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('one file');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    expect((await typed(page))[0].files.map((f) => f.name)).toEqual(['keep.md']);
  });

  test('a file over 256 KiB is refused with the reason, and a large image is made smaller to fit', async ({ page }) => {
    await openDemo(page);
    const reply = box(page);
    await pick(page, { name: 'logs.zip', mimeType: 'application/zip', buffer: Buffer.alloc(300 * 1024) });
    await expect(reply.getByRole('alert')).toHaveText("Couldn't attach logs.zip: logs.zip is 300 KiB, over the 256 KiB limit for an attachment.");
    await expect(reply.locator('.attachment')).toHaveCount(0);

    await pasteImage(page, { width: 900, height: 700, noise: true, name: 'Screenshot.png' });
    const chip = reply.locator('.attachment');
    await expect(chip).toHaveCount(1, { timeout: 15000 });
    await expect(chip.locator('.attachment-name')).toHaveText('Screenshot.jpg');
    await expect(chip.locator('.attachment-size')).toHaveText(/ KiB · made smaller$/);
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    const [{ files }] = await typed(page);
    expect(files[0].type).toBe('image/jpeg');
    expect(files[0].size).toBeLessThanOrEqual(256 * 1024);
  });

  test('a reply carries at most ten files', async ({ page }) => {
    await openDemo(page);
    const reply = box(page);
    await pick(page, ...Array.from({ length: 11 }, (_, i) => textFile(`f${i}.txt`, 5, 'text/plain')));
    await expect(reply.getByRole('alert')).toHaveText('A reply carries at most 10 attachments.');
    await expect(reply.locator('.attachment')).toHaveCount(10);
    await expect(reply.getByRole('button', { name: 'Attach a file or image' })).toBeDisabled();
  });

  test('progress shows while the files go, and the box is held until the outcome', async ({ page }) => {
    await openDemo(page, '&attachDelay=250');
    const reply = box(page);
    await pick(page, { name: 'big.bin', mimeType: 'application/octet-stream', buffer: Buffer.alloc(40 * 1024, 7) });
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toHaveText(/^Sending the attachment… \d+%$/);
    await expect(reply.getByRole('button', { name: 'Remove big.bin' })).toBeDisabled();
    await expect(reply.getByRole('button', { name: 'Attach a file or image' })).toBeDisabled();
    await expect(reply).toHaveAttribute('data-reply', 'delivered', { timeout: 10000 });
    // 40 KiB in chunks of 11 KiB.
    expect((await ops(page)).filter((o) => o === 'attachment.chunk')).toHaveLength(4);
  });

  test('after a file is turned down, taking one out means Try again cannot send it: Send sends what is shown', async ({ page }) => {
    await openDemo(page);
    const reply = box(page);
    await pick(page, textFile('keep.md'), textFile('drop.md'));
    await page.evaluate(() => { window.agentBridgeDemo.host.failNextChunk = 'the host could not take it'; });
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toHaveText("Couldn't send it: the host could not take it.");
    await expect(reply.getByRole('button', { name: 'Try again' })).toBeVisible();

    await reply.getByRole('button', { name: 'Remove drop.md' }).tap();
    await expect(reply.getByRole('button', { name: 'Try again' })).toBeHidden();
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    expect((await typed(page))[0].files.map((f) => f.name)).toEqual(['keep.md']);
  });

  test('a refused reply keeps its files, and sending again does not send their bytes again', async ({ page }) => {
    await openDemo(page);
    await page.evaluate(() => window.agentBridgeDemo.scriptReplies('refused'));
    const reply = box(page);
    await pick(page, textFile('notes.md'));
    await reply.getByRole('textbox', { name: 'Reply' }).fill('see the notes');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply).toHaveAttribute('data-reply', 'refused');
    await expect(reply.locator('.attachment-name')).toHaveText(['notes.md']);

    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    expect(await ops(page)).toEqual(['attachment.start', 'attachment.chunk', 'attachment.start']);
    expect(await typed(page)).toHaveLength(1);
  });
});

test('a machine that takes replies but not files offers no Attach, and a pasted file is left alone', async ({ page }) => {
  await openDemo(page, '&attach=0');
  const reply = box(page);
  await expect(reply.getByRole('textbox', { name: 'Reply' })).toBeVisible();
  await expect(reply.getByRole('button', { name: 'Attach a file or image' })).toBeHidden();
  await pasteImage(page);
  await expect(reply.locator('.attachment')).toHaveCount(0);
  expect(await page.evaluate(() => window.agentBridgeDemo.host.capabilities)).  toEqual(['sessions.read', 'sessions.answer', 'sessions.reply', 'sessions.stop']);
});
