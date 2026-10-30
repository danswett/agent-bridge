// Agent Bridge PWA: boot and wiring. The pieces:
//   identity.js      this device's non-extractable key (IndexedDB)
//   signin.js        GitHub device flow through the relay; the stored account
//   relay.js         one relay WebSocket: key proof, presence, routed frames
//   connection.js    the relay over time: reconnect with backoff, wake on visibility
//   channels.js      Noise_XX_P256 channels to hosts through the relay
//   host-client.js   the sessions protocol over a channel: requests, events, seq gaps
//   host-session.js  which host is shown; channel -> HostClient -> store, across reconnects
//   answers.js       building and checking an answer before it is sent
//   answer-service.js sending answers: one outcome each, resent with the same request
//   replies.js, reply-service.js  checking and sending replies (session.reply), likewise
//   attachments.js   preparing files and images for a reply, and sending them first
//   stop-service.js  ending sessions (session.stop), likewise, two presses when not idle
//   launch.js, launch-service.js  starting and resuming sessions (session.launch), likewise
//   transfer.js      bringing a session from another machine: its sessions, a signed command
//   push.js          notifications: turning them on and off, registering with each machine
//   usage.js, fleet.js  agent usage merged across machines, and each machine's status line
//   sessions-store.js, ui/*  what is shown
//   demo.js          ?demo=1: a simulated host, no relay
import { RELAY } from '../config.js';
import { loadIdentity, deviceName } from './identity.js';
import { accountStore, startDeviceFlow } from './signin.js';
import { BridgeConnection } from './connection.js';
import { HostSession } from './host-session.js';
import { HostClient } from './host-client.js';
import { SessionsStore } from './sessions-store.js';
import { AnswerService } from './answer-service.js';
import { ReplyService } from './reply-service.js';
import { StopService } from './stop-service.js';
import { LaunchService } from './launch-service.js';
import { LAUNCH } from './launch.js';
import { TRANSFER, collectRemote, signTransfer } from './transfer.js';
import { PushRegistrar, closeNotices, closeStaleNotices, fetchVapidKey } from './push.js';
import { Capabilities } from './protocol.js';
import { SessionListView, renderMachines } from './ui/views.js';
import { UsageBook, USAGE, machineStatus } from './usage.js';
import { FleetStatus } from './fleet.js';
import { UsageView } from './ui/usage-view.js';
import { LaunchPanel } from './ui/launch-view.js';
import { DemoBlobStore, DemoHost, demoTransfers, runScript } from './demo.js';
import { RelayBlobs, maxPlain } from './blobs.js';
import { AttachmentLimits } from './attachments.js';

const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);
const BASE = RELAY || location.origin;

const store = new SessionsStore();
// Every machine's latest usage report, drawn as one row per allowance.
const book = new UsageBook();
new UsageView($('usage'), book);
let currentClient = null;
let machineName = '';
// Who answers questions, sends replies and ends sessions now: { service, replies, stops,
// hostId, capabilities }, or null.
let answering = () => null;

const view = new SessionListView($('session-list'), store, {
  fetchDetail: (id) => (currentClient ? currentClient.getSession(id) : Promise.reject(new Error('not connected'))),
  machineName: () => machineName,
  answering: () => answering(),
});
view.onRender = (n) => {
  $('session-count').textContent = n ? `${n} live` : '';
  $('sessions-empty').hidden = n !== 0 || !$('host-state').hidden;
  focusLinked();
};

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

