// Agent Bridge PWA: boot and wiring. The pieces:
//   identity.js      this device's non-extractable key (IndexedDB)
//   signin.js        GitHub device flow through the relay; the stored account
//   relay.js         one relay WebSocket: key proof, presence, routed frames
//   connection.js    the relay over time: reconnect with backoff, wake on visibility
//   channels.js      Noise_XX_P256 channels to hosts through the relay
//   host-client.js   the sessions protocol over a channel: requests, events, seq gaps
//   host-session.js  which host is shown; channel -> HostClient -> store, across reconnects
//   sessions-store.js, ui/*  what is shown
//   demo.js          ?demo=1: a simulated host, no relay
import { RELAY } from '../config.js';
import { loadIdentity, deviceName } from './identity.js';
import { accountStore, startDeviceFlow } from './signin.js';
import { BridgeConnection } from './connection.js';
import { HostSession } from './host-session.js';
import { HostClient } from './host-client.js';
import { SessionsStore } from './sessions-store.js';
import { SessionListView, renderMachines } from './ui/views.js';
import { DemoHost, runScript } from './demo.js';

const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);
const BASE = RELAY || location.origin;

const store = new SessionsStore();
let currentClient = null;
let machineName = '';

const view = new SessionListView($('session-list'), store, {
  fetchDetail: (id) => (currentClient ? currentClient.getSession(id) : Promise.reject(new Error('not connected'))),
  machineName: () => machineName,
});
view.onRender = (n) => {
  $('session-count').textContent = n ? `${n} live` : '';
  $('sessions-empty').hidden = n !== 0 || !$('host-state').hidden;
};

function setPill(text, cls = '') {
  const p = $('conn');
  p.textContent = text;
  p.className = `pill ${cls}`.trim();
}

function setHostState(text) {
  $('host-state').textContent = text;
  $('host-state').hidden = !text;
  view.onRender(store.size);
}

function showSessionsFor(name) {
  machineName = name;
  $('sessions-h').textContent = name ? `Sessions on ${name}` : 'Sessions';
  $('sessions').hidden = false;
}

// ---- service worker and updates ----
// An installed PWA keeps running the code it has until restarted (S5 limitation), so a
// new version is announced with a reload button instead of waiting for a force-quit.
function registerServiceWorker() {
  if (!('serviceWorker' in navigator)) { return; }
  const hadController = !!navigator.serviceWorker.controller;
  navigator.serviceWorker.register('sw.js').then((reg) => {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') { reg.update().catch(() => {}); }
    });
  }).catch((e) => console.warn('service worker:', e.message));
  navigator.serviceWorker.addEventListener('controllerchange', () => {
    if (hadController) { $('update-banner').hidden = false; }
  });
  $('reload').onclick = () => location.reload();
}

// ---- demo ----
async function startDemo() {
  $('demo-banner').hidden = false;
  setPill('Demo', 'warn');
  const fixture = await (await fetch('fixtures/sessions-list.json', { cache: 'no-store' })).json();
  const host = new DemoHost(fixture);
  const client = new HostClient(host.connect(), { store });
  currentClient = client;
  const hello = await client.hello();
  $('machines').hidden = false;
  renderMachines($('machine-list'), {
    presence: [{ id: hello.machine.id, name: hello.machine.name, kind: 'host' }],
    me: null,
    current: hello.machine.id,
    onSelect: () => {},
  });
  showSessionsFor(hello.machine.name);
  setHostState('');
  await client.subscribe();
  const script = params.get('static') === '1' ? null : runScript(host, { tickMs: Number(params.get('tick')) || 4000 });
  window.agentBridgeDemo = {
    host, client, store, view,
    upsert: (s) => host.upsert(s),
    remove: (id) => host.remove(id),
    frame: (f) => host.frame(f),
    get calls() { return [...host.calls]; },
    pause: () => script?.pause(),
    resume: () => script?.resume(),
  };
  document.documentElement.dataset.demoReady = '1';
}

// ---- live ----
let identity;

