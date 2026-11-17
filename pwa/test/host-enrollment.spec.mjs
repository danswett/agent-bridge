import { readFileSync } from 'node:fs';
import { test, expect } from '@playwright/test';

const vectors = JSON.parse(readFileSync(new URL('../../docs/protocol/vectors/roster.json', import.meta.url), 'utf8'));
const cases = [
  { ...vectors.entries.valid.find((v) => v.name === 'add-host-v2'), code: null },
  ...vectors.entries.refused.filter((v) => v.name.startsWith('host-proof-') || v.name.startsWith('host-sign-')),
];

test('the browser accepts the C# host descriptor and agrees on every shared host-proof refusal', async ({ page }) => {
  await page.goto('?demo=1&static=1');
  const actual = await page.evaluate(async (cases) => {
    const { parseHostDescriptor } = await import('./js/roster/entry.js');
    const { fromBase64Url } = await import('./js/roster/codec.js');
    const { RosterFormatError } = await import('./js/roster/refusal.js');
    const results = [];
    for (const v of cases) {
      const body = JSON.parse(new TextDecoder().decode(fromBase64Url(JSON.parse(v.wrapper).body, 'vector')));
      try {
        await parseHostDescriptor(new TextEncoder().encode(JSON.stringify(body.device)), body.account, body.generation);
        results.push({ name: v.name, code: null });
      } catch (e) {
        if (!(e instanceof RosterFormatError)) throw e;
        results.push({ name: v.name, code: e.code });
      }
    }
    return results;
  }, cases);
  expect(actual).toEqual(cases.map(({ name, code }) => ({ name, code })));
});
