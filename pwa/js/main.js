// Agent Bridge PWA: boot and wiring. The pieces:
//   identity.js      this device's non-extractable key (IndexedDB)
//   signin.js        GitHub device flow through the relay; the stored account
//   relay.js         one relay WebSocket: key proof, presence, routed frames
//   connection.js    the relay over time: reconnect with backoff, wake on visibility
//   channels.js      Noise_XX_P256 channels to hosts through the relay
//   host-client.js   the sessions protocol over a channel: requests, events, seq gaps
//   host-session.js  which machines are shown (one, or all): a channel -> HostClient -> store
//                    for each, across reconnects
//   answers.js       building and checking an answer before it is sent
//   answer-service.js sending answers: one outcome each, resent with the same request
//   replies.js, reply-service.js  checking and sending replies (session.reply), likewise
//   attachments.js   preparing files and images for a reply, and sending them first
//   stop-service.js  ending sessions (session.stop), likewise, two presses when not idle
//   launch.js, launch-service.js  starting and resuming sessions (session.launch), likewise
//   transfer.js      bringing a session from another machine: its sessions, a signed command
//   push.js          notifications: turning them on and off, registering with each machine
//   usage.js, fleet.js  agent usage merged across machines, and each machine's status line
//   sessions-store.js, ui/*  what is shown; ui/sessions-page.js wires cards and launches to
//                    the machine each is for
//   demo.js          ?demo=1: simulated machines (?machines=2 or 3), no relay
import { RELAY } from '../config.js';
import { loadIdentity, deviceName } from './identity.js';
import { accountStore, startDeviceFlow } from './signin.js';
import { BridgeConnection } from './connection.js';
import { ALL, HostSession } from './host-session.js';
import { HostClient } from './host-client.js';
import { TRANSFER, collectRemote, signTransfer } from './transfer.js';
import { PushRegistrar, closeMachineNotices, closeNotices, closeStaleNotices, fetchVapidKey } from './push.js';
import { Capabilities } from './protocol.js';
import { MachinesFold, offlineMachines } from './ui/views.js';
import { SessionsPage } from './ui/sessions-page.js';
import { UsageBook } from './usage.js';
import { FleetStatus } from './fleet.js';
import { ForgetControl, CONFIRM_MS, vanished } from './forget.js';
import { UsageView } from './ui/usage-view.js';
import { DemoBlobStore, DemoConnection, DemoHost, demoMachines, demoTransfers, runScript } from './demo.js';
import { RelayBlobs } from './blobs.js';

const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);
const BASE = RELAY || location.origin;

// Every machine's latest usage report, drawn as one row per allowance.
const book = new UsageBook();
new UsageView($('usage'), book);
const machinesFold = new MachinesFold($('machines'));
// The sessions on screen and what their cards and the launch panel send, once begun.
let page = null;

// ---- links: a notification's ?host=&session=&question= ----
// The session a link asked for, until its card is on screen: { host, session }.
let linked = null;
// Set once signed in: goes to a link's machine.
let followLink = null;

const ID = /^[0-9a-f]{32}$/;
const param = (p, name) => { const v = p.get(name); return v && v.length <= 256 ? v : null; };

// A link's machine, session and question, taken out of the address so neither a reload nor a
// later sign-in, possibly as another account, reuses it.
function takeLink() {
  const link = { host: param(params, 'host'), session: param(params, 'session'), question: param(params, 'question') };
  if (!['host', 'session', 'question'].some((k) => params.has(k))) { return null; }
  for (const k of ['host', 'session', 'question']) { params.delete(k); }
  const rest = params.toString();
  history.replaceState(null, '', `${location.pathname}${rest ? `?${rest}` : ''}${location.hash}`);
  return { ...link, host: link.host && ID.test(link.host) ? link.host : null };
}

// Opens the session a notification was about, with its question ready to answer: on its own
// machine, shown alone or with every other.
function focusLinked() {
  if (!linked || !page?.hosts.shows(linked.host)) { return; }
  const card = page.view.card(linked.host, linked.session);
  if (!card) { return; }
  linked = null;
  if (card.folded) { card.setFolded(false); }
  const el = card.element;
  el.classList.add('linked');
  setTimeout(() => el.classList.remove('linked'), 4000);
  el.scrollIntoView({ block: 'start' });
  const target = el.querySelector('.question-slot button:not([disabled]), .question-slot input:not([disabled]), .question-slot textarea:not([disabled])') ?? el.querySelector('.fold');
  target?.focus({ preventScroll: true });
}

