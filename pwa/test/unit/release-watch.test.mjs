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

const A = 'agent-bridge-app-a';
const B = 'agent-bridge-app-b';

// One start of the app under a release, whose new release fails to install.
function failedStart(storage, release, said = []) {
  const watch = new ReleaseWatch({ storage, onChange: (stuck) => said.push(stuck) });
  watch.under(release);
  const worker = new Worker();
  watch.watch(worker);
  watch.watch(worker);
  worker.to('redundant');
  return watch;
}

test('releases that keep failing to install, one a start, say so, and the next one that installs clears it', () => {
  const storage = memory();
  const said = [];
  for (let start = 1; start <= STUCK_AFTER; start++) { failedStart(storage, A, said); }
  assert.deepEqual(said.filter((s, i) => i % 2 === 1), [false, false, true], 'each failure counted once, and the third is too many');

  const later = new ReleaseWatch({ storage });
  later.under(A);
  assert.equal(later.stuck, true, 'remembered at the next start');
  const fixed = new Worker();
  later.watch(fixed);
  fixed.to('installed', 'activating', 'activated');
  assert.equal(later.stuck, false);
  const after = new ReleaseWatch({ storage });
  after.under(A);
  assert.equal(after.stuck, false);
});

test('an install that fails before the release in control is known still counts, once it is', () => {
  const storage = memory();
  for (let i = 1; i < STUCK_AFTER; i++) { failedStart(storage, A); }
  const watch = new ReleaseWatch({ storage });
  const worker = new Worker();
  watch.watch(worker);
  worker.to('redundant');
  watch.under(A);
  assert.equal(watch.stuck, true);
});

test('failures counted under one release are no count under the next, nor under the first again after a rollback', () => {
  const storage = memory();
  for (let i = 0; i < STUCK_AFTER; i++) { failedStart(storage, A); }
  // B installed with no page open, and the app opens under it.
  const underB = new ReleaseWatch({ storage });
  underB.under(B);
  assert.equal(underB.stuck, false);
  // Then A again, rolled back the same way.
  const underA = new ReleaseWatch({ storage });
  underA.under(A);
  assert.equal(underA.stuck, false);
});

test('after a release takes over an open app, failures are counted under that release', () => {
  const storage = memory();
  const watch = new ReleaseWatch({ storage });
  watch.under(A);
  for (let i = 1; i < STUCK_AFTER; i++) {
    const failed = new Worker();
    watch.watch(failed);
    failed.to('redundant');
  }
  watch.tookOver();
  assert.equal(watch.failures, 0, 'the release that took over installed');
  const worker = new Worker();
  watch.watch(worker);
  worker.to('redundant');
  watch.under(B);
  const next = new ReleaseWatch({ storage });
  next.under(B);
  assert.equal(next.failures, 1);
});

test('a release that installed and is later replaced did not fail', () => {
  const storage = memory();
  const watch = new ReleaseWatch({ storage });
  watch.under(A);
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
  watch.under(A);
  for (let i = 0; i < STUCK_AFTER; i++) {
    const worker = new Worker();
    watch.watch(worker);
    worker.to('redundant');
  }
  assert.equal(watch.stuck, true);
});
