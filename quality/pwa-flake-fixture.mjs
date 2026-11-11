import { test as base, expect } from '@playwright/test';
import { writeFileSync } from 'node:fs';

export { expect };
export const test = base.extend({
  diagnostic: [async ({ page }, use, info) => {
    await page.addInitScript(() => {
      window.__diagEvents = [];
      window.__diag = (event, fields = {}) => {
        if (window.__diagEvents.length < 20000) {
          window.__diagEvents.push({ event, date: Date.now(), perf: performance.now(), ...structuredClone(fields) });
        }
      };
      for (const type of ['visibilitychange', 'pageshow', 'online', 'scroll', 'scrollend', 'wheel']) {
        document.addEventListener(type, (e) => {
          if (!['scroll', 'scrollend', 'wheel'].includes(type) || e.target.matches?.('.stream-scroll')) {
            window.__diag(type, { visible: document.visibilityState, top: e.target.scrollTop, persisted: e.persisted });
          }
        }, true);
      }
    });
    const snapshot = (label) => page.evaluate((label) => {
      const d = window.agentBridgeDemo;
      const sc = document.querySelector('.session-view:not([hidden]) .stream-scroll');
      window.__diag?.(label, { grace: d ? { active: d.grace.active, since: d.grace.since, age: d.grace.age, drawn: d.grace.drawn } : null,
        rows: sc?.querySelectorAll('.history-turn').length, top: sc?.scrollTop, height: sc?.scrollHeight, client: sc?.clientHeight,
        strip: document.querySelector('#connection-strip')?.hidden,
        holds: [...document.querySelectorAll('[data-hold]')].map((el) => ({ class: el.className, hold: el.dataset.hold })) });
    }, label);
    for (const name of ['install', 'pauseAt', 'runFor']) {
      const original = page.clock[name].bind(page.clock);
      page.clock[name] = async (...args) => {
        await snapshot(`clock.${name}.before:${JSON.stringify(args)}`);
        const result = await original(...args);
        await snapshot(`clock.${name}.after:${JSON.stringify(args)}`);
        return result;
      };
    }
    page.on('pageerror', (err) => console.error(`PAGEERROR ${err.stack}`));
    await use();
    await snapshot('final');
    const events = await page.evaluate(() => window.__diagEvents ?? []);
    const path = info.outputPath('events.json');
    writeFileSync(path, JSON.stringify({ title: info.title, project: info.project.name, repeat: info.repeatEachIndex, status: info.status, events }, null, 2));
    await info.attach('events', { path, contentType: 'application/json' });
    writeFileSync(`diag-output/${info.project.name}-${info.title.replace(/[^a-z0-9]/gi, '_').slice(0, 100)}-${info.repeatEachIndex}.json`,
      JSON.stringify({ title: info.title, repeat: info.repeatEachIndex, status: info.status, errors: info.errors, events }, null, 2));
    console.log(`DIAG ${info.project.name} repeat=${info.repeatEachIndex} ${info.status} ${info.title}`);
  }, { auto: true }],
});