// A tap on a notification while the app is open: the worker says where to go.
navigator.serviceWorker?.addEventListener('message', (e) => {
  if (e.data?.type !== 'agent-bridge.open' || typeof e.data.url !== 'string') { return; }
  let p;
  try { p = new URL(e.data.url).searchParams; } catch { return; }
  const host = param(p, 'host');
  if (!host || !ID.test(host)) { return; }
  followLink?.({ host, session: param(p, 'session'), question: param(p, 'question') });
});
navigator.serviceWorker?.startMessages?.();

function setPill(text, cls = '') {
  const p = $('conn');
  p.textContent = text;
  p.className = `pill ${cls}`.trim();
}

function setHostState(text) {
  if (page) { page.setHostState(text); return; }
  $('host-state').textContent = text;
  $('host-state').hidden = !text;
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
// The demo's own remembered choices, apart from the live app's on the same site.
const demoStorage = {
  getItem: (k) => localStorage.getItem(`demo:${k}`),
  setItem: (k, v) => localStorage.setItem(`demo:${k}`, v),
  removeItem: (k) => localStorage.removeItem(`demo:${k}`),
};

async function startDemo() {
  $('demo-banner').hidden = false;
  setPill('Demo', 'warn');
  const fixture = await (await fetch('fixtures/sessions-list.json', { cache: 'no-store' })).json();
  // ?ahb=1: a machine where agent-ha-bridge runs, so it takes nothing from the phone and says why.
  const ahb = params.get('ahb') === '1';
  const answerDelayMs = Number(params.get('answerDelay') ?? 700);
  const stopWindowMs = Number(params.get('stopWindow')) || 10000;
  const host = new DemoHost(fixture, {
    readOnly: ahb ? 'agent-ha-bridge is running for this user (AgentBridgeDaemon); answer at the machine or through it' : null,
    answerDelayMs,
    answering: params.get('answer') !== '0' && !ahb,
    replying: params.get('reply') !== '0' && params.get('answer') !== '0' && !ahb,
    attaching: params.get('attach') !== '0',
    // ?blobs=0: a host that takes no files through the blob store, as before it existed.
    large: params.get('blobs') !== '0',
    stopping: params.get('stop') !== '0' && params.get('answer') !== '0' && !ahb,
    approving: params.get('approve') !== '0',
    stopWindowMs,
    usage: params.get('usage') !== '0',
    launching: params.get('launch') !== '0' && params.get('answer') !== '0' && !ahb,
    // ?transfer=0: a host from before sessions came from other machines.
    transferring: params.get('transfer') !== '0',
  });
  // ?machines=2 or 3: the account's other machines, after this one.
  const machines = [host, ...demoMachines(Number(params.get('machines')) || 1, { answerDelayMs, stopWindowMs })];
  // ?attachDelay= slows each file chunk, so its progress can be watched.
  for (const m of machines) { m.attachmentDelayMs = Number(params.get('attachDelay')) || 0; }
  // The relay's blob store, in the page. ?blobMax= sets the largest blob it takes (stored bytes),
  // ?uploadDelay= slows each upload.
  const blobStore = new DemoBlobStore(params.get('blobMax') ? { max: Number(params.get('blobMax')) } : {});
  blobStore.uploadDelayMs = Number(params.get('uploadDelay')) || 0;
  for (const m of machines) { m.blobStore = blobStore; }
  // The relay's roster, in the page (?offline=1): one of the account's machines that is offline,
  // which Forget takes off as the relay would. ?forgetMs= shortens how long an arm lasts.
  const connection = new DemoConnection(machines, {
    known: params.get('offline') === '1'
      ? [{ id: 'd0e1f2a3b4c5d6e7f8091a2b3c4d5e6f', name: 'Travel laptop', kind: 'host', lastSeen: new Date(Date.now() - 3 * 86400000).toISOString() }]
      : null,
  });
  // ?timeout= shortens how long a request waits for its response, for the tests of resends.
  const timeoutMs = Number(params.get('timeout')) || 0;
  const timeouts = timeoutMs ? { timeoutMs, answerTimeoutMs: timeoutMs, replyTimeoutMs: timeoutMs, stopTimeoutMs: timeoutMs, launchTimeoutMs: timeoutMs, transferTimeoutMs: timeoutMs } : {};
  const hosts = new HostSession({ connection, storage: demoStorage, makeClient: (ch, store) => new HostClient(ch, { store, ...timeouts }) });
  const forgetOutcomes = [];
  const demoForget = new ForgetControl({
    connection: {
      forget: async (id) => {
        const scripted = forgetOutcomes.shift();
        if (scripted) { return scripted; }
        connection.known = connection.known.filter((k) => k.id !== id);
        return null;
      },
    },
    confirmMs: Number(params.get('forgetMs')) || CONFIRM_MS,
  });
  const fleet = new FleetStatus({ connection, hosts, book, me: null });
  const transfers = new Map();
  for (const m of machines) {
    if (m.capabilities.includes(TRANSFER.capability)) { transfers.set(m.machine.id, await demoTransfers(m, m.machine.id)); }
  }
  page = new SessionsPage({
    $, hosts, connection, machinesFold,
    blobs: () => blobStore, relayMax: () => blobStore.max,
    retryMs: Number(params.get('retry')) || 3000,
    transfers: (id) => transfers.get(id) ?? null,
    status: (id) => fleet.status(id),
    forget: demoForget,
    storage: demoStorage,
    onRendered: () => focusLinked(),
  });
  demoForget.onChange(() => page.drawMachines());
  fleet.onChange(() => page.drawMachines());
  $('machines').hidden = false;
  hosts.restoreTarget(null);
  fleet.start();
  // Ready once every machine shown has sent what it has.
  await new Promise((resolve) => {
    const check = () => { if (hosts.links().every((l) => l.phase === 'open' && l.client?.synced)) { resolve(); } };
    hosts.onChange(check);
    hosts.sessions.onChange(check);
    check();
  });
  const script = params.get('static') === '1' ? null : runScript(host, { tickMs: Number(params.get('tick')) || 4000 });
  const { answers, replies, stops, launches, launchPanel, view } = page;
  window.agentBridgeDemo = {
    host, hosts, connection, store: hosts.sessions, view, answers, replies, stops, launches, launchPanel, book, blobs: blobStore, page,
    // Each demo machine, by its id: 'demo', 'demo-laptop', 'demo-shadow'.
    machines: Object.fromEntries(machines.map((m) => [m.machine.id, m])),
    get client() { return hosts.link(host.machine.id)?.client ?? null; },
    // The launch panel told about the same machine again, as presence updates tell it.
    retarget: () => page.refresh(),
    // A new usage report from the host, sent as usage.update.
    usage: (report) => host.setUsage(report),
    // The next answers' outcomes, in order: 'delivered' (the default once these run out),
    // 'unconfirmed', 'stale', 'busy', 'refused', an unknown one, or { outcome, reason,
    // drop: true } to lose the response the first time, as a dropped channel would.
    script: (...outcomes) => host.scriptAnswers(outcomes),
    // The same, for the next replies.
    scriptReplies: (...outcomes) => host.scriptReplies(outcomes),
    // The same, for the next stops; none scripted, the host decides as a real one would.
    scriptStops: (...outcomes) => host.scriptStops(outcomes),
    // The same, for the next launches: 'started' (the default), 'unconfirmed', 'busy', 'refused',
    // 'failed', an unknown one, or { outcome, reason, drop: true }.
    scriptLaunches: (...outcomes) => host.scriptLaunches(outcomes),
    // The next forgets' refusals, in order ('online', 'rate-limited', ...); none scripted, forgotten.
    scriptForgets: (...outcomes) => forgetOutcomes.push(...outcomes),
    // The offline machine back online: the relay stops listing it.
    reconnectOffline: () => { connection.known = []; demoForget.reconcile([]); page.drawMachines(); },
    // The offline machine back and gone again between two lists: seen later than when pressed.
    bounceOffline: () => {
      connection.known = connection.known.map((k) => ({ ...k, lastSeen: new Date().toISOString() }));
      demoForget.reconcile(connection.known);
      page.drawMachines();
    },
    // A machine leaves presence, or comes back.
    setOnline: (id, online) => connection.setOnline(id, online),
    upsert: (s) => host.upsert(s),
    remove: (id) => host.remove(id),
    hold: (held) => host.hold(held),
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
  $('notify').hidden = true;
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
  const hosts = new HostSession({ connection, me: identity.deviceId });
  const link = takeLink();
  linked = link?.host && link.session ? { host: link.host, session: link.session } : null;
  // A link's machine is shown: with every other when every machine is, else on its own.
  followLink = ({ host, session }) => {
    linked = session ? { host, session } : null;
    if (!hosts.all || !hosts.isOnline(host)) { hosts.select(host); }
    focusLinked();
  };
  const fleet = new FleetStatus({ connection, hosts, book, me: identity.deviceId });
  // Forgetting an offline machine (#50): what this app keeps about it goes with it. Its push
  // subscription is on that machine, which is gone; the notices it left here are closed.
  const dropMachine = (id) => {
    fleet.forget(id);
    if (hosts.targetId === id) { hosts.select(ALL); }
    worker().then((r) => closeMachineNotices(r, id)).catch(() => {});
  };
  const forgets = new ForgetControl({ connection, onForgotten: dropMachine });
  // Answers, replies, stops and launches go to the machine they were given for, on whichever
  // channel to it is open now: after a reconnect, one still waiting for its outcome is resent
  // with the same request. Large files go through the blob store of the relay this page is
  // connected to, when it has one and the machine fetches from it (sessions.attach.blob).
  page = new SessionsPage({
    $, hosts, connection, me: identity.deviceId, machinesFold,
    blobs: () => (connection.relay ? new RelayBlobs(connection.relay) : null),
    relayMax: () => connection.relay?.blobs?.max ?? 0,
    // The account's other machines' sessions, each signed for when picked.
    transfers: (targetId) => ({
      remotes: () => collectRemote({ connection, targetId, me: identity.deviceId, makeClient: (ch) => new HostClient(ch) }),
      sign: (remote, session) => signTransfer({ key: identity.key, deviceId: identity.deviceId, machinePublic: remote.key, session, to: targetId }),
    }),
    status: (id) => fleet.status(id),
    forget: forgets,
    onRendered: () => focusLinked(),
  });
  const { answers, replies, stops, launches } = page;

  // ---- notifications ----
  const worker = () => new Promise((resolve, reject) => {
    if (!navigator.serviceWorker) { reject(new Error('This browser has no service worker.')); return; }
    const timer = setTimeout(() => reject(new Error("The app's service worker isn't running. Reload and try again.")), 10000);
    navigator.serviceWorker.ready.then((r) => { clearTimeout(timer); resolve(r); }, reject);
  });
  const notices = new PushRegistrar({
    registration: worker,
    fetchKey: () => fetchVapidKey(BASE),
    appUrl: new URL('./', location.href).href,
    device: deviceName(),
  });
  // The machines shown with a channel open now: each one registers this device for its
  // notifications, as a single machine shown always has.
  const openLinks = () => hosts.links().filter((l) => l.phase === 'open' && l.client);
  const nameOf = (l) => l.machine?.name ?? hosts.nameOf(l.id) ?? 'This machine';
  const listed = (names) => (names.length < 2 ? names.join('') : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`);
  const registered = new WeakSet();
  const drawNotify = () => {
    const support = notices.support;
    const on = notices.enabled;
    const button = $('notify-toggle');
    const silent = openLinks().filter((l) => !l.capabilities.includes(Capabilities.push)).map(nameOf);
    const notes = {
      install: 'On iPhone and iPad, notifications need Agent Bridge on the Home Screen: tap Share, then Add to Home Screen, and open it from there.',
      unsupported: "This browser can't show notifications from Agent Bridge.",
      denied: 'Notifications are blocked for Agent Bridge. Allow them in Settings to turn them on.',
    };
    $('notify').hidden = false;
    button.hidden = support !== 'ok';
    button.disabled = !!notices.busy;
    button.textContent = notices.busy === 'on' ? 'Turning on…' : notices.busy === 'off' ? 'Turning off…' : on ? 'Turn off notifications' : 'Turn on notifications';
    button.setAttribute('aria-pressed', String(on));
    $('notify-note').textContent = notes[support] ?? (!on
      ? 'Get a notification when a session asks you something or finishes, even with the app closed.'
      : silent.length
        ? `On for this device, but ${listed(silent)} ${silent.length === 1 ? "doesn't" : "don't"} send notifications yet.`
        : 'On for this device. The machines you open here notify it when a session asks you something or finishes.');
    $('notify-error').hidden = !notices.error;
    $('notify-error').textContent = notices.error;
  };
  notices.onChange(drawNotify);
  // The Settings app can change the permission while this is in the background.
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') { drawNotify(); } });
  $('notify-toggle').onclick = () => {
    const [first, ...rest] = openLinks();
    // Not awaited before asking: enable() asks for permission first, inside this tap. The
    // other machines shown register once it is on.
    const done = notices.enabled
      ? notices.disable(first?.client ?? null)
      : notices.enable(first?.client ?? null, first?.capabilities ?? []).then(() => Promise.all(rest.map((l) => notices.register(l.client, l.capabilities))));
    for (const l of [first, ...rest]) { if (l) { registered.add(l.client); } }
    done.catch((e) => console.warn('notifications:', e.message));
  };
  const registerHere = () => {
    for (const l of openLinks()) {
      if (registered.has(l.client)) { continue; }
      registered.add(l.client);
      notices.register(l.client, l.capabilities).catch((e) => {
        notices.error = `Couldn't register for notifications from ${nameOf(l)}: ${e.message}`;
        drawNotify();
      });
    }
  };
  // A question answered here needs no notification; and one answered anywhere else must not
  // stay on the lock screen looking unanswered once this app has seen it go.
  answers.onAny((a) => {
    if (a.state === 'done' && ['delivered', 'unconfirmed', 'stale'].includes(a.outcome)) {
      worker().then((r) => closeNotices(r, a.hostId, a.sessionId)).catch(() => {});
    }
  });
  let staleTimer = null;
  hosts.sessions.onChange((c) => {
    if (c.type !== 'synced' && !hosts.link(c.hostId)?.client?.synced) { return; }
    clearTimeout(staleTimer);
    staleTimer = setTimeout(() => {
      const synced = openLinks().filter((l) => l.client.synced);
      if (synced.length) { worker().then((r) => Promise.all(synced.map((l) => closeStaleNotices(r, l.id, l.store)))).catch(() => {}); }
    }, 500);
  });

  // The same for a machine forgotten from another of the account's devices: it leaves the
  // offline list without coming online.
  let lastKnown = null;
  const offlineNow = () => offlineMachines({ presence: connection.presence, known: connection.known, me: identity.deviceId });
  fleet.onChange(() => page.drawMachines());
  forgets.onChange(() => page.drawMachines());
  hosts.restoreTarget(link?.host ?? null);
  fleet.start();
  // "last seen 5m ago" moves on with nothing else to redraw the list: every machine may be offline.
  setInterval(() => { if (document.visibilityState !== 'hidden' && connection.known?.length) { page.drawMachines(); } }, 30000);

  connection.on('presence', () => {
    const now = connection.known ? offlineNow() : null;
    for (const id of vanished(lastKnown, now, connection.presence.map((d) => d.id))) { dropMachine(id); }
    lastKnown = now;
    // A machine that comes back takes its arm, and any refusal shown for it, with it.
    forgets.reconcile(now ?? []);
  });
  connection.on('presence', () => page.drawMachines());
  connection.on('log', (line) => console.log(line));
  connection.on('state', ({ state }) => {
    $('replaced-banner').hidden = state !== 'replaced';
    switch (state) {
      case 'connecting': return setPill('Connecting…');
      case 'connected': page.refresh(); return setPill('Connected', 'ok');
      case 'reconnecting': page.refresh(); return setPill('Reconnecting…', 'warn');
      case 'replaced': return setPill('Paused', 'warn');
      case 'signed-out': return undefined;
      default: return setPill(state);
    }
  });
  connection.on('signed-out', () => {
    // The account's sessions and remembered machine go with it; a reconnect within the
    // same sign-in keeps them, but a new sign-in may be a different account.
    hosts.forget();
    fleet.clear();
    forgets.clear();
    lastKnown = null;
    page.clear();
    accountStore.clear();
    connection.stop();
    // This account's machines must not go on notifying this device.
    notices.forget().catch(() => {});
    showSignIn('The relay no longer accepts this sign-in. Sign in again.');
  });
  $('use-here').onclick = () => connection.start();

  hosts.onChange(() => {
    registerHere();
    drawNotify();
  });

  $('signout').onclick = async () => {
    hosts.forget();
    fleet.clear();
    page.clear();
    accountStore.clear();
    connection.stop();
    // Not for long: signing out must not wait on a service worker that isn't running.
    await Promise.race([notices.forget().catch(() => {}), new Promise((r) => { setTimeout(r, 2000); })]);
    location.replace(location.pathname);
  };

  // For the E2E tests and debugging from the console. Holds no secrets beyond what the
  // page already has.
  window.agentBridge = { connection, hosts, store: hosts.sessions, answers, replies, stops, launches, notices, fleet, book, page };
  drawNotify();
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
