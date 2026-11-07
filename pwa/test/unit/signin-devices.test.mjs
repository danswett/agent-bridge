import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readSignInDevices, signInDevicesLines } from '../../js/signin-devices.js';

const device = (name, extra = {}) => ({ name, kind: 'phone', platform: 'iOS', ...extra });

test('who can sign Copilot in reads as Creative wrote it, with this device named and more than three folded', () => {
  assert.deepEqual(signInDevicesLines(readSignInDevices({ state: 'ready', devices: [device('iPhone', { thisDevice: true }), device('Work laptop', { kind: 'app' })] })),
    { line: 'Can sign Copilot in: iPhone (this device), Work laptop', note: 'Removing a single device comes later.', all: null });
  assert.deepEqual(signInDevicesLines(readSignInDevices({ state: 'ready', devices: ['iPhone', 'Work laptop', 'Kitchen', 'Tablet'].map((n) => device(n)) })),
    { line: 'Can sign Copilot in: iPhone, Work laptop and 2 more', note: 'Removing a single device comes later.', all: 'iPhone, Work laptop, Kitchen, Tablet' });
  assert.deepEqual(signInDevicesLines(readSignInDevices({ state: 'none', devices: [] })),
    { line: 'No devices can sign Copilot in here yet.', note: 'Adding a device comes in a later update.', all: null });
  assert.deepEqual(signInDevicesLines(readSignInDevices({ state: 'unreadable', devices: [] })),
    { line: "Can't read the device list on this machine.", note: null, all: null });
});

test("nobody is listed unless the roster was read, and nothing that is not a person's named device is", () => {
  const lines = (r) => signInDevicesLines(readSignInDevices(r));
  const unreadable = { line: "Can't read the device list on this machine.", note: null, all: null };
  assert.equal(signInDevicesLines(null), null);
  assert.equal(readSignInDevices([]), null);
  assert.deepEqual(lines({ state: 'maybe', devices: [device('iPhone')] }), unreadable);
  assert.deepEqual(lines({ state: 42, devices: [device('iPhone')] }), unreadable);
  assert.deepEqual(lines({ state: 'unreadable', devices: [device('iPhone')] }), unreadable);
  assert.equal(lines({ state: 'none', devices: [device('iPhone')] }).line, 'No devices can sign Copilot in here yet.');
  assert.equal(lines({ state: 'ready', devices: [device('Host', { kind: 'host' }), device(' '), { name: 7, kind: 'phone' }, 'iPhone', device('iPhone', { thisDevice: 'yes' })] }).line,
    'Can sign Copilot in: iPhone');
});