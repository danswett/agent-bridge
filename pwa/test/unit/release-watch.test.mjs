import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ReleaseWatch, STUCK_AFTER } from '../../js/release-watch.js';

// A worker installing a release, moved through its states as the browser would.
class Worker extends EventTarget {
  state = 'installing';
  to(...states) {
    for (const s of states) { this.state = s; this.dispatchEvent(new Event('statechange')); }
  }
}

function memory() {
  const m = new Map();
  return { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => m.set(k, String(v)), removeItem: (k) => m.delete(k) };
}

test('releases that keep failing to install, one a start, say so, and the next one that installs clears it', () => {
  const storage = memory();
  const said = [];
  for (let start = 1; start <= STUCK_AFTER; start++) {
    const watch = new ReleaseWatch({ storage, onChange: (stuck) => said.push(stuck) });
    assert.equal(watch.stuck, false, `not yet at start ${start}`);
    const worker = new Worker();
    watch.watch(worker);
    watch.watch(worker);
    worker.to('redundant');
  }
  assert.deepEqual(said, [false, false, true], 'each failure counted once, and the third is too many');

  const later = new ReleaseWatch({ storage, onChange: (stuck) => said.push(stuck) });
  assert.equal(later.stuck, true, 'remembered at the next start');
  const fixed = new Worker();
  later.watch(fixed);
  fixed.to('installed', 'activating', 'activated');
  assert.equal(later.stuck, false);
  assert.equal(new ReleaseWatch({ storage }).stuck, false);
});

test('failures counted under one release are no count under the next, which may have installed with no page open', () => {
  const storage = memory();
  const before = new ReleaseWatch({ storage, release: 'agent-bridge-app-a' });
  for (let i = 0; i < STUCK_AFTER; i++) {
    const worker = new Worker();
    before.watch(worker);
    worker.to('redundant');
  }
  assert.equal(new ReleaseWatch({ storage, release: 'agent-bridge-app-a' }).stuck, true, 'still on the release they were counted under');
  assert.equal(new ReleaseWatch({ storage, release: 'agent-bridge-app-b' }).stuck, false);
});

test('a release that installed and is later replaced did not fail', () => {
  const storage = memory();
  const watch = new ReleaseWatch({ storage });
  for (let i = 0; i < STUCK_AFTER; i++) {
    const worker = new Worker();
    watch.watch(worker);
    worker.to('installed', 'redundant');
  }
  assert.equal(watch.failures, 0);
});

test('with storage blocked, failures still count for as long as the page is open', () => {
  const blocked = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); }, removeItem() { throw new Error('blocked'); } };
  const watch = new ReleaseWatch({ storage: blocked });
  for (let i = 0; i < STUCK_AFTER; i++) {
    const worker = new Worker();
    watch.watch(worker);
    worker.to('redundant');
  }
  assert.equal(watch.stuck, true);
});