// Opens the session a notification was about, with its question ready to answer.
function focusLinked() {
  if (!linked || linked.host !== currentHost()) { return; }
  const card = view.card(linked.session);
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

let currentHost = () => null;

// A launched session's card, once it has arrived: unfolded and scrolled to. False until then.
function showSession(id) {
  const card = view.card(id);
  if (!card) { return false; }
  if (card.folded) { card.setFolded(false); }
  const el = card.element;
  el.classList.add('linked');
  setTimeout(() => el.classList.remove('linked'), 4000);
  el.scrollIntoView({ block: 'start' });
  el.querySelector('.fold')?.focus({ preventScroll: true });
  return true;
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

// The largest file a reply can carry through the blob store: what the machine and the relay
// both take, and never over the protocol's 100 MiB; 0 when either has none.
function largeLimit(capabilities, relayMax) {
  if (!capabilities?.includes(Capabilities.sessionsAttachBlob) || !(relayMax > 0)) { return 0; }
  return Math.min(AttachmentLimits.blobBytes, maxPlain(relayMax));
}

// ---- demo ----
async function startDemo() {
  $('demo-banner').hidden = false;
  setPill('Demo', 'warn');
  const fixture = await (await fetch('fixtures/sessions-list.json', { cache: 'no-store' })).json();
  // ?ahb=1: a machine where agent-ha-bridge runs, so it takes nothing from the phone and says why.
  const ahb = params.get('ahb') === '1';
  const host = new DemoHost(fixture, {
    readOnly: ahb ? 'agent-ha-bridge is running for this user (AgentBridgeDaemon); answer at the machine or through it' : null,
    answerDelayMs: Number(params.get('answerDelay') ?? 700),
    answering: params.get('answer') !== '0' && !ahb,
    replying: params.get('reply') !== '0' && params.get('answer') !== '0' && !ahb,
    attaching: params.get('attach') !== '0',
    // ?blobs=0: a host that takes no files through the blob store, as before it existed.
    large: params.get('blobs') !== '0',
    stopping: params.get('stop') !== '0' && params.get('answer') !== '0' && !ahb,
  approving: params.get('approve') !== '0',
    stopWindowMs: Number(params.get('stopWindow')) || 10000,
    usage: params.get('usage') !== '0',
    launching: params.get('launch') !== '0' && params.get('answer') !== '0' && !ahb,
    // ?transfer=0: a host from before sessions came from other machines.
    transferring: params.get('transfer') !== '0',
  });
  // ?attachDelay= slows each file chunk, so its progress can be watched.
  host.attachmentDelayMs = Number(params.get('attachDelay')) || 0;
  // The relay's blob store, in the page. ?blobMax= sets the largest blob it takes (stored bytes),
  // ?uploadDelay= slows each upload.
  const blobStore = new DemoBlobStore(params.get('blobMax') ? { max: Number(params.get('blobMax')) } : {});
  blobStore.uploadDelayMs = Number(params.get('uploadDelay')) || 0;
  host.blobStore = blobStore;
  // ?timeout= shortens how long a request waits for its response, for the tests of resends.
  const timeoutMs = Number(params.get('timeout')) || 0;
  const client = new HostClient(host.connect(), { store, ...(timeoutMs ? { timeoutMs, answerTimeoutMs: timeoutMs, replyTimeoutMs: timeoutMs, stopTimeoutMs: timeoutMs, launchTimeoutMs: timeoutMs, transferTimeoutMs: timeoutMs } : {}) });
  currentClient = client;
  const hello = await client.hello();
  const current = () => ({ client, hostId: hello.machine.id });
  const retryMs = Number(params.get('retry')) || 3000;
  const answers = new AnswerService({ current, retryMs });
  const replies = new ReplyService({ current, retryMs, blobs: () => blobStore });
  const stops = new StopService({ current, retryMs });
  const launches = new LaunchService({ current, retryMs });
  const largeAttachments = () => largeLimit(hello.capabilities, blobStore.max);
  answering = () => ({ service: answers, replies, stops, hostId: hello.machine.id, capabilities: hello.capabilities, readOnly: hello.machine.readOnly, largeAttachments });
  $('machines').hidden = false;
  const drawMachine = () => renderMachines($('machine-list'), {
    presence: [{ id: hello.machine.id, name: hello.machine.name, kind: 'host' }],
    me: null,
    current: hello.machine.id,
    onSelect: () => {},
    status: () => machineStatus({ ...hello.machine, sessions: client.synced ? store.size : hello.machine.sessions, held: client.synced ? store.held?.reason : null }),
  });
  drawMachine();
  store.onChange(drawMachine);
  showSessionsFor(hello.machine.name);
  setHostState('');
  await client.subscribe();
  if (hello.capabilities.includes(USAGE.capability)) {
    client.on('usage', (r) => book.set(hello.machine.id, r));
    book.set(hello.machine.id, await client.subscribeUsage());
  }
  const launchPanel = new LaunchPanel($('launch'), { service: launches, jump: showSession });
  const transfers = hello.capabilities.includes(TRANSFER.capability) ? await demoTransfers(host, hello.machine.id) : null;
  // A new target object each time, for the same machine, as a presence update hands in.
  const target = () => ({ hostId: hello.machine.id, machineName: hello.machine.name, load: () => client.launchOptions(), transfers });
  if (hello.capabilities.includes(LAUNCH.capability)) { launchPanel.setTarget(target()); }
  const script = params.get('static') === '1' ? null : runScript(host, { tickMs: Number(params.get('tick')) || 4000 });
  window.agentBridgeDemo = {
    host, client, store, view, answers, replies, stops, launches, launchPanel, book, blobs: blobStore,
    // The launch panel told about the same machine again, as presence updates tell it.
    retarget: () => launchPanel.setTarget(target()),
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
  const hosts = new HostSession({ connection, store });
  const link = takeLink();
  hosts.restoreTarget(link?.host ?? null);
  currentHost = () => hosts.targetId;
  linked = link?.host && link.session ? { host: link.host, session: link.session } : null;
  followLink = ({ host, session }) => {
    linked = session ? { host, session } : null;
    hosts.select(host);
    focusLinked();
  };
  // Answers go to the machine they were given for, on whichever channel to it is open now:
  // after a reconnect, one still waiting for its outcome is resent with the same request.
  const current = () => (hosts.phase === 'open' && hosts.client ? { client: hosts.client, hostId: hosts.targetId } : null);
  const answers = new AnswerService({ current });
  // Large files go through the blob store of the relay this page is connected to, when it has
  // one and the machine fetches from it (sessions.attach.blob).
  const relayBlobs = () => (connection.relay ? new RelayBlobs(connection.relay) : null);
  const replies = new ReplyService({ current, blobs: relayBlobs });
  const stops = new StopService({ current });
  const launches = new LaunchService({ current });
  const launchPanel = new LaunchPanel($('launch'), { service: launches, jump: showSession });
  const largeAttachments = () => largeLimit(hosts.capabilities, connection.relay?.blobs?.max ?? 0);
  answering = () => (hosts.targetId ? { service: answers, replies, stops, hostId: hosts.targetId, capabilities: hosts.capabilities, readOnly: hosts.machine?.readOnly ?? null, largeAttachments } : null);
  let shownCapabilities = '';

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
  let registeredOn = null;
  const drawNotify = () => {
    const support = notices.support;
    const on = notices.enabled;
    const button = $('notify-toggle');
    const name = hosts.machine?.name ?? 'This machine';
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
      : hosts.phase === 'open' && !hosts.capabilities.includes(Capabilities.push)
        ? `On for this device, but ${name} doesn't send notifications yet.`
        : 'On for this device. The machines you open here notify it when a session asks you something or finishes.');
    $('notify-error').hidden = !notices.error;
    $('notify-error').textContent = notices.error;
  };
  notices.onChange(drawNotify);
  // The Settings app can change the permission while this is in the background.
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') { drawNotify(); } });
  $('notify-toggle').onclick = () => {
    const client = hosts.phase === 'open' ? hosts.client : null;
    // Not awaited before asking: enable() asks for permission first, inside this tap.
    const done = notices.enabled ? notices.disable(client) : notices.enable(client, hosts.capabilities);
    if (client) { registeredOn = client; }
    done.catch((e) => console.warn('notifications:', e.message));
  };
  const registerHere = () => {
    if (hosts.phase !== 'open' || !hosts.client || registeredOn === hosts.client) { return; }
    registeredOn = hosts.client;
    notices.register(hosts.client, hosts.capabilities).catch((e) => {
      notices.error = `Couldn't register for notifications from ${hosts.machine?.name ?? 'this machine'}: ${e.message}`;
      drawNotify();
    });
  };
  // A question answered here needs no notification; and one answered anywhere else must not
  // stay on the lock screen looking unanswered once this app has seen it go.
  answers.onAny((a) => {
    if (a.state === 'done' && ['delivered', 'unconfirmed', 'stale'].includes(a.outcome)) {
      worker().then((r) => closeNotices(r, a.hostId, a.sessionId)).catch(() => {});
    }
  });
  let staleTimer = null;
  store.onChange((c) => {
    if (c.type !== 'synced' && !hosts.client?.synced) { return; }
    clearTimeout(staleTimer);
    staleTimer = setTimeout(() => {
      if (hosts.phase === 'open' && hosts.client?.synced) { worker().then((r) => closeStaleNotices(r, hosts.targetId, store)).catch(() => {}); }
    }, 500);
  });

  const nameOf = (id) => connection.presence.find((d) => d.id === id)?.name;
  const fleet = new FleetStatus({ connection, hosts, store, book, me: identity.deviceId });
  const drawMachines = () => {
    const n = renderMachines($('machine-list'), {
      presence: connection.presence, me: identity.deviceId, current: hosts.targetId,
      onSelect: (id) => { hosts.select(id); $('sessions').focus(); },
      status: (id) => fleet.status(id),
    });
    $('machines-empty').hidden = n !== 0 || !connection.ready;
  };
  fleet.onChange(drawMachines);
  fleet.start();

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
    // The account's sessions and remembered machine go with it; a reconnect within the
    // same sign-in keeps them, but a new sign-in may be a different account.
    hosts.forget();
    fleet.clear();
    answers.clear();
    replies.clear();
    stops.clear();
    launches.clear();
    launchPanel.setTarget(null);
    currentClient = null;
    accountStore.clear();
    connection.stop();
    // This account's machines must not go on notifying this device.
    notices.forget().catch(() => {});
    showSignIn('The relay no longer accepts this sign-in. Sign in again.');
  });
  $('use-here').onclick = () => connection.start();

  hosts.onChange(() => {
    currentClient = hosts.client;
    const caps = `${hosts.targetId}:${hosts.capabilities.join(' ')}:${hosts.machine?.readOnly ?? ''}`;
    if (caps !== shownCapabilities) { shownCapabilities = caps; view.refreshQuestions(); }
    if (hosts.phase === 'open') { answers.ready(); replies.ready(); stops.ready(); launches.ready(); }
    registerHere();
    drawNotify();
    drawMachines();
    if (!hosts.targetId) { $('sessions').hidden = true; launchPanel.setTarget(null); return; }
    const name = hosts.machine?.name ?? nameOf(hosts.targetId) ?? 'your machine';
    showSessionsFor(name);
    // While reconnecting the panel stays, with any launch still waiting to be resent; a machine
    // that comes back without sessions.launch hides it.
    const launchable = hosts.phase === 'open' ? hosts.capabilities.includes(LAUNCH.capability) : launchPanel.hostId === hosts.targetId;
    const targetId = hosts.targetId;
    // The account's other machines' sessions, each signed for when picked: only where this one
    // takes them, and only what they hand on.
    const transfers = hosts.capabilities.includes(TRANSFER.capability) ? {
      remotes: () => collectRemote({ connection, targetId, me: identity.deviceId, makeClient: (ch) => new HostClient(ch) }),
      sign: (remote, session) => signTransfer({ key: identity.key, deviceId: identity.deviceId, machinePublic: remote.key, session, to: targetId }),
    } : null;
    launchPanel.setTarget(launchable ? { hostId: targetId, machineName: name, load: () => (hosts.client ? hosts.client.launchOptions() : Promise.reject(new Error('not connected'))), transfers } : null);
    switch (hosts.phase) {
      case 'waiting': return setHostState(hosts.error || (hosts.targetOnline ? `Connecting to ${name}…` : `${name} is offline. Its sessions will show when it is back.`));
      case 'opening': return setHostState(`Opening an end-to-end encrypted channel to ${name}…`);
      case 'open': return setHostState('');
      case 'unsupported': return setHostState(hosts.error);
      case 'failed': return setHostState(`Couldn't reach ${name}: ${hosts.error}. Trying again.`);
      default: return setHostState('');
    }
  });

  $('signout').onclick = async () => {
    hosts.forget();
    fleet.clear();
    answers.clear();
    replies.clear();
    stops.clear();
    launches.clear();
    accountStore.clear();
    connection.stop();
    // Not for long: signing out must not wait on a service worker that isn't running.
    await Promise.race([notices.forget().catch(() => {}), new Promise((r) => { setTimeout(r, 2000); })]);
    location.replace(location.pathname);
  };

  // For the E2E tests and debugging from the console. Holds no secrets beyond what the
  // page already has.
  window.agentBridge = { connection, hosts, store, answers, replies, stops, launches, notices, fleet, book };
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
