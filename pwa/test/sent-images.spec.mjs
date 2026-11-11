import { test, expect } from '@playwright/test';

for (const width of [390, 820]) {
  test.describe(`${width}px sender images`, () => {
    test.use({ viewport: { width, height: 900 }, hasTouch: true });
    test('same-name sender images survive reload; missing bytes stay chips; viewer closes and URLs are revoked', async ({ page, context, browser }) => {
      await page.goto('?demo=1&static=1');
      await page.evaluate(async () => {
        const { ReplyService } = await import('/js/reply-service.js');
        const { HostClient } = await import('/js/host-client.js');
        const { prepareAttachment } = await import('/js/attachments.js');
        const { accountStore } = await import('/js/signin.js');
        accountStore.save({ accountId: 'image-test', token: 'test-only' });
        const refs = [1, 2].map((n) => `@/demo/attachments/${String(n).repeat(32)}-same.png`);
        const transport = { send(m) { queueMicrotask(() => this.onmessage({ re: m.id, ok: true, result: m.op === 'attachment.start' ? { received: 100000, complete: true } : { outcome: 'delivered', attachments: [{ id: m.data.attachments[0].id, ref: refs.shift() }] } })); } };
        const client = new HostClient(transport);
        const service = new ReplyService({ current: () => ({ client, hostId: 'test' }) });
        for (const colour of ['red', 'blue']) {
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 60;
          canvas.getContext('2d').fillStyle = colour; canvas.getContext('2d').fillRect(0, 0, 60, 60);
          const blob = await new Promise((resolve) => canvas.toBlob(resolve));
          const file = await prepareAttachment(Object.assign(blob, { name: 'same.png' }));
          service.submit({ hostId: 'test', sessionId: 'test', text: '', attachments: [file] });
          while (service.get('test', 'test').state !== 'done') { await new Promise((r) => setTimeout(r, 10)); }
        }
      });
      await page.reload();
      const render = async (target) => target.evaluate(async () => {
        const { sentMessage } = await import('/js/ui/sent-message.js');
        const refs = [1, 2, 3].map((n) => `@/demo/attachments/${String(n).repeat(32)}-same.png`).join(' ');
        const bubble = sentMessage(refs, 'test-sent'); document.body.append(bubble);
        window.testBubble = bubble;
      });
      await render(page);
      const bubble = page.locator('.test-sent');
      await expect(bubble.locator('img')).toHaveCount(2);
      await expect(bubble).not.toContainText('/attachments/');
      await expect(bubble.getByRole('listitem').last()).toContainText('same.png');
      const buttons = bubble.getByRole('button');
      await buttons.first().click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(buttons.first()).toBeFocused();
      const colours = await bubble.locator('img').evaluateAll(async (images) => Promise.all(images.map(async (img) => {
        await img.decode(); const c = document.createElement('canvas'); c.width = c.height = 1;
        c.getContext('2d').drawImage(img, 0, 0, 1, 1); return [...c.getContext('2d').getImageData(0, 0, 1, 1).data];
      })));
      expect(colours).toEqual([[255, 0, 0, 255], [0, 0, 255, 255]]);
      const other = await browser.newContext(); const otherPage = await other.newPage();
      await otherPage.goto('?demo=1&static=1'); await render(otherPage);
      await expect(otherPage.locator('.test-sent img')).toHaveCount(0); await other.close();
      await page.evaluate(() => { window.revoked = []; const revoke = URL.revokeObjectURL; URL.revokeObjectURL = (url) => { window.revoked.push(url); revoke(url); }; window.testBubble.remove(); });
      await expect.poll(() => page.evaluate(() => window.revoked.length)).toBe(2);
    });
  });
}
