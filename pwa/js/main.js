// The PWA: boot and wiring. The pieces:
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
//   platform.js      the platform layer (#168): shortcut hints, safe areas, the edge swipe back,
//                    and which system face the page is in, for inline mono's x-height
//   demo.js          ?demo=1: simulated machines (?machines=2 or 3), no relay; loaded only then
//   brand.js         the product's name, for every place the app shows it
//   release-watch.js new releases of the app that keep failing to install
import { RELAY } from '../config.js';
import { BRAND, NOTIFICATIONS_BLOCKED } from './brand.js';
import { loadIdentity, deviceName, kvGet, kvPut, kvDelete } from './identity.js';
import { accountStore, startDeviceFlow } from './signin.js';
import { BridgeConnection } from './connection.js';
import { ALL, HostSession } from './host-session.js';
import { HostClient } from './host-client.js';
import { TRUST } from './launch.js';
import { TRANSFER, collectRemote, signTransfer, signTrust } from './transfer.js';
import { PushRegistrar, closeMachineNotices, closeNotices, closeStaleNotices, fetchVapidKey } from './push.js';
import { Capabilities } from './protocol.js';
import { offlineMachines } from './ui/views.js';
import { Shell } from './ui/shell.js';
import { SessionsPage } from './ui/sessions-page.js';
import { UsageBook } from './usage.js';
import { FleetStatus } from './fleet.js';
import { ForgetControl, CONFIRM_MS, vanished } from './forget.js';
import { UpdateControl } from './update.js';
import { ReleaseWatch } from './release-watch.js';
import { UsageView } from './ui/usage-view.js';
import { conceal, reveal, watchDisclosures, watchPresses } from './ui/motion.js';
import { RelayBlobs } from './blobs.js';
import { edgeSwipeBack, installedOnApple, markPlatform } from './platform.js';

// Installed on iOS or iPadOS, the edge swipe walks the same back stack as Android's Back.
if (installedOnApple(markPlatform())) { edgeSwipeBack(document, () => history.back()); }
const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);
const BASE = RELAY || location.origin;

// The pages, the app bar and the navigation between them.
const shell = new Shell();

// Every machine's latest usage report, drawn as one row per allowance on the usage page, and as
// one line under the sessions.
const book = new UsageBook();
new UsageView($('usage'), book, { line: $('usage-line') });

// The account's usage readings this device keeps between visits (#172): in IndexedDB beside its
// key, for one account. Another account's are never shown, and signing out deletes them.
const USAGE_KEPT = 'usage-kept';
let usageEpoch = 0;
function keepUsage(account) {
  const epoch = ++usageEpoch;
  const who = String(account?.accountId ?? account?.login ?? '');
  if (!who) { return; }
  kvGet(USAGE_KEPT).catch(() => null).then((saved) => {
    if (epoch !== usageEpoch) { return; }
    book.useCache({
      entries: saved?.account === who && Array.isArray(saved.entries) ? saved.entries : [],
      save: (entries) => { if (epoch === usageEpoch) { kvPut(USAGE_KEPT, { account: who, entries }).catch(() => {}); } },
    });
  });
}
function dropKeptUsage() {
  usageEpoch++;
  return kvDelete(USAGE_KEPT).catch(() => {});
}
// The sessions on screen and what their cards and the launch panel send, once begun.
let page = null;

// ---- links: a notification's ?host=&session=&question= ----
// The session a link asked for, until its card is on screen: { host, session, question }.
let linked = null;
// Set once signed in: goes to a link's machine.
let followLink = null;
// While a shown link's answers wait to take focus: stops watching for focus moved meanwhile.
let unwatchFocus = () => {};

// What a link asked for: another, or none. Whatever watched the last one's focus goes with it.
function setLinked(next) {
  unwatchFocus();
  linked = next;
}

