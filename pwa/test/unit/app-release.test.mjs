import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AppRelease } from '../../js/app-release.js';

const A = 'a'.repeat(40);
const B = 'b'.repeat(40);
const C = 'c'.repeat(40);
const builtAt = '2026-11-08T20:00:00Z';
const response = (version = B) => ({ ok: true, json: async () => ({ version, builtAt }) });

class Worker extends EventTarget {
  state = 'installing';
  to(state) {
    this.state = state;
    this.dispatchEvent(new Event('statechange'));
  }
}

class Registration extends EventTarget {
  installing = null;
  waiting = null;
  updates = 0;
  async update() { this.updates++; }
  found(worker = new Worker()) {
    this.installing = worker;
    this.dispatchEvent(new Event('updatefound'));
    return worker;
  }
}

function setup(fetchVersion = async () => response(), registration = new Registration()) {
  const phases = [];
  const app = new AppRelease({ version: A, fetchVersion, onChange: (state) => phases.push(state.phase) });
  app.attach(registration);
  return { app, registration, phases };
}

test('checking bypasses the cache, ignores repeated presses and becomes current only for the running id', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const requests = [];
  const { app, registration, phases } = setup((...args) => {
    requests.push(args);
    return new Promise((resolve) => setTimeout(() => resolve(response(A)), 25));
  });
  assert.equal(app.state.phase, 'idle');
  const check = app.check();
  assert.equal(app.state.phase, 'checking');
  await app.check();
  assert.equal(requests.length, 1);
  assert.equal(requests[0][0], 'version.json');
  assert.equal(requests[0][1].cache, 'no-store');
  t.mock.timers.tick(25);
  await check;
  assert.equal(registration.updates, 1);
  assert.deepEqual(phases, ['checking', 'current']);
  assert.deepEqual(app.state.latest, { version: A, builtAt });
});

test('a different published id is behind until its worker installs and takes over', async () => {
  const { app, registration, phases } = setup();
  await app.check();
  assert.equal(app.state.phase, 'behind');
  const worker = registration.found();
  assert.equal(app.state.phase, 'installing');
  worker.to('installed');
  worker.to('activating');
  assert.equal(app.state.phase, 'installing', 'installed alone does not prove takeover');
  app.tookOver();
  assert.equal(app.state.phase, 'ready');
  worker.to('activated');
  worker.to('redundant');
  assert.equal(app.state.phase, 'ready', 'a later worker cannot undo a confirmed takeover');
  assert.deepEqual(phases, ['checking', 'behind', 'installing', 'ready']);
});

test('a worker found during update is observed even if it finishes before update resolves', async () => {
  const { app, registration } = setup();
  registration.update = async () => registration.found().to('activated');
  await app.check();
  assert.equal(app.state.phase, 'ready');
});

test('an existing installing or waiting worker is observed without waiting for updatefound', async () => {
  for (const slot of ['installing', 'waiting']) {
    const registration = new Registration();
    const worker = new Worker();
    if (slot === 'waiting') { worker.to('installed'); }
    registration[slot] = worker;
    const { app } = setup(undefined, registration);
    await app.check();
    assert.equal(app.state.phase, 'installing');
    worker.to('activated');
    assert.equal(app.state.phase, 'ready');
  }
});

test('a failed install returns to behind and events from a replaced worker are ignored', async () => {
  const { app, registration } = setup();
  await app.check();
  const old = registration.found();
  const current = registration.found();
  old.to('activated');
  assert.equal(app.state.phase, 'installing');
  current.to('redundant');
  assert.equal(app.state.phase, 'behind');
});

test('offline, rejected HTTP, malformed metadata and update failures preserve the last result and allow retry', async () => {
  for (const failure of [
    () => { throw new Error('private network cause'); },
    () => ({ ok: false }),
    () => ({ ok: true, json: async () => { throw new Error('private JSON cause'); } }),
    () => response('not-a-release'),
    () => ({ ok: true, json: async () => ({ version: B, builtAt: 'not-a-date' }) }),
    () => ({ ok: true, json: async () => null }),
    null,
  ]) {
    let fail = false;
    const { app, registration } = setup(async () => fail && failure ? failure() : response());
    await app.check();
    const before = app.state;
    fail = true;
    if (!failure) { registration.update = async () => { throw new Error('private worker cause'); }; }
    await app.check();
    assert.deepEqual(app.state, { ...before, error: "Couldn't check for updates." });
    fail = false;
    registration.update = async () => {};
    await app.check();
    assert.equal(app.state.error, '');
    assert.equal(app.state.phase, 'behind');
  }
});

test('a failed first check stays idle and a browser without a registration can still report published versions', async () => {
  const { app } = setup(async () => { throw new Error('offline'); }, null);
  await app.check();
  assert.deepEqual(app.state, { phase: 'idle', latest: null, error: "Couldn't check for updates." });
  const available = setup(undefined, null).app;
  await available.check();
  assert.equal(available.state.phase, 'behind');
});

test('a ready update stays ready when an even newer release is published', async () => {
  let latest = B;
  const { app, registration } = setup(async () => response(latest));
  await app.check();
  registration.found().to('activated');
  assert.equal(app.state.phase, 'ready');
  latest = C;
  registration.installing = null;
  await app.check();
  assert.equal(app.state.phase, 'ready', 'Reload still offers a real update from the running page');
  assert.equal(app.state.latest.version, C, 'publication metadata does not identify the activated worker');
  registration.found().to('activated');
  assert.equal(app.state.phase, 'ready');
});

test('a worker discovered before the next metadata check can still offer an update from the running page', async () => {
  let latest = B;
  const { app, registration } = setup(async () => response(latest));
  await app.check();
  const earlier = registration.found();
  latest = C;
  await app.check();
  earlier.to('activated');
  app.tookOver();
  assert.equal(app.state.phase, 'ready');
  assert.equal(app.state.latest.version, C, 'ready does not claim that published C is installed');
  registration.found().to('activated');
  assert.equal(app.state.phase, 'ready');
});

test('attaching an already-active worker does not claim an update, but an observed takeover before checking does', async () => {
  const registration = new Registration();
  registration.installing = new Worker();
  registration.installing.to('activated');
  const { app } = setup(undefined, registration);
  await app.check();
  assert.equal(app.state.phase, 'behind');
  registration.found().to('activated');
  await app.check();
  assert.equal(app.state.phase, 'ready');
  const early = setup().app;
  early.tookOver();
  assert.equal(early.state.phase, 'ready');
  await early.check();
  assert.equal(early.state.phase, 'ready');
});

test('disposing during a pending update never reattaches a worker listener', async () => {
  let finishUpdate;
  const { app, registration } = setup();
  const worker = registration.found();
  let listeners = 0;
  const add = worker.addEventListener.bind(worker);
  const remove = worker.removeEventListener.bind(worker);
  worker.addEventListener = (...args) => { listeners++; add(...args); };
  worker.removeEventListener = (...args) => { listeners--; remove(...args); };
  registration.update = () => new Promise((resolve) => { finishUpdate = resolve; });
  const check = app.check();
  while (!finishUpdate) { await Promise.resolve(); }
  app.dispose();
  const detached = listeners;
  finishUpdate();
  await check;
  assert.equal(listeners, detached);
});

test('replacing a registration and disposing remove obsolete listeners and suppress late check results', async () => {
  let resolve;
  const { app, registration } = setup(() => new Promise((done) => { resolve = done; }));
  const check = app.check();
  app.attach(new Registration());
  registration.found().to('activated');
  app.dispose();
  resolve(response());
  await check;
  assert.equal(app.state.phase, 'checking', 'disposed objects publish nothing more');
});