function showSignIn(error) {
  $('signin').hidden = false;
  $('machines').hidden = true;
  $('sessions').hidden = true;
  $('account').hidden = true;
  $('signin-error').hidden = !error;
  $('signin-error').textContent = error ?? '';
  setPill('Signed out');
}

$('signin-button').onclick = async () => {
  const button = $('signin-button');
  button.disabled = true;
  $('signin-error').hidden = true;
  try {
    const flow = await startDeviceFlow(BASE);
    $('code').textContent = flow.userCode;
    $('verify').href = flow.verificationUri;
    $('verify').textContent = flow.verificationUri.replace(/^https?:\/\//, '');
    $('code-box').hidden = false;
    const account = await flow.wait();
    accountStore.save(account);
    $('code-box').hidden = true;
    $('signin').hidden = true;
    begin(account);
  } catch (e) {
    $('code-box').hidden = true;
    $('signin-error').hidden = false;
    $('signin-error').textContent = e.message;
  } finally {
    button.disabled = false;
  }
};

function begin(account) {
  $('account').hidden = false;
  $('login').textContent = account.login ?? '';
  $('me').textContent = identity.deviceId.slice(0, 8);
  $('machines').hidden = false;

  const connection = new BridgeConnection({ base: BASE, key: identity.key, deviceId: identity.deviceId, name: deviceName(), account });
  const hosts = new HostSession({ connection, store });
  const fromLink = /^[0-9a-f]{32}$/.test(params.get('host') ?? '') ? params.get('host') : null;
  hosts.restoreTarget(fromLink);

  const nameOf = (id) => connection.presence.find((d) => d.id === id)?.name;
  const drawMachines = () => {
    const n = renderMachines($('machine-list'), {
      presence: connection.presence, me: identity.deviceId, current: hosts.targetId,
      onSelect: (id) => { hosts.select(id); $('sessions').focus(); },
    });
    $('machines-empty').hidden = n !== 0 || !connection.ready;
  };

  connection.on('presence', drawMachines);
  connection.on('log', (line) => console.log(line));
  connection.on('state', ({ state }) => {
    $('replaced-banner').hidden = state !== 'replaced';
    switch (state) {
      case 'connecting': return setPill('Connecting…');
      case 'connected': drawMachines(); return setPill('Connected', 'ok');
      case 'reconnecting': return setPill('Reconnecting…', 'warn');
      case 'replaced': return setPill('Paused', 'warn');
      case 'signed-out': return undefined;
      default: return setPill(state);
    }
  });
  connection.on('signed-out', () => {
    accountStore.clear();
    connection.stop();
    showSignIn('The relay no longer accepts this sign-in. Sign in again.');
  });
  $('use-here').onclick = () => connection.start();

  hosts.onChange(() => {
    currentClient = hosts.client;
    drawMachines();
    if (!hosts.targetId) { $('sessions').hidden = true; return; }
    const name = hosts.machine?.name ?? nameOf(hosts.targetId) ?? 'your machine';
    showSessionsFor(name);
    switch (hosts.phase) {
      case 'waiting': return setHostState(hosts.error || (hosts.targetOnline ? `Connecting to ${name}…` : `${name} is offline. Its sessions will show when it is back.`));
      case 'opening': return setHostState(`Opening an end-to-end encrypted channel to ${name}…`);
      case 'open': return setHostState('');
      case 'unsupported': return setHostState(hosts.error);
      case 'failed': return setHostState(`Couldn't reach ${name}: ${hosts.error}. Trying again.`);
      default: return setHostState('');
    }
  });

  $('signout').onclick = () => {
    accountStore.clear();
    connection.stop();
    location.replace(location.pathname);
  };

  // For the E2E tests and debugging from the console. Holds no secrets beyond what the
  // page already has.
  window.agentBridge = { connection, hosts, store };
  connection.start();
}

async function startLive() {
  identity = await loadIdentity();
  const account = accountStore.load();
  if (account) { begin(account); } else { showSignIn(); }
}

registerServiceWorker();
(params.get('demo') === '1' ? startDemo() : startLive()).catch((e) => {
  console.error(e);
  setPill('Error', 'bad');
  setHostState(e.message);
  $('sessions').hidden = false;
});