// Any focus landing anywhere but where showing the session left it, and any press or key at
// all, while its answers wait for the machine, gives the wait up: the person has moved on. A
// click on something that takes no focus leaves focus on the page itself with no focusin, and
// a redrawn panel drops it there too, so where focus is once the machine is current says
// nothing; that the person did something meanwhile does. Focus coming back to the page as a
// whole (another app, then this one again) is neither.
function watchFocus(want) {
  const off = () => { if (linked === want) { setLinked(null); } };
  const moved = (e) => { if (e.target !== want.left && e.target !== document && e.target !== document.body) { off(); } };
  document.addEventListener('focusin', moved, true);
  document.addEventListener('pointerdown', off, true);
  document.addEventListener('keydown', off, true);
  unwatchFocus = () => {
    document.removeEventListener('focusin', moved, true);
    document.removeEventListener('pointerdown', off, true);
    document.removeEventListener('keydown', off, true);
    unwatchFocus = () => {};
  };
}

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
// machine, shown alone or with every other. Its answers take focus only once that machine
// takes them (#269: not while its list is still being confirmed), and only while the session
// is still the one shown.
// Focus goes to an answer only for the question the notification was about, and only if the
// person has not moved focus since: a tap choice sends on Enter or Space, and while the machine
// was held the question could have been answered at the machine and another asked, which a
// focus moved later would have them approve unseen (#269 security review).
function focusLinked() {
  if (!linked || !page?.hosts.shows(linked.host)) { return; }
  const card = page.view.card(linked.host, linked.session);
  if (!card) { return; }
  if (!linked.shown) {
    // Taken while the session is shown: showing it closes a search that hid it, which redraws
    // the list, which comes back here.
    const want = { ...linked, shown: true };
    setLinked(null);
    page.showSession(card.id, card.hostId);
    want.left = document.activeElement;
    setLinked(want);
    if (!page.hosts.route(card.hostId)) { watchFocus(want); return; }
  } else if (page.view.selected !== card) {
    setLinked(null);
    return;
  }
  if (!page.hosts.route(card.hostId)) { return; }
  const { question, left } = linked;
  setLinked(null);
  // Body only as where nothing has been focused since: a move would have given this up.
  const now = document.activeElement;
  if (now && now !== document.body && now !== left) { return; }
  if (!question || card.session?.question?.id !== question) { return; }
  // The question's answer controls, not a code block's Copy button in its message: Enter on
  // a notification's question must answer it, not copy the command it shows.
  const el = card.view;
  const target = el.querySelector('.question-slot button:not([disabled]):not(.md-copy), .question-slot input:not([disabled]), .question-slot textarea:not([disabled])');
  target?.focus({ preventScroll: true });
  // Brought into sight as Needs you brings a question, so its answers wait before a click counts.
  if (target) { card.revealQuestion('auto', target); }
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
  shell.setConnection(text, cls);
}

function setHostState(text) {
  if (page) { page.setHostState(text); return; }
  $('host-state').textContent = text;
  $('host-state').hidden = !text;
}

// ---- service worker and updates ----
// A status banner appearing (ui.md, PWA: app banners): its words go to a live region already on
// the page, which a region unhidden with them already in it is not reliably read out as.
function showStatus(banner) {
  if (!banner.hidden) { return; }
  reveal(banner);
  $('banner-said').textContent = banner.querySelector('span').textContent;
}

// And going: nothing left in the live region for a screen reader to come across once it has gone.
function hideStatus(banner) {
  if (banner.hidden) { return; }
  conceal(banner);
  const said = $('banner-said');
  if (said.textContent === banner.querySelector('span').textContent) { said.textContent = ''; }
}

// An installed PWA keeps running the code it has until restarted (S5 limitation), so a
// new version is announced with a reload button instead of waiting for a force-quit.
function registerServiceWorker() {
  if (!('serviceWorker' in navigator)) { return; }
  const hadController = !!navigator.serviceWorker.controller;
  // Only an app running a release it has can be stuck on it: without one, each load is the network's.
  const releases = new ReleaseWatch({
    onChange: (stuck) => {
      if (stuck && hadController) { showStatus($('stuck-banner')); } else { hideStatus($('stuck-banner')); }
    },
  });
  // Which release is in control, read again after each takeover; only the latest read counts.
  let reading = 0;
  const learn = () => {
    const mine = ++reading;
    releaseInControl().then((release) => { if (mine === reading) { releases.under(release); } });
  };
  learn();
  navigator.serviceWorker.register('sw.js').then((reg) => {
    // Watched from the start, while the release in control is still being read: an install
    // that fails meanwhile is counted, and kept once the release is known.
    reg.addEventListener('updatefound', () => releases.watch(reg.installing));
    // One the browser found on its own as this page loaded, before it could listen.
    releases.watch(reg.installing);
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') { reg.update().catch(() => {}); }
    });
  }).catch((e) => console.warn('service worker:', e.message));
  navigator.serviceWorker.addEventListener('controllerchange', () => {
    releases.tookOver();
    learn();
    if (hadController) { showStatus($('update-banner')); }
  });
  $('reload').onclick = () => location.reload();
  // A load is the browser's next try.
  $('retry-release').onclick = () => location.reload();
}

