import { test, expect } from '@playwright/test';

for (const width of [390, 1024]) {
  test.describe(`row and strip motion at ${width}`, () => {
    test.use({ viewport: { width, height: 900 }, hasTouch: true, isMobile: true });
    test.beforeEach(async ({ page }) => {
      await page.goto('?demo=1&static=1');
      await page.waitForSelector('html[data-demo-ready]');
      await page.evaluate(async () => {
        // Hold native animations before the next frame can outlast their token duration.
        const animate = Element.prototype.animate;
        Element.prototype.animate = function (...args) { const a = animate.apply(this, args); a.pause(); a.currentTime = 0; return a; };
        // Snapshot readiness precedes its motion-suppressed frame and the fonts' final layout.
        await document.fonts.ready; await new Promise(requestAnimationFrame);
      });
    });

    test('a row leaves where it was while its neighbour eases up without relayout, then a burst opens one slot', async ({ page }) => {
      const sample = await page.evaluate(async () => {
        const d = window.agentBridgeDemo;
        const rows = [...document.querySelectorAll('#session-list > .session')];
        const neighbour = rows[1], removed = rows[0], before = neighbour.getBoundingClientRect().top;
        d.client.store.remove(removed.dataset.id);
        d.page.view.render();
        await new Promise(requestAnimationFrame);
        const shift = neighbour.getAnimations().find((a) => a.id === 'ab-motion-shift');
        if (!shift) { return { shifted: false }; }
        shift.pause(); shift.currentTime = 40;
        const layout = [neighbour.offsetTop, neighbour.offsetHeight], mid = neighbour.getBoundingClientRect().top;
        shift.currentTime = 120;
        const nextLayout = [neighbour.offsetTop, neighbour.offsetHeight];
        shift.finish(); const end = neighbour.getBoundingClientRect().top;
        const { timing } = await import('./js/ui/motion.js');
        const animate = neighbour.animate.bind(neighbour), moves = [];
        neighbour.animate = (...args) => { const a = animate(...args); moves.push(a); return a; };
        for (let i = 0; i < 3; i++) {
          d.client.store.upsert({ ...d.host.get(neighbour.dataset.id), id: `motion-burst-${i}` });
          d.page.view.render();
        }
        await new Promise(requestAnimationFrame);
        const arrival = moves.filter((a) => a.id === 'ab-motion-shift');
        return { shifted: true, before, mid, end, layout, nextLayout, duration: shift.effect.getTiming().duration,
          token: timing('shift').duration, arrivals: arrival.length,
          properties: shift.effect.getKeyframes().flatMap((f) => Object.keys(f)),
          burstFrom: arrival[0]?.effect.getKeyframes()[0].transform };
      });
      expect(sample.shifted).toBe(true);
      expect(sample.mid).toBeLessThan(sample.before);
      expect(sample.mid).toBeGreaterThan(sample.end);
      expect(sample.layout).toEqual(sample.nextLayout);
      expect(sample.duration).toBe(sample.token);
      for (const property of ['height', 'width', 'top', 'left']) { expect(sample.properties).not.toContain(property); }
      expect(sample.arrivals).toBe(1);
      expect(sample.burstFrom).not.toBe('none');
    });
    test('a row-menu move eases both neighbours and a second change retargets from the drawn place on the token curve', async ({ page }) => {
      const result = await page.evaluate(async () => {
        const { flip } = await import('./js/ui/motion.js');
        const rows = [...document.querySelectorAll('#session-list > .session')];
        const moved = rows[0], neighbour = rows[1];
        moved.querySelector('.row-main').dispatchEvent(new KeyboardEvent('keydown', { key: 'F10', shiftKey: true, bubbles: true }));
        [...document.querySelectorAll('.row-menu-item')].find((el) => el.textContent.includes('Move down')).click();
        await new Promise(requestAnimationFrame);
        const shift = neighbour.getAnimations().find((a) => a.id === 'ab-motion-shift');
        if (!shift) { return null; }
        shift.pause(); shift.currentTime = 40;
        const before = neighbour.getBoundingClientRect().top;
        flip(neighbour.parentElement, () => { moved.remove(); });
        await new Promise(requestAnimationFrame);
        const next = neighbour.getAnimations().find((a) => a.id === 'ab-motion-shift');
        next.pause(); next.currentTime = 0;
        return { before, after: neighbour.getBoundingClientRect().top, oldState: shift.playState,
          easing: next.effect.getTiming().easing,
          token: getComputedStyle(document.documentElement).getPropertyValue('--ab-motion-list-item-ease').trim() };
      });
      expect(result).not.toBeNull();
      expect(Math.abs(result.after - result.before)).toBeLessThan(1);
      expect(result.oldState).toBe('idle');
      expect(result.easing).toBe(result.token);
    });
    test('a moving row leaves from its drawn position rather than its layout destination', async ({ page }) => {
      const positions = await page.evaluate(async () => {
        const { flip, leave } = await import('./js/ui/motion.js');
        const rows = [...document.querySelectorAll('#session-list > .session')];
        const neighbour = rows[1], list = neighbour.parentElement;
        flip(list, () => rows[0].remove());
        await new Promise(requestAnimationFrame);
        const shift = neighbour.getAnimations().find((a) => a.id === 'ab-motion-shift');
        shift.pause(); shift.currentTime = 40;
        const before = neighbour.getBoundingClientRect().top;
        let copy;
        flip(list, () => {
          const exit = leave(neighbour, 'listItem');
          exit.pause();
          copy = exit.effect.target.getBoundingClientRect().top;
          neighbour.remove();
        });
        return { before, copy };
      });
      expect(Math.abs(positions.copy - positions.before)).toBeLessThan(1);
    });
    for (const pending of [false, true]) {
      test(`an incomplete initial snapshot never shifts the rows already received${pending ? ' after a queued settled update' : ''}`, async ({ page }) => {
        const shifts = await page.evaluate(async (pending) => {
          const d = window.agentBridgeDemo, drawn = [];
          const animate = Element.prototype.animate;
          Element.prototype.animate = function (...args) { const a = animate.apply(this, args); drawn.push(a); return a; };
          const template = d.host.get(document.querySelector('.session').dataset.id);
          if (pending) {
            d.client.store.upsert({ ...template, id: 'before-incomplete-snapshot' });
            d.page.view.render();
          }
          d.client.synced = false;
          for (let i = 0; i < 2; i++) {
            d.client.store.upsert({ ...template, id: `snapshot-${i}` });
            d.page.view.render();
            await new Promise(requestAnimationFrame);
          }
          return drawn.filter((a) => /^ab-motion-(shift|reorder)$/.test(a.id)).length;
        }, pending);
        expect(shifts).toBe(0);
      });
    }
    test('a manual move during a neighbour shift starts from the drawn position', async ({ page }) => {
      const positions = await page.evaluate(async () => {
        const d = window.agentBridgeDemo, rows = [...document.querySelectorAll('#session-list > .session')];
        const neighbour = rows[1];
        d.client.store.remove(rows[0].dataset.id);
        d.page.view.render();
        await new Promise(requestAnimationFrame);
        const shift = neighbour.getAnimations().find((a) => a.id === 'ab-motion-shift');
        shift.pause(); shift.currentTime = 40;
        const before = neighbour.getBoundingClientRect().top;
        neighbour.querySelector('.row-main').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', altKey: true, bubbles: true }));
        const reorder = neighbour.getAnimations().find((a) => a.id === 'ab-motion-reorder');
        reorder.pause(); reorder.currentTime = 0;
        const immediate = neighbour.getBoundingClientRect().top;
        await new Promise(requestAnimationFrame);
        return { before, immediate, after: neighbour.getBoundingClientRect().top };
      });
      expect(Math.abs(positions.immediate - positions.before)).toBeLessThan(1);
      expect(Math.abs(positions.after - positions.before)).toBeLessThan(1);
    });

    for (const via of ['Alt+Down', 'menu']) {
      test(`${via} keeps row identity, focus and a press begun before the move`, async ({ page }) => {
        const id = await page.locator('#session-list > .session').nth(1).getAttribute('data-id');
        const button = page.locator(`#session-list > .session[data-id="${id}"] > .row-main`);
        const focused = await button.evaluate((el) => {
          const d = window.agentBridgeDemo, row = el.parentElement;
          window.keptMotion = { button: el, row, downs: 0, clicks: 0 };
          el.addEventListener('pointerdown', () => window.keptMotion.downs++);
          el.addEventListener('click', () => window.keptMotion.clicks++);
          el.focus();
          d.page.view.order.move(row.dataset.key, -1); d.page.view.render();
          const kept = document.activeElement === el;
          d.page.view.order.move(row.dataset.key, 1); d.page.view.render();
          return kept && document.activeElement === el;
        });
        expect(focused).toBe(true);
        await button.hover();
        await page.mouse.down();
        if (via === 'Alt+Down') { await button.press('Alt+ArrowDown'); }
        else {
          await button.press('Shift+F10');
          await page.getByRole('menuitem', { name: /Move down/ }).evaluate((el) => el.click());
        }
        await page.evaluate(() => { for (const a of document.getAnimations()) { a.pause(); a.currentTime = 0; } });
        expect(await button.evaluate((el) => el === window.keptMotion.button && el.parentElement === window.keptMotion.row)).toBe(true);
        expect(await button.evaluate((el) => document.activeElement === el)).toBe(true);
        expect(await button.evaluate((el) => [...el.parentElement.parentElement.children].indexOf(el.parentElement))).toBe(2);
        const box = await button.boundingBox();
        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await page.mouse.up();
        expect(await page.evaluate(() => [window.keptMotion.downs, window.keptMotion.clicks])).toEqual([1, 1]);
        expect(await button.evaluate((el) => el === window.keptMotion.button && el.parentElement === window.keptMotion.row)).toBe(true);
      });
    }

    for (const reduced of [false, true]) {
      test(`delegate chips arrive, leave and ease aside${reduced ? ' without travel under reduced motion' : ''}`, async ({ page }) => {
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.emulateMedia({ reducedMotion: reduced ? 'reduce' : 'no-preference' });
        const result = await page.evaluate(async () => {
          const { DelegateStrip } = await import('./js/ui/delegates-view.js');
          const strip = new DelegateStrip();
          document.querySelector('#list-pane').prepend(strip.element);
          const agent = (id) => ({ id, name: id, status: 'running', startedAt: new Date().toISOString() });
          strip.update({ subagents: [agent('first'), agent('second')] });
          const initial = strip.item('second').getAnimations().length;
          const kept = strip.item('second'); kept.focus();
          strip.update({ subagents: [agent('second'), agent('third')] });
          await new Promise(requestAnimationFrame);
          const shifted = strip.item('second').getAnimations();
          const arrived = strip.item('third').getAnimations();
          return { initial, same: strip.item('second') === kept, focused: document.activeElement === kept,
            ids: shifted.map((a) => a.id), arrived: arrived.map((a) => a.id),
            travel: shifted.some((a) => a.effect.getKeyframes().some((f) => f.transform && f.transform !== 'none')),
            exits: document.getAnimations().filter((a) => a.id === 'ab-motion-listItem' && a.effect.target.inert).length };
        });
        expect(result.initial).toBe(0);
        expect(result.same).toBe(true);
        expect(result.focused).toBe(true);
        expect(result.ids).toContain(reduced ? 'ab-motion-value' : 'ab-motion-shift');
        expect(result.travel).toBe(!reduced);
        expect(result.arrived).toContain('ab-motion-listItem');
        expect(result.exits).toBe(1);
      });
    }
  });
}
