// The real service worker receiving real pushes, in Chromium: DevTools' ServiceWorker
// domain delivers a push message to the installed worker as the push service would, and
// the notifications it shows are read back from the registration. WebKit has no such hook;
// test/unit/sw.test.mjs covers the same handlers there.
import { test, expect } from '@playwright/test';

// The full Chromium (its new headless mode): the headless shell refuses notifications even
// when they are granted.
test.use({ serviceWorkers: 'allow', channel: 'chromium' });
test.skip(({ browserName }) => browserName !== 'chromium', 'only Chromium can be sent a push through DevTools');

const MACHINE = '53d1997f0a1b2c3d4e5f60718293a4b5';

const notice = (scope, kind, body, extra = {}) => JSON.stringify({
  web_push: 8030,
  notification: {
    title: 'Pick the session store',
    body,
    navigate: `${scope}?host=${MACHINE}&session=s1&question=q1`,
    tag: `${MACHINE}:s1`,
    lang: 'en',
    ...(kind === 'resolved' ? { silent: true } : {}),
    data: { v: 1, kind, machine: MACHINE, session: 's1', question: 'q1', at: '2026-10-28T04:22:43.182Z' },
    ...extra,
  },
});

test('a push shows a notification, and every later notice about the session replaces it', async ({ page, context, baseURL }) => {
  const origin = new URL(baseURL).origin;
  await context.grantPermissions(['notifications'], { origin });
  const cdp = await context.newCDPSession(page);
  const registrations = [];
  cdp.on('ServiceWorker.workerRegistrationUpdated', (e) => registrations.push(...e.registrations));
  await cdp.send('ServiceWorker.enable');

  await page.goto('?demo=1&static=1');
  const scope = await page.evaluate(async () => (await navigator.serviceWorker.ready).scope);
  await expect.poll(() => registrations.find((r) => r.scopeURL === scope && !r.isDeleted)?.registrationId, { timeout: 15000 }).toBeTruthy();
  const registrationId = registrations.find((r) => r.scopeURL === scope).registrationId;

  // Chromium's getNotifications() drops any notification it finds in its database but not
  // yet on the platform's list of displayed ones, and it writes the database before it
  // displays. A read in that gap loses the notification for good, though it is on screen.
  // Polling while pushes were still being shown failed this test on 3 of 4 pushes to main,
  // missing whichever notice the read caught in the gap. So the worker counts each
  // notification it has finished showing, and the page reads only once every push
  // delivered so far is shown.
  await expect.poll(() => context.serviceWorkers().some((w) => w.url().startsWith(scope)), { timeout: 15000 }).toBe(true);
  const worker = context.serviceWorkers().find((w) => w.url().startsWith(scope));
  await worker.evaluate(() => {
    const reg = self.registration;
    const show = reg.showNotification.bind(reg);
    self.testShown = 0;
    reg.showNotification = (...args) => show(...args).finally(() => { self.testShown += 1; });
  });
  let delivered = 0;
  const deliver = (data) => { delivered += 1; return cdp.send('ServiceWorker.deliverPushMessage', { origin, registrationId, data }); };
  const settled = () => expect.poll(() => worker.evaluate(() => self.testShown), { timeout: 15000 }).toBe(delivered);
  const shown = () => page.evaluate(async () => (await (await navigator.serviceWorker.ready).getNotifications())
    .map((n) => ({ title: n.title, body: n.body, tag: n.tag, silent: n.silent, renotify: n.renotify, data: n.data })));

  await deliver(notice(scope, 'question', 'DEV · waiting on you\nWhich database?'));
  await settled();
  await expect.poll(shown, { timeout: 15000 }).toHaveLength(1);
  const [question] = await shown();
  expect(question).toMatchObject({ title: 'Pick the session store', body: 'DEV · waiting on you\nWhich database?', tag: `${MACHINE}:s1`, renotify: true, silent: false });
  expect(question.data).toEqual({ url: `${scope}?host=${MACHINE}&session=s1&question=q1`, kind: 'question', machine: MACHINE, session: 's1', question: 'q1' });

  // Answered at the machine: the same notification, quietly changed.
  await deliver(notice(scope, 'resolved', 'DEV · no longer waiting'));
  await settled();
  await expect.poll(async () => (await shown()).map((n) => n.body), { timeout: 15000 }).toEqual(['DEV · no longer waiting']);
  expect((await shown())[0]).toMatchObject({ silent: true, renotify: false });

  // Whatever arrives, something is shown, and a tap stays in the app.
  await deliver('not json');
  await deliver(notice(scope, 'question', 'x', { tag: 'other', navigate: 'https://evil.example/' }));
  await settled();
  await expect.poll(shown, { timeout: 15000 }).toHaveLength(3);
  const all = await shown();
  expect(all.map((n) => n.title)).toContain('Agent Bridge');
  expect(all.find((n) => n.tag === 'other').data.url).toBe(scope);
});