// The release in control: the worker's record of it (sw.js, releases.json), which each release
// writes as it activates; so a worker still activating (one taking over) is waited for. null
// with no worker, from one that keeps no record, or from a cache that can't be read.
async function releaseInControl() {
  const worker = navigator.serviceWorker.controller;
  if (!worker) { return null; }
  try {
    if (worker.state === 'activating') {
      await new Promise((resolve) => worker.addEventListener('statechange', resolve, { once: true }));
    }
    const record = await caches.match(new URL('releases.json', new URL('./', worker.scriptURL)).href);
    return (await record?.json())?.active ?? null;
  } catch {
    return null;
  }
}
// ---- demo ----
// The demo's own remembered choices, apart from the live app's on the same site.
const demoStorage = {
  getItem: (k) => localStorage.getItem(`demo:${k}`),
  setItem: (k, v) => localStorage.setItem(`demo:${k}`, v),
  removeItem: (k) => localStorage.removeItem(`demo:${k}`),
};

async function startDemo() {
  // Only the demo loads its pretend hosts: the live app never fetches or parses them (#609).
  const { DemoBlobStore, DemoConnection, DemoHost, demoDelegating, demoMachines, demoTransfers, demoTrust, runScript } = await import('./demo.js');
  $('demo-banner').hidden = false;
  $('demo-settings').hidden = false;
  shell.setSignedIn(true);
  // The demo banner says so already: on a phone the app bar stays out of the way.
  setPill('Demo', 'demo');
  const fixture = await (await fetch('fixtures/sessions-list.json', { cache: 'no-store' })).json();
  // ?delegates=1: a session with delegates at work, the main agent's words apart from theirs (#176).
  if (params.get('delegates') === '1') { fixture.sessions.push(demoDelegating()); }
  // ?ahb=1: a machine where agent-ha-bridge runs, so it takes nothing from the phone and says why.
  const ahb = params.get('ahb') === '1';
  const answerDelayMs = Number(params.get('answerDelay') ?? 700);
  const stopWindowMs = Number(params.get('stopWindow')) || 10000;
  const host = new DemoHost(fixture, {
    // ?name=: the demo machine's name, for a long one (#214).
    ...(params.get('name') ? { machineName: params.get('name').slice(0, 120) } : {}),
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
    // ?trust=0: a host from before folder-trust questions were answered from devices.
    trusting: params.get('trust') !== '0',
    // ?updates=1: a host whose app installs updates asked for from here (#51). Never a
    // read-only one, which offers none.
    updating: params.get('updates') === '1' && !ahb,
  });
  host.updateStepMs = Number(params.get('updateStep')) || host.updateStepMs;
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
  // ?keep=1: usage readings kept between visits (#172), in the demo's own storage.
  if (params.get('keep') === '1') {
    let kept = null;
    try { kept = JSON.parse(demoStorage.getItem(USAGE_KEPT) ?? 'null'); } catch { /* none kept */ }
    book.useCache({
      entries: Array.isArray(kept?.entries) ? kept.entries : [],
      save: (entries) => { try { demoStorage.setItem(USAGE_KEPT, JSON.stringify({ account: 'demo', entries })); } catch { /* a nicety */ } },
    });
  }
  const demoUpdates = new UpdateControl({ fleet });
  const transfers = new Map();
  for (const m of machines) {
    if (m.capabilities.includes(TRANSFER.capability)) { transfers.set(m.machine.id, await demoTransfers(m, m.machine.id)); }
  }
  // Folder-trust questions answered from here, each with a command signed for that demo machine.
  const trusts = new Map();
  for (const m of machines) {
    if (m.capabilities.includes(TRUST.capability)) { trusts.set(m.machine.id, await demoTrust(m, () => hosts.route(m.machine.id)?.client ?? null)); }
  }
  page = new SessionsPage({
    $, hosts, connection, shell,
    blobs: () => blobStore, relayMax: () => blobStore.max,
    retryMs: Number(params.get('retry')) || 3000,
    transfers: (id) => transfers.get(id) ?? null,
    trust: (id) => trusts.get(id) ?? null,
    status: (id) => fleet.status(id),
    facts: (id) => fleet.facts(id),
    forget: demoForget,
    updates: demoUpdates,
    storage: demoStorage,
    onRendered: () => focusLinked(),
  });
  demoForget.onChange(() => page.drawMachines());
  demoUpdates.onChange(() => page.drawMachines());
  fleet.onChange(() => page.drawMachines());
  $('machines').hidden = false;
  hosts.restoreTarget(page.routedHost ?? null);
  fleet.start();
  // Ready once every machine shown has sent what it has. One remembered from a demo with more
  // machines is not there to send anything, and says it is offline.
  await new Promise((resolve) => {
    const check = () => { if (hosts.links().every((l) => !hosts.isOnline(l.id) || (l.phase === 'open' && l.client?.synced))) { resolve(); } };
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
    // This machine's launch target as the page makes it, for a test to tell the panel about it with a change (read-only).
    target: () => page.launchTarget(host.machine.id),
    // A new usage report from the host, sent as usage.update.
    usage: (report) => host.setUsage(report),
    // The next answers' outcomes, in order: 'delivered' (the default once these run out),
    // 'unconfirmed', 'stale', 'busy', 'refused', an unknown one, or { outcome, reason,
    // drop: true } to lose the response the first time, as a dropped channel would, or { error }
    // for the host to fail the op itself.
    script: (...outcomes) => host.scriptAnswers(outcomes),
    // The same, for the next replies.
    scriptReplies: (...outcomes) => host.scriptReplies(outcomes),
    // The same, for the next stops; none scripted, the host decides as a real one would.
    scriptStops: (...outcomes) => host.scriptStops(outcomes),
    // The same, for the next launches: 'started' (the default), 'unconfirmed', 'busy', 'refused',
    // 'failed', an unknown one, or { outcome, reason, drop: true }.
    scriptLaunches: (...outcomes) => host.scriptLaunches(outcomes),
    // The next update presses' refusals, by reason; none scripted, a press is taken (#51).
    scriptUpdates: (...reasons) => host.scriptUpdates(reasons),
    // What the host's app reports now, sent as update.state.
    setUpdate: (state) => host.setUpdate(state),
    get updateLog() { return [...host.updateLog]; },
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
    // A launched agent asking whether to trust its folder, and the next answers' outcomes.
    askTrust: (question) => host.askTrust(question),
    scriptTrust: (...outcomes) => host.scriptTrust(outcomes),
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
  shell.setSignedIn(false);
  $('signin').hidden = false;
  $('sessions').hidden = true;
  $('account').hidden = true;
  $('notify').hidden = true;
  $('signin-error').hidden = !error;
  $('signin-error').textContent = error ?? '';
  setPill('Signed out');
}

let signInAttempt;
function clearSignInAttempt() {
  signInAttempt = null;
  $('cancel-signin').hidden = true;
  $('signin-button').disabled = false;
  $('code-box').hidden = true;
  $('code').textContent = '';
  $('verify').removeAttribute('href');
}

$('cancel-signin').onclick = () => {
  signInAttempt?.abort();
  clearSignInAttempt();
  $('signin-button').focus();
};

$('signin-button').onclick = async () => {
  if (signInAttempt) { return; }
  const attempt = new AbortController();
  signInAttempt = attempt;
  $('signin-button').disabled = true;
  $('cancel-signin').hidden = false;
  $('signin-error').hidden = true;
  try {
    const flow = await startDeviceFlow(BASE, attempt.signal);
    attempt.signal.throwIfAborted();
    $('code').textContent = flow.userCode;
    $('verify').href = flow.verificationUri;
    $('verify').textContent = flow.verificationUri.replace(/^https?:\/\//, '');
    $('code-box').hidden = false;
    const account = await flow.wait(attempt.signal);
    attempt.signal.throwIfAborted();
    accountStore.save(account);
    $('signin').hidden = true;
    begin(account);
  } catch (e) {
    if (attempt.signal.aborted) { return; }
    $('signin-error').hidden = false;
    $('signin-error').textContent = e.message;
  } finally {
    // An old fetch can finish after cancellation and after a new code is displayed.
    if (signInAttempt === attempt) { clearSignInAttempt(); }
  }
};

function begin(account) {
  shell.setSignedIn(true);
  shell.setAccount(account.login ?? '');
  $('account').hidden = false;
  $('login').textContent = account.login ?? '';
  $('me').textContent = identity.deviceId.slice(0, 8);
  $('machines').hidden = false;

  const connection = new BridgeConnection({ base: BASE, key: identity.key, deviceId: identity.deviceId, name: deviceName(), account });
  const hosts = new HostSession({ connection, me: identity.deviceId });
  const link = takeLink();
  setLinked(link?.host && link.session ? { host: link.host, session: link.session, question: link.question } : null);
  // A link's machine is shown: with every other when every machine is, else on its own. A
  // notification is not a choice in the Machines panel, so what is remembered stays.
  followLink = ({ host, session, question }) => {
    setLinked(session ? { host, session, question } : null);
    if (!hosts.all || !hosts.isOnline(host)) { hosts.visit(host); }
    focusLinked();
  };
  const fleet = new FleetStatus({ connection, hosts, book, me: identity.deviceId });
  keepUsage(account);
  // Forgetting an offline machine (#50): what this app keeps about it goes with it. Its push
  // subscription is on that machine, which is gone; the notices it left here are closed.
  // Each machine's updates (#51): Check and Install, from a person's device, which this is.
  const updates = new UpdateControl({ fleet });
  const dropMachine = (id) => {
    hosts.dropMachine(id);
    fleet.forget(id);
    updates.forget(id);
    if (hosts.targetId === id) { hosts.select(ALL); }
    worker().then((r) => closeMachineNotices(r, id)).catch(() => {});
  };
  const forgets = new ForgetControl({ connection, onForgotten: dropMachine });
  // Answers, replies, stops and launches go to the machine they were given for, on whichever
  // channel to it is open now: after a reconnect, one still waiting for its outcome is resent
  // with the same request. Large files go through the blob store of the relay this page is
  // connected to, when it has one and the machine fetches from it (sessions.attach.blob).
  page = new SessionsPage({
    $, hosts, connection, me: identity.deviceId, shell,
    blobs: () => (connection.relay ? new RelayBlobs(connection.relay) : null),
    relayMax: () => connection.relay?.blobs?.max ?? 0,
    // The account's other machines' sessions, each signed for when picked.
    transfers: (targetId) => ({
      remotes: () => collectRemote({ connection, targetId, me: identity.deviceId, makeClient: (ch) => new HostClient(ch) }),
      sign: (remote, session) => signTransfer({ key: identity.key, deviceId: identity.deviceId, machinePublic: remote.key, session, to: targetId }),
    }),    // Folder-trust questions answered from here, each with a command this device signs for the
    // machine on the other end of its channel, whose key the handshake proved, and sent there.
    trust: (targetId) => {
      const name = () => hosts.nameOf(targetId) ?? 'that machine';
      return {
        sign: (question, choice) => {
          const key = hosts.route(targetId) ? hosts.link(targetId)?.channel?.hs?.remoteStatic : null;
          if (!key) { return Promise.reject(new Error(`not connected to ${name()}`)); }
          return signTrust({ key: identity.key, deviceId: identity.deviceId, machinePublic: new Uint8Array(key), question, choice });
        },
        answer: (data) => {
          const client = hosts.route(targetId)?.client;
          return client ? client.answerTrust(data) : Promise.reject(new Error(`not connected to ${name()}`));
        },
      };
    },
    status: (id) => fleet.status(id),
    facts: (id) => fleet.facts(id),
    forget: forgets,
    updates,
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
      install: `On iPhone and iPad, notifications need ${BRAND.name} on the Home Screen: tap Share, then Add to Home Screen, and open it from there.`,
      unsupported: `This browser can't show notifications from ${BRAND.name}.`,
      denied: `${NOTIFICATIONS_BLOCKED} Allow them in Settings to turn them on.`,
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
  // Each machine registers on its own, so one that fails says so and holds up none of the others.
  const registerOn = (l) => {
    registered.add(l.client);
    notices.register(l.client, l.capabilities).catch((e) => {
      notices.error = `Couldn't register for notifications from ${nameOf(l)}: ${e.message}`;
      drawNotify();
    });
  };
  $('notify-toggle').onclick = () => {
    const open = openLinks();
    if (notices.enabled) {
      notices.disable(open[0]?.client ?? null).catch((e) => console.warn('notifications:', e.message));
      return;
    }
    // Not awaited before asking: enable() asks for permission first, inside this tap. Each
    // machine's registration is queued behind it, and goes only once notifications are on.
    notices.enable().catch((e) => console.warn('notifications:', e.message));
    for (const l of open) { registerOn(l); }
  };
  const registerHere = () => {
    for (const l of openLinks()) { if (!registered.has(l.client)) { registerOn(l); } }
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
      const synced = openLinks().filter((l) => l.client.synced).map((l) => ({ l, client: l.client }));
      // Each list must still be its machine's when the notices are looked at: one dropped
      // meanwhile (another machine picked) is empty, and would close every question it has.
      const still = ({ l, client }) => () => hosts.link(l.id) === l && l.client === client && client.synced;
      if (synced.length) { worker().then((r) => Promise.all(synced.map((x) => closeStaleNotices(r, x.l.id, x.l.store, still(x))))).catch(() => {}); }
    }, 500);
  });

  // The same for a machine forgotten from another of the account's devices: it leaves the
  // offline list without coming online.
  let lastKnown = null;
  const offlineNow = () => offlineMachines({ presence: connection.presence, known: connection.known, me: identity.deviceId });
  fleet.onChange(() => page.drawMachines());
  forgets.onChange(() => page.drawMachines());
  updates.onChange(() => page.drawMachines());
  hosts.restoreTarget(link?.host ?? page.routedHost ?? null);
  fleet.start();
  // "last seen 5m ago" moves on with nothing else to redraw the list: every machine may be offline.
  // So does "checked 5m ago" under a machine's updates.
  setInterval(() => {
    if (document.visibilityState !== 'hidden' && (connection.known?.length || connection.presence.some((d) => fleet.update(d.id)))) { page.drawMachines(); }
  }, 30000);

  connection.on('presence', () => {
    const now = connection.known ? offlineNow() : null;
    for (const id of vanished(lastKnown, now, connection.presence.map((d) => d.id))) { dropMachine(id); }
    lastKnown = now;
    // The account's machines, online or not, in one reading: usage read by any other was read by a
    // machine forgotten, perhaps while this page was closed (#172). None where the relay keeps no roster.
    book.roster(connection.known ? [...connection.presence.map((d) => d.id), ...connection.known.map((k) => k.id)] : null);
    // A machine that comes back takes its arm, and any refusal shown for it, with it.
    forgets.reconcile(now ?? []);
  });
  connection.on('presence', () => page.drawMachines());
  connection.on('log', (line) => console.log(line));
  connection.on('state', ({ state }) => {
    if (state === 'replaced') { reveal($('replaced-banner')); } else { conceal($('replaced-banner')); }
    switch (state) {
      // News, as reconnecting is: the strip shows on a phone, in plain words.
  case 'connecting': return setPill('Connecting…', 'busy');
      case 'connected': page.refresh(); return setPill('Connected', 'ok');
      // A shown link still waiting to focus an answer is given up when the relay drops: the
      // machine's list will be another by the time it is back.
      case 'reconnecting': if (linked?.shown) { setLinked(null); } page.refresh(); return setPill('Reconnecting…', 'warn');
      case 'replaced': if (linked?.shown) { setLinked(null); } return setPill('Paused', 'warn');
      case 'signed-out': return undefined;
      default: return setPill(state);
    }
  });
  connection.on('signed-out', () => {
    // The account's sessions and remembered machine go with it; a reconnect within the
    // same sign-in keeps them, but a new sign-in may be a different account.
    setLinked(null);
    hosts.forget();
    fleet.clear();
    forgets.clear();
    updates.clear();
    lastKnown = null;
    page.clear();
    // A session's address names this account's machine: the next sign-in, perhaps another
    // account, would otherwise start on it. A link that arrives after this is kept.
    if (shell.route === 'sessions' && shell.rest.length) { shell.go('sessions', { replace: true }); }
    accountStore.clear();
    dropKeptUsage();
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
    setLinked(null);
    hosts.forget();
    fleet.clear();
    page.clear();
    accountStore.clear();
    connection.stop();
    // Not for long: signing out must not wait on a service worker that isn't running.
    await Promise.race([Promise.all([notices.forget().catch(() => {}), dropKeptUsage()]), new Promise((r) => { setTimeout(r, 2000); })]);
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
watchDisclosures(document);
watchPresses(document);
(params.get('demo') === '1' ? startDemo() : startLive()).catch((e) => {
  console.error(e);
  setPill('Error', 'bad');
  shell.setSignedIn(true);
  // Said where it can be seen: on Sessions, whatever page the address named.
  shell.go('sessions', { replace: true });
  setHostState(e.message);
  $('sessions').hidden = false;
});
