import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { mountRelease, releaseLine } from '../../js/ui/release-view.js';
import { AppRelease } from '../../js/app-release.js';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const SHA = '64a4019c0ffee0123456789abcdef0123456789a';

test('a stamped build reads as its 7-character release id and the date it was built, in the person\'s locale', () => {
  assert.deepEqual(releaseLine(SHA, '2026-11-07T20:15:00.000Z', 'en-US'), { id: '64a4019', date: '· built Nov 7, 2026', copy: '64a4019 · built Nov 7, 2026' });
  assert.notEqual(releaseLine(SHA, '2026-11-07T20:15:00.000Z', 'de-DE').date, releaseLine(SHA, '2026-11-07T20:15:00.000Z', 'en-US').date);
});

test('a local build with no stamp shows dev and no date, and an unreadable date is no date', () => {
  assert.deepEqual(releaseLine('dev', '', 'en-US'), { id: 'dev', date: '', copy: 'dev' });
  assert.equal(releaseLine(SHA, 'not a time', 'en-US').date, '');
  assert.equal(releaseLine(undefined, undefined, 'en-US').id, 'dev');
});

test('the bug-report bundle is still given the release id as appVersion', () => {
  assert.match(readFileSync(path.join(pwa, 'js', 'main.js'), 'utf8'), /appVersion: APP_VERSION,/);
});

test('Settings calls the release controller, renders its busy state and keeps newer metadata after a failed check', async () => {
  const elements = {};
  const doc = { getElementById(id) {
    return elements[id] ??= Object.assign(new EventTarget(), {
      textContent: '', hidden: false, attributes: {},
      setAttribute(name, value) { this.attributes[name] = value; },
    });
  } };
  let finish;
  let pending;
  let requests = 0;
  const app = new AppRelease({
    version: SHA,
    fetchVersion: () => { requests++; return new Promise((resolve) => { finish = resolve; }); },
    onChange: (state) => view.render(state),
  });
  const view = mountRelease({
    version: SHA, builtAt: '', doc, win: { navigator: { language: 'en-US' } },
    check: () => { pending = app.check(); },
  });
  const button = doc.getElementById('release-check');
  button.dispatchEvent(new Event('click'));
  assert.equal(requests, 1);
  assert.equal(button.disabled, true);
  assert.equal(button.attributes['aria-busy'], 'true');
  finish({ ok: true, json: async () => ({ version: 'b'.repeat(40), builtAt: '' }) });
  await pending;
  assert.equal(elements['release-id'].textContent, '64a4019');
  assert.equal(elements['release-newer-id'].textContent, 'bbbbbbb');
  assert.equal(elements['release-newer'].hidden, false);
  assert.equal(button.disabled, false);
  button.dispatchEvent(new Event('click'));
  finish({ ok: false });
  await pending;
  assert.equal(elements['release-result'].textContent, "Couldn't check for updates.");
  assert.equal(elements['release-newer'].hidden, false);
  assert.equal(elements['release-newer-id'].textContent, 'bbbbbbb');
  assert.equal(button.hidden, false);
  assert.equal(button.disabled, false);
  assert.equal(button.attributes['aria-busy'], 'false');
});
