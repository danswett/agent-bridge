// Demo mode (?demo=1): the app with no relay and no machine. A host is simulated in the
// page, speaking the real protocol to the real HostClient, so everything from event
// dispatch to the cards is the code a live host drives. Its sessions come from the
// protocol's worked example (fixtures/sessions-list.json, a copy of
// docs/protocol/examples/sessions-list.json).
//
// window.agentBridgeDemo (demo mode only) lets tests and curious people drive it:
//   upsert(session), remove(id), frame(rawFrame), calls (ops received), pause(),
//   script(...outcomes) for the next answers, scriptReplies(...outcomes) for the next replies,
//   host.attachments and host.attachmentLog for the files sent ahead of replies, blobs (the
//   relay's blob store and storage, in the page) for the large ones,
//   scriptStops(...outcomes) for the next presses of End session,
//   scriptLaunches(...outcomes) for the next launches, host.launchOptions for what it offers,
//   askTrust(question) for a launched agent asking whether to trust its folder, scriptTrust(...)
//   for the next answers to one, host.trustLog for every launch.trust.answer received,
//   host.holdResponses() and host.releaseResponses() to keep responses back until a test has
//   looked, and host.holdSnapshot() and host.releaseSnapshot() to hold and release the end of
//   a snapshot (sessions.synced)
//
// It answers questions as a host would: the same validation (answers.js mirrors the C#
// Answers.Check), stale when the session is no longer asking, a request seen before gets
// its first outcome again, and an answer that is typed takes the question away with an
// upsert. Replies likewise (replies.js mirrors Replies.Check): refused while the session
// asks a question and for agents a host can't reply to yet (only Copilot), and a typed one
// shows as the session's latest message, with the session at work on it. End session as a
// host ends one: a session that is not idle takes a second press carrying the token the first
// was given, then shows as ending and goes. A launch as a host launches one (launch.js mirrors
// LaunchProtocol.Check): refused for what this machine does not offer, then started, with the new
// session upserted. Outcomes are delivered unless a test or curious person scripts others. A
// second machine, Studio PC, offers two ended sessions to bring over (transfer.js): the launch
// carries a command this page signs with a key of its own for Studio PC's, which the demo host
// reads as the target would and Studio PC would check.
//
// ?machines=2 adds a second machine to the account, Laptop, with sessions of its own;
// ?machines=3 adds a third, DEV-VM1 (shadow), which watches only, as a machine where
// agent-ha-bridge still runs does. Each is its own DemoHost behind DemoConnection, the relay's
// presence and channels in the page, so the app shows them as it shows real ones: one at a
// time, or every machine at once.
import { checkAnswer } from './answers.js';
import { checkReply, typedForm } from './replies.js';
import { readQuestion } from './protocol.js';
import { USAGE } from './usage.js';
import { UPDATE } from './update.js';
import { LAUNCH, TRUST, checkLaunch, flattenPrompt, trustAction } from './launch.js';
import { TRANSFER, signEscapedApproval, signTransfer, signTrust } from './transfer.js';
import { ESCAPED_CAPABILITY } from './escaped.js';
import { P256KeyPair } from './noise-p256.js';
import { deviceIdOf } from './identity.js';
import { fromUtf8 } from './bytes.js';
import { blobIdOf, fromBase64url, openBlob, storedSize, toBase64url } from './blobs.js';
import { SIGN_IN } from './auth-state.js';

const BLOB_FILE_MAX = 100 * 1024 * 1024;

const clone = (v) => JSON.parse(JSON.stringify(v));
const now = () => new Date().toISOString();

// The transport HostClient expects, connected straight to a DemoHost.
class DemoTransport {
  onmessage = null;
  onclose = null;
  // Its own seq, as each channel to a real host numbers its events from 1.
  seq = 0;
  constructor(host) { this.host = host; }
  send(message) {
    // Asynchronous, like a real channel, so ordering bugs show up here too.
    setTimeout(() => this.host.receive(this, message), 0);
    return Promise.resolve();
  }
  deliver(message) { setTimeout(() => this.onmessage?.(clone(message)), 0); }
  close() { this.host.transports.delete(this); }
}

// The relay connection, in the page: presence lists the demo's machines, and a channel to one
// is a DemoTransport to its DemoHost. Tests take a machine offline (setOnline) or make its
// channels fail to open (DemoHost.failOpen), as a machine that can't be reached would.
export class DemoConnection {
  ready = true;
  #listeners = new Map();

  constructor(hosts, { known = null } = {}) {
    this.hosts = new Map(hosts.map((h) => [h.machine.id, h]));
    this.presence = hosts.map((h) => ({ id: h.machine.id, name: h.machine.name, kind: 'host' }));
    this.known = known;
  }

  on(type, fn) {
    if (!this.#listeners.has(type)) { this.#listeners.set(type, new Set()); }
    this.#listeners.get(type).add(fn);
    return () => this.#listeners.get(type)?.delete(fn);
  }

  emit(type, detail) {
    for (const fn of this.#listeners.get(type) ?? []) {
      try { fn(detail); } catch (e) { console.error(e); }
    }
  }

  async openChannel(id) {
    const host = this.hosts.get(id);
    await new Promise((r) => { setTimeout(r, 0); });
    if (!host || !this.presence.some((d) => d.id === id)) { throw new Error('the machine is offline'); }
    if (host.failOpen) { throw new Error(host.failOpen); }
    const t = host.connect();
    t.peerName = host.machine.name;
    return t;
  }

  // A machine leaves presence (its channels end with it) or comes back.
  setOnline(id, online) {
    const host = this.hosts.get(id);
    if (!host) { return; }
    const listed = this.presence.some((d) => d.id === id);
    if (online && !listed) {
      const order = [...this.hosts.keys()];
      this.presence = [...this.presence, { id, name: host.machine.name, kind: 'host' }].sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
    } else if (!online && listed) {
      this.presence = this.presence.filter((d) => d.id !== id);
      host.disconnect();
    } else { return; }
    this.emit('presence', this.presence);
  }
}

export class DemoHost {
  transports = new Set();
  calls = [];
  // Every session.get request's data, for a test to see what was asked (a delegate by agent).
  getLog = [];
  // What each delegate said in full, by its id, for session.get with agent; a test changes it.
  delegateWords = clone(DELEGATE_WORDS);
  failDelegateGets = 0;
  revision = 0;

  // Each session.answer request: its outcome, by request UUID, as a host keeps them.
  answers = new Map();
  // Every session.answer request as received, resends included.
  answerLog = [];
  // What was typed into a session, once per answer: what a resend must not repeat.
  typed = [];
  #script = [];
  onTyped = null;
  // session.reply, the same way: outcomes by request, every request, what was typed.
  replies = new Map();
  replyLog = [];
  typedReplies = [];
  #replyScript = [];
  onReplied = null;
  // Files sent ahead of replies (attachment.*), by id, as a host holds them; and every
  // attachment op as received.
  attachments = new Map();
  attachmentLog = [];
  // session.stop: outcomes by request, every request, and the first presses waiting for their
  // second (by session: tokens and when they were given).
  stops = new Map();
  stopLog = [];
  #stopScript = [];
  #responsesHeld = null;
  #letResponsesGo = null;
  #snapshotHeld = null;
  #letSnapshotGo = null;
  // Holds before a delivery types or before its outcome goes (holdBefore), by step.
  #heldBefore = new Map();
  // How long after typing an answer or a reply its outcome goes, as a host waits for the
  // session to record it: long enough, in the demo (?confirmDelay=), to see Typed (#600).
  confirmDelayMs = 0;
  #arms = new Map();
  // session.launch: outcomes by request, and every request as received.
  launches = new Map();
  launchLog = [];
  #launchScript = [];
  launchOptions = demoLaunchOptions();
  // The other machine's sessions, as it offers them for a transfer.
  remote = { hostId: 'demo-studio', machineName: 'Studio PC', resumable: demoRemoteResumable() };
  // How long Studio PC takes to say what it offers, for a test of a slow machine.
  remoteDelayMs = 0;
  // launch.trust.answer: outcomes by request, every request as received, and the next outcomes.
  trustAnswers = new Map();
  trustLog = [];
  #trustScript = [];
  // This machine's static key, which a folder-trust answer is signed for.
  key = null;
  // update.* (#51): what its app reports, every press as received, the next refusals, and how
  // long each step of an update takes here.
  update = null;
  updateLog = [];
  #updateScript = [];
  updateStepMs = 400;
  // Sign-in (#800): every signin.start and signin.cancel as received; the next starts' failures
  // (their errors) and outcomes ({ outcome, reason } in place of started); the code the next flow
  // shows, when a test wants another (a spoofed one, say); and the flow under way, with the
  // channels in it, which alone are sent its code.
  signInLog = [];
  signInFailures = [];
  signInOutcomes = [];
  // How long the next starts' replies wait, in ms: a result that comes back after a Cancel.
  signInDelays = [];
  // How long the next cancels take to finish, in ms: "cancelled" is logged once each has.
  signInCancelDelays = [];
  nextCode = null;
  signInFlow = null;
  #flows = 0;

  // answering: false plays a host that can't answer (agent-ha-bridge still running, say);
  // replying: false, one that can't reply; attaching: false, one that takes no files; large:
  // false, one that takes no files through the blob store;
  // stopping: false, one that can't end sessions; approving: false, one with approving
  // Copilot's permission prompts turned off. stopWindowMs is how long a first press of
  // a busy session waits for its second. usage: false plays a host from before usage.get;
  // launching: false, one that can't launch sessions; transferring: false, one that takes no
  // sessions from other machines. readOnly: why it takes nothing from the phone, as hello says.
  // updating: true plays a host whose app installs updates asked for from the phone (#51).
  // signingIn: true, one that runs an agent's own device-code sign-in when a person asks (#800).
  // progress: false plays a host from before sessions.progress, which says nothing of an answer
  // or a reply until its outcome.
  // confirming: false, one that does not take an escaped approval's confirmation from this device (#803).
  constructor(fixture, { readOnly = null, machineId = 'demo', machineName = 'Demo machine', answerDelayMs = 700, answering = true, replying = answering, attaching = replying, large = attaching, stopping = answering, approving = answering, confirming = approving, stopWindowMs = 10000, usage = true, launching = answering, transferring = launching, trusting = launching, updating = false, signingIn = false, progress = true } = {}) {
    this.machine = { id: machineId, name: machineName, os: 'windows', hostVersion: '0.1.0', dev: true, ...(readOnly ? { readOnly } : {}) };
    this.usage = demoUsage();
    this.answerDelayMs = answerDelayMs;
    this.stopWindowMs = stopWindowMs;
    this.capabilities = ['sessions.read', ...(answering ? ['sessions.answer'] : []), ...(answering && approving ? ['sessions.approve'] : []), ...(answering && confirming ? [ESCAPED_CAPABILITY] : []), ...(replying ? ['sessions.reply'] : []), ...(replying && attaching ? ['sessions.attach'] : []), ...(replying && attaching && large ? ['sessions.attach.blob'] : []), ...(stopping ? ['sessions.stop'] : []), ...((answering || replying) && progress ? ['sessions.progress'] : []), ...(usage ? [USAGE.capability] : []), ...(launching ? [LAUNCH.capability] : []), ...(launching && transferring ? [TRANSFER.capability] : []), ...(launching && trusting ? [TRUST.capability] : []), ...(updating ? [UPDATE.capability] : []), ...(signingIn && answering ? [SIGN_IN.capability] : [])];
    if (updating) {
      this.update = { current: '0.1.0', installer: true, available: '0.2.0', notes: 'Updates from your phone', checkedAt: now(), phase: 'idle', automatic: false };
    }
    this.sessions = new Map();
    for (const s of fixture.sessions ?? []) { this.sessions.set(s.id, s); }
    this.revision = fixture.revision ?? 1;
  }

  connect() {
    const t = new DemoTransport(this);
    this.transports.add(t);
    return t;
  }

  // Every channel to this machine ends, as when it goes offline or restarts.
  disconnect() {
    for (const t of [...this.transports]) {
      this.transports.delete(t);
      setTimeout(() => t.onclose?.(new Error('the host closed the channel')), 0);
    }
  }

  receive(t, { id, op, data }) {
    this.calls.push(op);
    const reply = (result) => t.deliver({ re: id, ok: true, result });
    switch (op) {
      case 'hello': {
        const signIn = this.#rollUp();
        return reply({ protocol: 1, machine: { ...this.machine, sessions: this.sessions.size, ...(signIn.length ? { signIn } : {}) }, capabilities: this.capabilities });
      }
      case USAGE.get:
        if (!this.capabilities.includes(USAGE.capability)) { break; }
        return reply(this.usage);
      case USAGE.subscribe:
        if (!this.capabilities.includes(USAGE.capability)) { break; }
        t.usageSubscribed = true;
        return reply(this.usage);
      case 'sessions.list':
        return reply({ sessions: [...this.sessions.values()].map(withoutHistory), revision: this.revision });
      case 'session.get': {
        this.getLog.push(clone(data ?? {}));
        const s = this.sessions.get(data?.id);
        // With agent: that one delegate in full, as a host since sub-agents answers. After the
        // answer delay and any hold, so a test can change the delegate while one is on its way.
        // failDelegateGets: that many fail first, as during a reconnect.
        if (s && typeof data?.agent === 'string') {
          if (this.failDelegateGets > 0) {
            this.failDelegateGets--;
            t.deliver({ re: id, ok: false, error: 'the machine is reconnecting' });
            return undefined;
          }
          this.#after(() => delegateDetail(this.sessions.get(data.id) ?? s, data.agent, this.delegateWords)).then(reply);
          return undefined;
        }
        return reply(s ? withHistory(s) : { missing: true });
      }
      case 'sessions.subscribe': {
        t.subscribed = true;
        reply({ revision: this.revision, count: this.sessions.size });
        for (const s of this.sessions.values()) { this.#event(t, 'session.upsert', withoutHistory(s)); }
        const synced = () => this.#event(t, 'sessions.synced', { revision: this.revision, ...(this.held ? { discoveryHeld: this.held } : {}) });
        if (this.#snapshotHeld) { this.#snapshotHeld.then(() => { if (this.transports.has(t)) { synced(); } }); } else { synced(); }
        return undefined;
      }
      case 'session.answer':
        return this.#answer(data, reply, (error) => t.deliver({ re: id, ok: false, error }), this.#progressTo(t, id, data));
      case 'session.reply':
        if (!this.capabilities.includes('sessions.reply')) { break; }
        return this.#reply(data, reply, this.#progressTo(t, id, data));
      case 'attachment.start':
      case 'attachment.chunk':
      case 'attachment.discard':
        if (!this.capabilities.includes('sessions.attach')) { break; }
        return this.#attachment(op, data).then(reply, (e) => t.deliver({ re: id, ok: false, error: e.message }));
      case 'session.stop':
        if (!this.capabilities.includes('sessions.stop')) { break; }
        return this.#stop(data, reply);
      case LAUNCH.options:
        // A read-only host still says what waits on it (a folder-trust question), as a real one does.
        if (!this.capabilities.includes(LAUNCH.capability) && !this.machine.readOnly) { break; }
        return reply(clone(this.launchOptions));
      case LAUNCH.launch:
        if (!this.capabilities.includes(LAUNCH.capability)) { break; }
        return this.#launch(data, reply);
      case TRUST.answer:
        if (!this.capabilities.includes(TRUST.capability)) { break; }
        return this.#answerTrust(data, reply);
      case UPDATE.get:
        if (!this.capabilities.includes(UPDATE.capability)) { break; }
        return reply(clone(this.update));
      case UPDATE.subscribe:
        if (!this.capabilities.includes(UPDATE.capability)) { break; }
        t.updateSubscribed = true;
        return reply(clone(this.update));
      case UPDATE.check:
      case UPDATE.install:
        if (!this.capabilities.includes(UPDATE.capability)) { break; }
        return reply(this.#updateAsk(op, data));
      case SIGN_IN.start:
      case SIGN_IN.cancel:
        if (!this.capabilities.includes(SIGN_IN.capability)) { break; }
        return this.#signIn(t, op, data, reply, (error) => t.deliver({ re: id, ok: false, error }));
      default:
        return t.deliver({ re: id, ok: false, error: `unknown op ${op}` });
    }
    return t.deliver({ re: id, ok: false, error: `unknown op ${op}` });
  }

  // The host's AttachmentStore, simplified: one file per id, chunks in order, checked
  // against its sha256 once whole. attachmentDelayMs slows each chunk, for watching progress.
  async #attachment(op, data) {
    this.attachmentLog.push({ op, attachment: data?.attachment, offset: data?.offset, size: data?.size, ...(data?.blob ? { blob: true } : {}) });
    if (op === 'attachment.start' && data?.blob) { return this.#blobAttachment(data); }
    const f = this.attachments.get(data?.attachment);
    if (op === 'attachment.discard') { return { discarded: !!f && !f.used && this.attachments.delete(data.attachment) }; }
    if (op === 'attachment.start') {
      if (!(data.size > 0 && data.size <= 256 * 1024)) { throw new Error(data.size > 0 ? 'the attachment is larger than 256 KiB' : 'the attachment is empty'); }
      if (f) { return { received: f.used ? f.size : f.received, complete: f.used || f.complete }; }
      this.attachments.set(data.attachment, { name: data.name, type: data.type, size: data.size, sha256: data.sha256, parts: [], received: 0, complete: false, used: false });
      return { received: 0, complete: false };
    }
    if (!f || f.used) { throw new Error(`attachment ${String(data?.attachment).slice(0, 8)} is not on the host (it expired, was removed, or never arrived); attach it again`); }
    if (this.attachmentDelayMs) { await new Promise((r) => setTimeout(r, this.attachmentDelayMs)); }
    // failNextChunk: the next chunk is turned down with this reason, as a host would on a fault.
    if (this.failNextChunk) { const reason = this.failNextChunk; this.failNextChunk = null; throw new Error(reason); }
    const bytes = Uint8Array.from(atob(data.data), (ch) => ch.charCodeAt(0));
    if (data.offset + bytes.length <= f.received) { return { received: f.received, complete: f.complete }; }
    if (data.offset !== f.received) { throw new Error(`expected the chunk at offset ${f.received}`); }
    f.parts.push(bytes);
    f.received += bytes.length;
    if (f.received < f.size) { return { received: f.received, complete: false }; }
    const whole = new Uint8Array(f.size);
    let at = 0;
    for (const p of f.parts) { whole.set(p, at); at += p.length; }
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', whole));
    const hash = btoa(String.fromCharCode(...digest)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    if (hash !== f.sha256) {
      this.attachments.delete(data.attachment);
      throw new Error("the attachment's bytes do not match its sha256, so it was dropped; send it again");
    }
    f.complete = true;
    f.bytes = whole;
    return { received: f.received, complete: true };
  }

  // A file in the blob store, as the host's AttachmentStore takes one: fetched in the
  // background from blobStore (a DemoBlobStore), opened, checked against its sha256, then deleted
  // from the store; attachment.start again reports how far it has got, or why it failed.
  #blobAttachment(data) {
    if (!this.capabilities.includes('sessions.attach.blob')) { throw new Error('this host cannot fetch from the blob store now: it is not connected to a relay that has one'); }
    if (!(data.size > 0 && data.size <= BLOB_FILE_MAX)) { throw new Error(data.size > 0 ? 'the attachment is larger than 100 MiB' : 'the attachment is empty'); }
    const f = this.attachments.get(data.attachment);
    if (f) {
      if (f.failed) { this.attachments.delete(data.attachment); throw new Error(f.failed); }
      return { received: f.used ? f.size : f.received, complete: f.used || f.complete };
    }
    const entry = { name: data.name, type: data.type, size: data.size, sha256: data.sha256, received: 0, complete: false, used: false, blob: true };
    this.attachments.set(data.attachment, entry);
    this.#fetchBlob(entry, data.blob);
    return { received: 0, complete: false };
  }

  async #fetchBlob(entry, blob) {
    if (this.attachmentDelayMs) { await new Promise((r) => setTimeout(r, this.attachmentDelayMs)); }
    const stored = this.blobStore?.read(blob.id);
    if (!stored) { entry.failed = 'the host could not fetch the attachment from the blob store (the relay refused the blob request: not-found); send it again'; return; }
    let bytes;
    try { bytes = await openBlob(stored, entry.size, fromBase64url(blob.key), blobIdOf(blob.id)); } catch (e) {
      entry.failed = `the attachment in the blob store is not what was sent (${e.message}), so it was dropped; send it again`;
      this.blobStore.delete(blob.id);
      return;
    }
    const hash = toBase64url(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)));
    this.blobStore.delete(blob.id);
    if (hash !== entry.sha256) { entry.failed = "the attachment's bytes do not match its sha256, so it was dropped; send it again"; return; }
    entry.bytes = bytes;
    entry.received = entry.size;
    entry.complete = true;
  }

  scriptAnswers(outcomes) { this.#script.push(...outcomes); }

  scriptReplies(outcomes) { this.#replyScript.push(...outcomes); }

  scriptStops(outcomes) { this.#stopScript.push(...outcomes); }

  scriptLaunches(outcomes) { this.#launchScript.push(...outcomes); }

  scriptTrust(outcomes) { this.#trustScript.push(...outcomes); }

  // A launched agent asking whether to trust its folder, listed in launch.options until answered.
  askTrust(question) {
    const q = { agent: 'codex', kind: 'codex', label: 'Codex', askedAt: now(), answerable: true, ...question };
    this.launchOptions = { ...this.launchOptions, trust: [q, ...(this.launchOptions.trust ?? []).filter((x) => x.id !== q.id)] };
    return q;
  }

  #answerTrust(data, reply) {
    this.trustLog.push(clone(data ?? null));
    const request = (() => { try { return JSON.parse(fromUtf8.decode(fromBase64url(data?.command?.body ?? ''))).request; } catch { return ''; } })();
    const known = this.trustAnswers.get(request);
    // expire: the command had expired by the time it was sent again, which a host refuses
    // before it looks for the first outcome.
    if (known?.expire) { this.#after(() => ({ outcome: 'refused', reason: 'the command has expired' })).then(reply); return; }
    if (known) { known.result.then(reply); return; }
    const step = this.#trustScript.shift() ?? null;
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const result = this.#after(() => this.#decideTrust(data ?? {}, scripted));
    this.trustAnswers.set(request, { result, expire: !!scripted?.expire });
    result.then((r) => { if (!scripted?.drop) { reply(r); } });
  }

  // As a host decides: the command must be this device's, for this machine, question and choice;
  // the question must still wait and be answerable. Trust starts its session; Quit closes it.
  #decideTrust(data, scripted) {
    let body = null;
    try { body = JSON.parse(fromUtf8.decode(fromBase64url(data.command?.body ?? ''))); } catch { body = null; }
    if (!body || body.action !== trustAction(data.choice) || body.target?.session !== data.question || body.to !== this.machine.id || body.target?.machine !== this.machine.id) {
      return { outcome: 'refused', reason: 'the command is not for this answer' };
    }
    const q = (this.launchOptions.trust ?? []).find((x) => x.id === data.question);
    if (!q) { return { outcome: 'refused', reason: 'that question is no longer waiting: it was answered, or the agent closed' }; }
    if (!q.answerable) { return { outcome: 'refused', reason: q.atMachine ?? 'Answer at the machine.' }; }
    if (scripted && !['started', 'declined'].includes(scripted.outcome)) {
      const reasons = {
        unconfirmed: `Not confirmed: answer at the machine. The question is still in ${q.label}'s window.`,
        refused: 'its window changed before anything was typed (fail screen 0); answer at the machine',
      };
      return { outcome: scripted.outcome, reason: scripted.reason ?? reasons[scripted.outcome] ?? 'from a newer host' };
    }
    this.launchOptions = { ...this.launchOptions, trust: this.launchOptions.trust.filter((x) => x.id !== q.id) };
    if (data.choice !== TRUST.trust) { return { outcome: 'declined', reason: `${q.label} was not trusted with ${q.workspace ?? q.folder}, and closed; nothing was launched` }; }
    const id = crypto.randomUUID();
    const at = now();
    this.upsert({ id, agent: q.kind, title: 'New session', cwd: q.folder, status: 'idle', statusSince: at, startedAt: at, updatedAt: at, processId: 4100, activity: { turnCount: 0 } });
    return { outcome: 'started', session: id };
  }

  // Holds every response not yet sent until releaseResponses(), so a state that lasts only
  // while a request is on its way (Sending…) stays to be looked at however slowly the browser
  // runs: a test that polled for it inside the answer delay missed it on a busy CI runner.
  // Not hold(): that is discovery's, below, and a second method of the same name replaced it.
  holdResponses() { this.#responsesHeld ??= new Promise((resolve) => { this.#letResponsesGo = resolve; }); }

  releaseResponses() { this.#letResponsesGo?.(); this.#responsesHeld = null; this.#letResponsesGo = null; }

  // Holds the end of the next snapshots (sessions.synced) until releaseSnapshot(): the sessions
  // arrive, but not word that they are all there, as a host with many sessions is slow to send
  // them all. For the tests of a machine back from offline (#269), which takes nothing until then.
  holdSnapshot() { this.#snapshotHeld ??= new Promise((resolve) => { this.#letSnapshotGo = resolve; }); }

  releaseSnapshot() { this.#letSnapshotGo?.(); this.#snapshotHeld = null; this.#letSnapshotGo = null; }

  // A response as a host gives one: after the answer delay, and after any hold.
  #after(decide) { return new Promise((resolve) => setTimeout(resolve, this.answerDelayMs)).then(() => this.#responsesHeld).then(decide); }

  #launch(data, reply) {
    this.launchLog.push(clone(data ?? null));
    const request = typeof data?.request === 'string' ? data.request : '';
    const known = this.launches.get(request);
    if (known) { known.result.then(reply); return; }
    const step = this.#launchScript.shift() ?? null;
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const result = this.#after(() => this.#decideLaunch(data ?? {}, scripted));
    this.launches.set(request, { result });
    result.then((r) => { if (!scripted?.drop) { reply(r); } });
  }

  // As a host decides: what it does not offer is refused, in its words; otherwise the session
  // starts in the workspace (or the resumed session's folder) and is upserted.
  #decideLaunch(data, scripted) {
    const o = this.launchOptions;
    if (scripted && scripted.outcome !== 'started') {
      const reasons = {
        busy: "a session this machine launched is waiting at its folder trust prompt; answer it at the machine",
        refused: 'agent-ha-bridge is running for this user, so this host leaves launching to it',
        unconfirmed: "the session started, but it is asking whether to trust its folder; answer that at the machine",
        failed: 'the agent could not be started (The system cannot find the file specified.)',
      };
      return { outcome: scripted.outcome, reason: scripted.reason ?? reasons[scripted.outcome] ?? 'from a newer host', ...(scripted.trust ? { trust: this.askTrust(scripted.trust) } : {}) };
    }
    const problem = checkLaunch(data);
    if (problem) { return { outcome: 'refused', reason: problem }; }
    if (data.transfer) { return this.#decideTransfer(data); }
    const resume = data.resume ? o.resumable.find((r) => r.id === data.resume) : null;
    if (data.resume && !resume) { return { outcome: 'refused', reason: `No resumable session '${data.resume}' on this machine; it is not on its resume list` }; }
    const agent = o.agents.find((a) => a.id === (data.agent ?? resume?.launcher ?? o.defaults.agent));
    if (!agent) { return { outcome: 'refused', reason: `Unknown agent '${data.agent}'; this machine has ${o.agents.map((a) => a.id).join(', ')}` }; }
    const workspace = resume ? null : o.workspaces.find((w) => w.label === (data.workspace ?? o.defaults.workspace));
    if (!resume && !workspace) { return { outcome: 'refused', reason: o.workspaces.length ? `Unknown workspace '${data.workspace}'; this machine has ${o.workspaces.map((w) => w.label).join(', ')}` : 'no workspace is approved on this machine yet; approve one at the machine (agent-bridge-host workspace add <folder>)' }; }
    if (!resume && agent.needsPrompt && !flattenPrompt(data.prompt)) { return { outcome: 'refused', reason: `${agent.label} starts its session only on a first message; give it a prompt` }; }
    for (const axis of ['model', 'effort', 'context']) {
      const v = data[axis];
      if (v && !agent[`${axis}s`].includes(v)) { return { outcome: 'refused', reason: `Unknown ${axis} '${v}' for ${agent.label}; it offers ${agent[`${axis}s`].join(', ') || 'only its own default'}` }; }
    }
    const cwd = resume?.cwd ?? workspace.path;
    const id = resume?.id ?? crypto.randomUUID();
    const said = flattenPrompt(data.prompt);
    const at = now();
    this.upsert({
      id, agent: agent.kind, title: resume?.title ?? (said ? (said.length > 60 ? `${said.slice(0, 59)}…` : said) : 'New session'), cwd,
      model: data.model || agent.defaults.model || undefined, status: said ? 'working' : 'idle', statusSince: at, startedAt: at, updatedAt: at,
      processId: 4000 + this.launches.size,
      activity: { ...(said ? { userMessage: said } : {}), turnCount: said ? 1 : 0 },
    });
    if (resume) { this.launchOptions = { ...o, resumable: o.resumable.filter((r) => r.id !== resume.id) }; }
    const permissions = data.permissions ?? o.defaults.permissions;
    return { outcome: 'started', session: id, launched: { agent: agent.id, ...(resume ? { resume: resume.id } : { workspace: workspace.label }), cwd, worktree: !!workspace?.isolate, permissions } };
  }

  // As the target and the machine the session is on decide between them: the command must be for
  // the session asked for, from Studio PC, to this machine; the session must still be offered and
  // run by an agent here; then it opens, under a new id, in the workspace picked.
  #decideTransfer(data) {
    const o = this.launchOptions;
    const { from, session, command } = data.transfer;
    if (from !== this.remote.hostId) { return { outcome: 'refused', reason: 'that machine is not online' }; }
    let body = null;
    try { body = JSON.parse(fromUtf8.decode(fromBase64url(command?.body ?? ''))); } catch { body = null; }
    if (!body || body.action !== TRANSFER.action || body.target?.session !== session || body.to !== this.machine.id || !command.mac || !command.by) {
      return { outcome: 'refused', reason: 'the session could not be brought from the other machine: the signed command is not for this launch: it must be signed by the device asking, for this machine to receive that session' };
    }
    const r = this.remote.resumable.find((x) => x.id === session);
    if (!r) { return { outcome: 'refused', reason: 'the session could not be brought from the other machine: Studio PC refused: that session is not one its machine offers (it is not on its resume list)' }; }
    const agent = o.agents.find((a) => a.id === (data.agent ?? r.launcher));
    if (!agent || agent.kind !== r.agent) { return { outcome: 'refused', reason: `the session is a ${r.agent} session; launch it with an agent that runs those` }; }
    const workspace = o.workspaces.find((w) => w.label === data.workspace);
    if (!workspace) { return { outcome: 'refused', reason: `Unknown workspace '${data.workspace}'; this machine has ${o.workspaces.map((w) => w.label).join(', ')}` }; }
    const id = crypto.randomUUID();
    const said = flattenPrompt(data.prompt);
    const at = now();
    this.upsert({
      id, agent: agent.kind, title: r.title, cwd: workspace.path, status: said ? 'working' : 'idle', statusSince: at, startedAt: at, updatedAt: at,
      processId: 4000 + this.launches.size, activity: { ...(said ? { userMessage: said } : {}), turnCount: said ? 1 : 0 },
    });
    const permissions = data.permissions ?? o.defaults.permissions;
    return {
      outcome: 'started', session: id,
      launched: { agent: agent.id, workspace: workspace.label, cwd: workspace.path, worktree: workspace.isolate, permissions, resume: id, transfer: { from, session, bytes: r.bytes } },
    };
  }

  #stop(data, reply) {
    this.stopLog.push(clone(data ?? null));
    const request = typeof data?.request === 'string' ? data.request : '';
    const known = this.stops.get(request);
    if (known) { known.result.then(reply); return; }
    const step = this.#stopScript.shift() ?? null;
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const result = this.#after(() => this.#decideStop(data, scripted));
    this.stops.set(request, { result });
    result.then((r) => { if (!scripted?.drop) { reply(r); } });
  }

  // As a host decides: a session that is not idle needs a second press carrying the token the
  // first was given, within the window; then it shows as ending, and goes.
  #decideStop(data, scripted) {
    const s = this.sessions.get(data?.id);
    if (!s) { return { outcome: 'stale', reason: 'the session has ended' }; }
    if (scripted && scripted.outcome !== 'ended') {
      const reasons = {
        busy: 'an answer or reply to this session is being delivered, or it is already being ended',
        refused: 'the session runs as administrator, and the host does not end elevated sessions; end it at the machine',
        stale: 'the session has ended',
        failed: "the host could not terminate the session's process (Access is denied.)",
      };
      return { outcome: scripted.outcome, reason: scripted.reason ?? reasons[scripted.outcome] ?? 'from a newer host' };
    }
    const b = s.activity?.background;
    const doing = s.status === 'working' ? 'this session is still working'
      : s.status === 'waiting' ? 'this session is waiting on you'
        : b?.agents ? 'this session is waiting for background agents it started'
          : b?.shells ? 'this session is waiting for background commands it started' : null;
    const arms = (this.#arms.get(s.id) ?? []).filter((a) => Date.now() - a.at <= this.stopWindowMs);
    if (doing && !arms.some((a) => a.token === data.confirm)) {
      const token = crypto.randomUUID().replaceAll('-', '');
      this.#arms.set(s.id, [...arms, { token, at: Date.now() }]);
      const seconds = Math.round(this.stopWindowMs / 1000);
      const why = data.confirm ? 'the first press has lapsed; ' : '';
      return { outcome: 'confirm', reason: `${why}${doing}; the confirmation lapses in ${seconds} s`, confirm: token, confirmSeconds: seconds };
    }
    this.#arms.delete(s.id);
    this.upsert({ ...s, status: 'ending', statusSince: now(), updatedAt: now() });
    return new Promise((resolve) => setTimeout(() => {
      this.remove(s.id);
      resolve({ outcome: 'ended', ...(scripted?.forced ? { forced: true } : {}) });
    }, this.answerDelayMs));
  }

  #reply(data, reply, progress) {
    this.replyLog.push(clone(data ?? null));
    const request = typeof data?.request === 'string' ? data.request : '';
    const known = this.replies.get(request);
    if (known) { known.repeat(progress, reply); return; }
    const step = this.#replyScript.shift() ?? 'delivered';
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const delivery = this.#delivery(progress);
    const result = this.#delivered(delivery, (typed) => this.#decideReply(data, scripted, typed));
    this.replies.set(request, delivery);
    result.then((r) => { if (!scripted.drop) { reply(delivery.told(r, delivery.start, !!progress)); } });
  }

  // Where the request asked for progress and this host offers it (sessions.progress), a
  // function sending one frame of it to that copy of the request; otherwise null.
  #progressTo(t, id, data) {
    return data?.progress === true && this.capabilities.includes('sessions.progress') ? (p) => t.deliver({ re: id, progress: p }) : null;
  }

  // One answer's or reply's progress, as a host keeps it: each stage goes to every copy of the
  // request that asked for progress, and a repeat still on its way gets the stages reached so far.
  // When each stage was reached is kept once; each copy's outcome counts its timings from that
  // copy's own arrival, a stage reached before it came as 0 (v1-sessions.md, as the host's
  // DeliveryTimeline does). A repeat used to be told the first copy's figures.
  #delivery(progress) {
    const stages = [];
    const told = new Set(progress ? [progress] : []);
    const at = {};
    return {
      start: Date.now(),
      // The first copy's outcome, once #delivered has begun it.
      result: null,
      tell(p) {
        if (p.stage === 'typed') { at.typed = Date.now(); }
        stages.push(p);
        for (const fn of told) { fn(p); }
      },
      join: (fn) => { if (!fn) { return; } for (const p of stages) { fn(p); } told.add(fn); },
      done(r) { at.done = Date.now(); return r; },
      // The outcome as one copy of the request is told it: with timings where that copy asked
      // for progress and the keys went.
      told(r, arrived, asked) {
        if (!asked || at.typed === undefined) { return r; }
        const since = (t) => Math.max(0, t - arrived);
        return { ...r, timings: { typed: since(at.typed), ...(r.outcome === 'delivered' ? { recorded: since(at.done), confirmed: since(at.done) } : {}) } };
      },
      // A repeat of the request, even one still being delivered: the stages so far and the first
      // copy's outcome, its timings its own.
      repeat(fn, reply) {
        const arrived = Date.now();
        this.join(fn);
        this.result.then((r) => reply(this.told(r, arrived, !!fn)));
      },
    };
  }

  // A delivery as a host makes one: received at once (after any hold of responses), then after
  // the answer delay and any hold before typing, decide(typed) types, saying typed as it does;
  // then, after any hold before the result and confirmDelayMs, the outcome, with its timings
  // where progress was asked for. The holds and the delay let a test, or a person in the demo,
  // see each stage (#600).
  #delivered(delivery, decide) {
    Promise.resolve(this.#responsesHeld).then(() => delivery.tell({ stage: 'received' }));
    delivery.result = this.#after(() => this.#heldBefore.get('typing')?.promise)
      .then(() => decide((working) => delivery.tell({ stage: 'typed', working })))
      .then(async (r) => {
        await this.#heldBefore.get('result')?.promise;
        if (this.confirmDelayMs) { await new Promise((resolve) => setTimeout(resolve, this.confirmDelayMs)); }
        return delivery.done(r);
      });
    return delivery.result;
  }

  // Holds every delivery before it types ('typing') or before its outcome goes ('result'), until
  // releaseBefore() with the same step: Sent and Typed stay to be looked at however slowly the
  // browser runs, as holdResponses() keeps Sending.
  holdBefore(step) {
    if (this.#heldBefore.has(step)) { return; }
    let release;
    const promise = new Promise((resolve) => { release = resolve; });
    this.#heldBefore.set(step, { promise, release });
  }

  releaseBefore(step) { this.#heldBefore.get(step)?.release(); this.#heldBefore.delete(step); }

  #decideReply(data, scripted, typed = () => {}) {
    const problem = checkReply(data);
    if (problem) { return { outcome: 'refused', reason: problem }; }
    const s = this.sessions.get(data?.id);
    if (!s) { return { outcome: 'stale', reason: 'the session has ended' }; }
    if (s.agent !== 'copilot') { return { outcome: 'refused', reason: `replying is not supported for ${s.agent} yet; reply at the machine` }; }
    if (s.question) { return { outcome: 'refused', reason: 'the session is waiting for an answer to its question; answer that first' }; }
    // The files it names, as the host claims them: held, whole, and not gone with another reply.
    const files = [];
    for (const { id } of data.attachments ?? []) {
      const f = this.attachments.get(id);
      if (!f) { return { outcome: 'refused', reason: `attachment ${id.slice(0, 8)} is not on the host (it expired, was removed, or never arrived); attach it again` }; }
      if (f.used) { return { outcome: 'refused', reason: `attachment ${id.slice(0, 8)} went with an earlier reply; attach it again` }; }
      if (!f.complete) { return { outcome: 'refused', reason: `attachment ${id.slice(0, 8)} has not finished arriving; send it again` }; }
      files.push(f);
    }
    const { outcome } = scripted;
    if (outcome === 'stale') { return { outcome, reason: scripted.reason ?? 'the session has ended' }; }
    if (outcome === 'busy') { return { outcome, reason: scripted.reason ?? 'another answer or reply to this session is being delivered' }; }
    if (outcome === 'refused') { return { outcome, reason: scripted.reason ?? 'the session is running elevated' }; }
    // Typed: the session has a new message from you, and works on it. Files lead as the host
    // types them, @ and the path it staged them at.
    for (const f of files) { f.used = true; }
    const mentions = files.map((f) => `@C:\\Users\\demo\\AppData\\Local\\AgentBridge\\default\\attachments\\${stagedName(f.name)}`);
    const said = files.length ? [...mentions, typedForm(data.text ?? '').trim()].filter(Boolean).join(' ') : typedForm(data.text);
    typed(s.status === 'working');
    this.typedReplies.push({ id: s.id, request: data.request, said, files: files.map(({ name, type, size }) => ({ name, type, size })) });
    this.upsert({ ...s, status: 'working', statusSince: now(), updatedAt: now(), activity: { ...s.activity, userMessage: said } });
    this.onReplied?.(s.id, said);
    if (outcome === 'delivered') { return { outcome }; }
    return { outcome, reason: scripted.reason ?? (outcome === 'unconfirmed' ? 'the session did not record the reply within 60 seconds' : 'from a newer host') };
  }

  #answer(data, reply, fail, progress) {
    this.answerLog.push(clone(data ?? null));
    const request = typeof data?.request === 'string' ? data.request : '';
    // A repeat, even of one still being delivered, gets the first one's outcome.
    const known = this.answers.get(request);
    if (known) { known.repeat(progress, reply); return; }
    const step = this.#script.shift() ?? 'delivered';
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const delivery = this.#delivery(progress);
    // error: the host fails the op itself, with nothing typed, so the same request can go again.
    // A host says it received a request before any check, so that one says so too.
    if (scripted.error) {
      Promise.resolve(this.#responsesHeld).then(() => delivery.tell({ stage: 'received' }));
      this.#after(() => null).then(() => fail(scripted.error));
      return;
    }
    const result = this.#delivered(delivery, (typed) => this.#decide(data, scripted, typed));
    this.answers.set(request, delivery);
    // drop: the host did the work, but its response never reached the phone.
    result.then((r) => { if (!scripted.drop) { reply(delivery.told(r, delivery.start, !!progress)); } });
  }

  #decide(data, scripted, typed = () => {}) {
    const s = this.sessions.get(data?.id);
    const q = s?.question ? readQuestion(s.question) : null;
    if (!q || q.id !== data.question) { return { outcome: 'stale', reason: 'the session is no longer asking that question' }; }
    const problem = checkAnswer(q, data) ?? this.#escapedRefusal(q, data);
    if (problem) { return { outcome: 'refused', reason: problem }; }
    const { outcome } = scripted;
    if (outcome === 'stale') { return { outcome, reason: scripted.reason ?? 'answered at the machine' }; }
    if (outcome === 'busy') { return { outcome, reason: scripted.reason ?? 'another answer or reply to this session is being delivered' }; }
    if (outcome === 'refused') { return { outcome, reason: scripted.reason ?? 'the session is running elevated' }; }
    // Typed: the question goes, and the session works on the answer.
    const said = data.text ?? Object.entries(data.fields ?? {}).map(([k, v]) => `${k}: ${JSON.stringify(v)}`).join('; ');
    typed(s.status === 'working');
    this.typed.push({ id: s.id, question: q.id, request: data.request, said });
    this.upsert({ ...s, status: 'working', statusSince: now(), updatedAt: now(), question: null, activity: { ...s.activity, userMessage: said } });
    this.onTyped?.(s.id);
    if (outcome === 'delivered') { return { outcome }; }
    return { outcome, reason: scripted.reason ?? (outcome === 'unconfirmed' ? 'the session stopped answering' : 'from a newer host') };
  }

  // As the host's AnswerService.EscapedRefusal, for what a page can check: Approve of an escaped
  // approval only with a confirmation naming this question, this request and what was read.
  #escapedRefusal(q, data) {
    if (q.kind !== 'approval' || !q.escaped || data.fields?.decision !== 'approve') { return null; }
    if (q.escaped.approve === 'tooMany') { return 'this approval holds too many hidden characters to show on another device, so it can only be approved at the machine; deny it from here, or answer at the machine'; }
    if (q.escaped.approve !== 'confirm') { return 'this machine takes Approve for an approval with escaped characters only at the machine (permissions.escapedApproval); deny it from here, or answer at the machine'; }
    let body = null;
    try { body = JSON.parse(fromUtf8.decode(fromBase64url(data.confirm?.body ?? ''))); } catch { body = null; }
    if (!body) { return 'this approval holds hidden or right-to-left characters, so Approve needs the escaped-command confirmation, which this app does not give: deny it, update the app, or answer at the machine'; }
    this.confirmLog.push(body);
    const t = body.target ?? {};
    if (body.action !== 'session.approve:escaped' || t.session !== data.id || t.question !== q.id || body.request !== data.request) { return 'the confirmation is for another approval'; }
    if (t.shown !== q.escaped.shown) { return 'the command changed after it was confirmed: read it again'; }
    return null;
  }

  confirmLog = [];

  // An escaped approval's confirmation waits here while a test holds it (demoConfirm).
  signing = null;
  holdSigning() { this.signing = new Promise((resolve) => { this.releaseSigning = () => { this.signing = null; resolve(); }; }); }

  #event(t, ev, data) { t.deliver({ ev, seq: ++t.seq, data }); }

  // The next presses' refusals, in order (their reasons); none scripted, a press is taken.
  scriptUpdates(reasons) { this.#updateScript.push(...reasons); }

  // What the app reports, sent to every channel following it as update.state.
  setUpdate(state) {
    this.update = state;
    for (const t of this.transports) { if (t.updateSubscribed) { this.#event(t, UPDATE.event, clone(state)); } }
  }

  // As the machine's app would: a check finds what it finds; an install downloads, installs
  // and comes back on the new release, each step updateStepMs apart.
  #updateAsk(op, data) {
    this.updateLog.push({ op, request: data?.request ?? null });
    const refused = this.#updateScript.shift();
    if (refused) { return { outcome: 'refused', reason: refused, state: clone(this.update) }; }
    const request = data?.request ?? null;
    const step = (fn, after) => setTimeout(fn, after * this.updateStepMs);
    if (op === UPDATE.check) {
      this.setUpdate({ ...this.update, phase: 'checking', request });
      step(() => this.setUpdate({ ...this.update, phase: 'idle', request: null, checkedAt: now() }), 1);
      return { outcome: 'accepted', state: clone(this.update) };
    }
    const from = this.update.current;
    const to = this.update.available;
    this.setUpdate({ ...this.update, phase: 'downloading', progress: 50, request });
    step(() => this.setUpdate({ ...this.update, phase: 'installing', progress: null }), 1);
    step(() => this.setUpdate({
      ...this.update, current: to, available: null, notes: null, phase: 'idle', request: null, checkedAt: now(),
      last: { outcome: 'updated', from, to, request, at: now() },
    }), 2);
    return { outcome: 'accepted', state: clone(this.update) };
  }

  // A new usage report, sent to every channel subscribed to usage as a host would.
  setUsage(report) {
    this.usage = report;
    for (const t of this.transports) { if (t.usageSubscribed) { this.#event(t, USAGE.update, report); } }
  }

  #broadcast(ev, data) {
    for (const t of this.transports) { if (t.subscribed) { this.#event(t, ev, data); } }
  }

  upsert(session) {
    const s = { ...session, updatedAt: session.updatedAt ?? now() };
    this.sessions.set(s.id, s);
    this.revision++;
    this.#broadcast('session.upsert', withoutHistory(s));
  }

  remove(id) {
    this.sessions.delete(id);
    this.revision++;
    this.#broadcast('session.remove', { id });
  }

  // What holds discovery ({ reason, agents }), or null once nothing does, as discovery.changed.
  hold(held) {
    this.held = held ?? null;
    this.#broadcast('discovery.changed', held ? { discoveryHeld: held } : {});
  }

  // hello.machine.signIn (#800): one entry per agent and account with sessions needing sign-in.
  #rollUp() {
    const groups = new Map();
    for (const s of this.sessions.values()) {
      const at = s.attention;
      if (at?.kind !== 'signInNeeded') { continue; }
      const k = `${s.agent}\n${at.account ?? ''}`;
      if (!groups.has(k)) { groups.set(k, { agent: s.agent, provider: at.provider, ...(at.account ? { account: at.account } : {}), since: at.since, sessions: [], signIn: at.signIn, ...(at.recovery ? { recovery: at.recovery } : {}) }); }
      groups.get(k).sessions.push(s.id);
    }
    return [...groups.values()];
  }

  // As a host runs the agent's own device-code sign-in: started with a flow id, then the code and
  // URL the agent printed, sent only to the channels in the flow, never in a session or snapshot.
  // A second start while one runs joins it (busy) and gets its code too.
  #signIn(t, op, data, reply, fail) {
    this.signInLog.push({ op, ...clone(data ?? {}) });
    const failure = this.signInFailures.shift();
    if (failure) { return fail(failure); }
    if (op === SIGN_IN.cancel) {
      if (this.signInFlow?.id === data?.flow) { this.signInFlow = null; }
      const slow = this.signInCancelDelays.shift() ?? 0;
      if (slow) { return setTimeout(() => { this.signInLog.push({ op: 'cancelled', flow: data?.flow }); reply({ outcome: 'cancelled' }); }, slow); }
      return reply({ outcome: 'cancelled' });
    }
    const delay = this.signInDelays.shift() ?? 0;
    if (delay) { const now = reply; reply = (v) => setTimeout(() => now(v), delay); }
    const scripted = this.signInOutcomes.shift();
    if (scripted) { return reply(scripted); }
    if (![...this.sessions.values()].some((s) => s.attention?.kind === 'signInNeeded' && s.agent === data?.agent)) { return reply({ outcome: 'refused', reason: 'nothing on this machine needs that sign-in' }); }
    const joining = !!this.signInFlow;
    if (!this.signInFlow) {
      const code = this.nextCode ?? { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 15 * 60000).toISOString() };
      this.nextCode = null;
      this.signInFlow = { id: `flow-${++this.#flows}`, agent: data.agent, code, channels: new Set() };
    }
    const flow = this.signInFlow;
    flow.channels.add(t);
    reply({ outcome: joining ? 'busy' : 'started', flow: flow.id });
    setTimeout(() => { if (this.signInFlow === flow && this.transports.has(t)) { this.#event(t, SIGN_IN.code, { flow: flow.id, agent: flow.agent, ...clone(flow.code) }); } }, 50);
    return undefined;
  }

  // The person entered the code at github.com (or didn't): signin.done to the flow's channels.
  // signedIn doesn't clear the sessions: under #800's clear gate the host only marks them pending
  // (signed in, waiting for the session to confirm) until the model answers (confirmSignIn).
  finishSignIn(outcome, reason = null) {
    const flow = this.signInFlow;
    if (!flow) { return; }
    this.signInFlow = null;
    const resumed = outcome === 'signedIn' ? this.#markSignIn(flow.agent, 'pending') : [];
    for (const t of flow.channels) { if (this.transports.has(t)) { this.#event(t, SIGN_IN.done, { flow: flow.id, outcome, ...(reason ? { reason } : {}), resumed, continue: [] }); } }
  }

  // Silent recovery: a stored credential still worked, and nobody signed in.
  recoverSignIn(agent = 'copilot') { this.#clearSignIn(agent); }

  // The model answered after the sign-in: the clear gate passes and the host clears the sessions.
  confirmSignIn(agent = 'copilot') { this.#clearSignIn(agent); }

  // The next turn failed sign-in again: back to needing it, with a new since.
  failSignInAgain(agent = 'copilot') { this.#markSignIn(agent, 'failed', true); }

  #markSignIn(agent, recovery, again = false) {
    const at = now();
    const ids = [];
    for (const s of [...this.sessions.values()]) {
      if (s.agent !== agent || s.attention?.kind !== 'signInNeeded') { continue; }
      ids.push(s.id);
      this.upsert({ ...s, attention: { ...s.attention, recovery, ...(again ? { since: at } : {}) }, updatedAt: at });
    }
    return ids;
  }

  // A sign-in another device started: a press here joins it (busy).
  signInElsewhere(agent = 'copilot') {
    const code = this.nextCode ?? { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 15 * 60000).toISOString() };
    this.nextCode = null;
    this.signInFlow = { id: `flow-${++this.#flows}`, agent, code, channels: new Set() };
  }

  // The sessions' sign-in fails again after it was cleared: a new episode, as a token revoked.
  needSignInAgain() {
    const at = now();
    for (const [id, attention] of this.#signInCleared) {
      const s = this.sessions.get(id);
      if (s) { this.upsert({ ...s, attention: { ...attention, recovery: 'failed', since: at }, updatedAt: at }); }
    }
    this.#signInCleared.clear();
  }

  #signInCleared = new Map();

  #clearSignIn(agent) {
    const at = now();
    const ids = [];
    for (const s of [...this.sessions.values()]) {
      if (s.agent !== agent || s.attention?.kind !== 'signInNeeded') { continue; }
      ids.push(s.id);
      this.#signInCleared.set(s.id, s.attention);
      this.upsert({ ...s, attention: null, statusSince: at, updatedAt: at });
    }
    return ids;
  }

  // The last seq sent on its channels: the one a client shows, in tests.
  get seq() { return Math.max(0, ...[...this.transports].map((t) => t.seq)); }

  // A frame exactly as given, for testing what the client does with odd ones. A frame
  // with a numeric seq moves each channel's counter on to it.
  frame(f) {
    for (const t of this.transports) {
      if (Number.isInteger(f.seq)) { t.seq = f.seq; }
      t.deliver(f);
    }
  }

  get(id) { return this.sessions.get(id); }
}

// The name the host stages a file under: a random prefix, then the name made safe to mention.
function stagedName(name) {
  const leaf = String(name ?? '').replace(/^.*[\\/]/, '');
  const dot = leaf.lastIndexOf('.');
  const ext = dot > 0 && /^\.[0-9A-Za-z]{1,12}$/.test(leaf.slice(dot)) ? leaf.slice(dot).toLowerCase() : '';
  const stem = (ext ? leaf.slice(0, dot) : leaf).replace(/[^0-9A-Za-z._-]/g, '-').replace(/^[-.]+|[-.]+$/g, '').slice(0, 48) || 'attachment';
  const prefix = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
  return `${prefix}-${stem}${ext}`;
}

function withoutHistory(s) {
  const c = clone(s);
  if (c.activity) { delete c.activity.history; }
  return c;
}

// Tool history for session.get, made up from the session so each card has some.
function withHistory(s) {
  const c = clone(s);
  c.activity ??= {};
  if (!Array.isArray(c.activity.history)) {
    const base = Date.parse(s.updatedAt) || Date.now();
    const at = (k) => new Date(base - k * 47000).toISOString();
    const tools = [['view', 'docs/protocol/v1-sessions.md'], ['powershell', 'Run the unit tests'], ['edit', 'pwa/js/host-client.js'], ['grep', 'session.upsert'], ['powershell', 'npx playwright test']];
    c.activity.history = tools.map(([name, summary], k) => ({
      id: `${s.id}-t${k}`, name, summary, status: k === 1 ? 'failed' : 'done', startedAt: at(tools.length - k), endedAt: at(tools.length - k - 0.5),
    }));
    if (c.activity.currentTool) { c.activity.history.push(c.activity.currentTool); }
  }
  return c;
}

// session.get { id, agent }: the delegate's entry with its own words, as a host since sub-agents
// sends it, or missing when the session lists no such delegate.
function delegateDetail(s, agent, said) {
  const entry = (s.subagents ?? []).find((a) => a.id === agent);
  if (!entry) { return { missing: true }; }
  const words = said[agent] ?? {};
  return { ...clone(entry), response: words.response ?? entry.line ?? '', ...(words.reasoning ? { reasoning: words.reasoning } : {}), history: delegateTools(entry) };
}

// Its tool calls, oldest first as a host sends them: two it finished a minute apart from its
// start, and the one it is running now.
function delegateTools(entry) {
  const start = Date.parse(entry.startedAt);
  if (!Number.isFinite(start)) { return entry.currentTool ? [clone(entry.currentTool)] : []; }
  const at = (ms) => new Date(start + ms).toISOString();
  const calls = [
    { id: `${entry.id}-t0`, name: 'view', summary: 'src/Host/RestartWitness.cs', status: 'done', startedAt: at(5000), endedAt: at(9000) },
    { id: `${entry.id}-t1`, name: 'grep', summary: 'ResetTimer', status: entry.outcome === 'failed' ? 'failed' : 'done', startedAt: at(20000), endedAt: at(83000) },
  ];
  if (entry.currentTool) { calls.push(clone(entry.currentTool)); }
  return calls;
}

// What the demo's delegates said in full, beyond the latest line a list carries.
const DELEGATE_WORDS = {
  'demo-agent-suite': {
    response: 'The suite fails on HEAD: **RestartAfterResume** times out.\n\nBisecting over the last 10 commits: 4 left to try.',
    reasoning: 'A timing failure that only shows after a resume. Bisect first, then read the timer.',
  },
  'demo-agent-witness': {
    response: 'The witness follows the first reply of a turn, not the latest. That is why it misses a resume.',
    reasoning: 'Check CI before saying so.',
  },
};

// ?delegates=1: a session whose main agent started two background delegates and went idle. The
// host's top-level response and reasoning are the delegates' (source names them); the main
// agent's own are in own, which the card shows as its words.
export function demoDelegating(base = Date.now()) {
  const at = (ms) => new Date(base - ms).toISOString();
  return {
    id: 'demo-delegates', agent: 'copilot', title: 'Fix RestartAfterResume', repository: 'danswett/agent-bridge', branch: 'fix/restart-after-resume',
    model: 'claude-opus-5.5', status: 'idle', statusSince: at(17 * 60000), startedAt: at(20 * 60000), updatedAt: at(30000),
    activity: {
      response: 'Bisecting over the last 10 commits: 4 left to try.',
      reasoning: 'Check CI before saying so.',
      userMessage: 'Run the suite and check the witness logic, both in the background.',
      turnCount: 1,
      background: { agents: 2, shells: 1 },
      source: { response: 'demo-agent-suite', reasoning: 'demo-agent-witness' },
      own: { response: 'Started 2 background agents: one runs the full suite, one reads the witness logic. I\'ll report back when both are done.', reasoning: 'Two independent jobs: delegate both.', at: at(17 * 60000) },
    },
    subagents: [
      { id: 'demo-agent-witness', kind: 'background', name: 'Read witness shell logic', task: 'Read the witness\'s shell logic and check it follows the latest reply of a turn.', model: 'gpt-5.6-luna', status: 'working', startedAt: at(17 * 60000), line: 'Check CI before saying so.', currentTool: { id: 'demo-t1', name: 'powershell', summary: 'Watch the checks', status: 'running', startedAt: at(40000) }, shells: 0 },
      { id: 'demo-agent-suite', kind: 'background', name: 'Run full test suite', task: 'Run the full test suite on HEAD and report any failure with its first error line.', model: 'claude-haiku-5.5', status: 'working', startedAt: at(16 * 60000), line: 'Bisecting over the last 10 commits: 4 left to try.', shells: 1 },
      { id: 'demo-agent-grep', parent: 'demo-agent-witness', kind: 'task', name: 'Find the timer reset', model: 'gpt-5.6-luna', status: 'working', startedAt: at(2 * 60000), line: 'Two candidates in RestartWitness.cs.', shells: 0 },
      { id: 'demo-agent-review', parent: 'demo-agent-witness', kind: 'task', name: 'Review the witness change', model: 'gpt-5.6-luna', status: 'ended', outcome: 'done', startedAt: at(12 * 60000), endedAt: at(11 * 60000), line: 'No regressions: the flag now follows the latest reply.', shells: 0 },
      { id: 'demo-agent-lint', kind: 'task', name: 'Lint the PWA', model: 'claude-haiku-5.5', status: 'ended', outcome: 'failed', startedAt: at(15 * 60000), endedAt: at(14 * 60000), line: 'eslint could not start: no config found.', shells: 0 },
    ],
  };
}

// A few changes on a loop, so the demo looks alive: the working session's tool moves on,
// a new session appears and goes, the delegating one's background agents come and go, and
// the idle one gets a follow-up.
// What the demo machine offers: two agents, two workspaces (one isolated), one session to resume.
function demoLaunchOptions() {
  const tuning = { efforts: ['low', 'medium', 'high'], contexts: [] };
  return {
    agents: [
      { id: 'copilot', label: 'Copilot', kind: 'copilot', models: ['claude-opus-5.5', 'claude-sonnet-5', 'gpt-6-sol'], ...tuning, contexts: ['default', '1m'], profiles: null, defaults: { model: 'claude-opus-5.5' }, attachments: true, needsPrompt: false },
      { id: 'codex', label: 'Codex', kind: 'codex', models: ['gpt-6-sol'], ...tuning, profiles: null, defaults: {}, attachments: false, needsPrompt: true },
    ],
    workspaces: [
      { label: 'agent-bridge', path: 'C:\\Users\\demo\\repos\\agent-bridge', isolate: true },
      { label: 'notes', path: 'C:\\Users\\demo\\notes', isolate: false },
    ],
    resumable: [
      { id: '7d3c1a2e-5b6f-4c8d-9e0a-1b2c3d4e5f60', agent: 'copilot', launcher: 'copilot', title: 'Fix the flaky relay test', cwd: 'C:\\Users\\demo\\repos\\agent-bridge', workspace: 'agent-bridge', updatedAt: '2026-10-27T18:02:11.000Z' },
    ],
    defaults: { agent: 'copilot', workspace: 'agent-bridge', permissions: 'ask' },
  };
}

// What Studio PC offers to hand on: two ended sessions, one each for the agents the demo runs.
function demoRemoteResumable() {
  return [
    { id: '2b4e6f80-1a3c-4d5e-8f70-9a0b1c2d3e4f', agent: 'copilot', launcher: 'copilot', title: 'Draft the transfer docs', cwd: 'D:\\src\\agent-bridge', workspace: 'agent-bridge', updatedAt: '2026-10-27T16:40:00.000Z', bytes: 3 * 1024 * 1024 + 123456 },
    { id: '5c6d7e8f-9a0b-4c1d-8e2f-3a4b5c6d7e8f', agent: 'codex', launcher: 'codex', title: 'Profile the relay under load', cwd: 'D:\\src\\relay', workspace: 'relay', updatedAt: '2026-10-26T22:15:00.000Z', bytes: 812345 },
  ];
}

// Answering this machine's folder-trust questions, with commands signed by a key this page makes
// for itself for the demo machine's.
// client(): the client of the channel open to that machine now, or null.
export async function demoTrust(host, client) {
  const me = await P256KeyPair.generate();
  host.key ??= await P256KeyPair.generate();
  const deviceId = await deviceIdOf(me.public);
  const machinePublic = host.key.public;
  return {
    sign: async (question, choice) => {
      const command = await signTrust({ key: me, deviceId, machinePublic, question, choice });
      // The demo machine's id is not its key's: the body names it as the host would read it.
      const body = JSON.parse(fromUtf8.decode(fromBase64url(command.body)));
      return { ...command, body: toBase64url(new TextEncoder().encode(JSON.stringify({ ...body, target: { ...body.target, machine: host.machine.id }, to: host.machine.id }))) };
    },
    answer: (data) => { const c = client(); return c ? c.answerTrust(data) : Promise.reject(new Error('not connected')); },
  };
}

// Confirming this machine's escaped approvals (#803), signed by a key this page makes for itself,
// for the demo machine's.
export async function demoConfirm(host) {
  const me = await P256KeyPair.generate();
  host.key ??= await P256KeyPair.generate();
  const deviceId = await deviceIdOf(me.public);
  return async ({ session, question, shown, request, now }) => {
    // Tests hold signing (host.holdSigning()) to press Cancel or Back while it is under way.
    await host.signing;
    return signEscapedApproval({ key: me, deviceId, machinePublic: host.key.public, session, question, shown, request, now, to: host.machine.id });
  };
}

// The other machine, as the launch panel sees it: its sessions and a key, and commands signed
// for it by a key this page makes for itself.
export async function demoTransfers(host, targetId) {
  const me = await P256KeyPair.generate();
  const studio = await P256KeyPair.generate();
  const deviceId = await deviceIdOf(me.public);
  return {
    remotes: async () => {
      await new Promise((r) => { setTimeout(r, host.remoteDelayMs); });
      return [{ hostId: host.remote.hostId, machineName: host.remote.machineName, key: studio.public, resumable: host.remote.resumable.map(({ bytes, ...r }) => r) }];
    },
    sign: (remote, session) => signTransfer({ key: me, deviceId, machinePublic: remote.key, session, to: targetId }),
  };
}

// The relay's blob store and Azure Storage, in the page: tickets as the relay issues them (the
// blob's random id first), write-once uploads with progress, reads by the host, deletes. The
// same interface as blobs.js RelayBlobs. Tests steer it: uploadDelayMs slows an upload so its
// progress and Cancel can be seen; refuseNextPut gives the next put the relay's refusal;
// failNextUpload, storage's HTTP status for the next upload.
export class DemoBlobStore {
  stored = new Map();
  log = [];
  uploadDelayMs = 0;
  refuseNextPut = null;
  failNextUpload = 0;

  constructor({ max = storedSize(200 * 1024 * 1024) } = {}) { this.maxStored = max; }

  get max() { return this.maxStored; }

  async put(to, size) {
    this.log.push({ op: 'put', to, size });
    const refusal = this.refuseNextPut ?? (size > this.maxStored ? 'too-large' : null);
    this.refuseNextPut = null;
    if (refusal) { throw Object.assign(new Error(`the relay refused the blob request: ${refusal}`), { relayReason: refusal }); }
    const name = toBase64url(crypto.getRandomValues(new Uint8Array(16)));
    return { blob: `${name}.e30.demo`, url: `https://demo.blob.core.windows.net/blobs/${name}?sp=c`, expires: new Date(Date.now() + 30 * 60000).toISOString() };
  }

  async upload(url, body, { signal, onProgress } = {}) {
    const name = new URL(url).pathname.split('/').pop();
    this.log.push({ op: 'upload', name, size: body.length });
    for (let step = 1; step <= 10; step++) {
      signal?.throwIfAborted();
      if (this.uploadDelayMs) { await new Promise((r) => setTimeout(r, this.uploadDelayMs / 10)); }
      signal?.throwIfAborted();
      onProgress?.((body.length * step) / 10);
    }
    if (this.failNextUpload) {
      const status = this.failNextUpload;
      this.failNextUpload = 0;
      throw Object.assign(new Error(`storage answered ${status} to the upload`), { status });
    }
    if (this.stored.has(name)) { throw Object.assign(new Error('storage answered 409 to the upload'), { status: 409 }); }
    this.stored.set(name, body.slice());
  }

  read(ticket) { return this.stored.get(String(ticket).split('.')[0]) ?? null; }

  async delete(ticket) {
    this.log.push({ op: 'delete' });
    return this.stored.delete(String(ticket).split('.')[0]);
  }
}

// ?signin=1 (#800): four Copilot sessions on this machine that stopped because its sign-in expired
// (the VM1 fixture's words), rolled up into one sign-in needed; a session whose turn ended on the
// quota; and one that has said nothing past the host's threshold while working. Each says so in
// its attention, over a truthful status. The sessions are added to the host given.
export function demoSignIn(host, base = Date.now()) {
  const at = (ms) => new Date(base - ms).toISOString();
  const titles = ['Coordinate the Shiproom', 'Fix the relay reconnect', 'Review the usage panel', 'Draft the release notes'];
  const ids = titles.map((_, i) => `signin-${i + 1}`);
  titles.forEach((title, i) => host.sessions.set(ids[i], {
    id: ids[i], agent: 'copilot', title, repository: 'danswett/agent-bridge', model: 'gpt-6.1-sol',
    status: 'idle', statusSince: at(300000 + i * 60000), startedAt: at(3600000), updatedAt: at(300000 + i * 60000),
    activity: { response: 'Working on it.', turnCount: 3 + i },
    attention: { kind: 'signInNeeded', since: at(300000 + i * 60000), agent: 'copilot', provider: 'github', account: 'demo-user', message: 'Copilot needs you to sign in again (401).', signIn: 'phone', recovery: 'failed' },
  }));
  // These two are driven by an agent: their rows still say what is wrong (rules 10-12 over rule 7).
  host.sessions.set('error-quota', {
    id: 'error-quota', agent: 'claude', title: 'Summarise the incident', driver: 'agent', driverPrincipal: 'homeassistant:demo', status: 'idle', statusSince: at(120000), updatedAt: at(120000),
    activity: { response: 'Reading the logs.', turnCount: 2 },
    attention: { kind: 'error', since: at(120000), agent: 'claude', errorClass: 'quota', message: 'Claude stopped with an error.' },
  });
  host.sessions.set('stalled', {
    id: 'stalled', agent: 'codex', title: 'Re-run the flaky suite', driver: 'agent', driverPrincipal: 'homeassistant:demo', status: 'working', statusSince: at(900000), updatedAt: at(900000),
    activity: { response: 'Running the suite again.', turnCount: 1 },
    attention: { kind: 'notResponding', since: at(480000), agent: 'codex', lastEventAt: at(600000) },
  });
  return ids;
}

// The demo's other machines, after the first (?machines=): Laptop, with a session to reply to
// and one asking a question, and DEV-VM1 (shadow), which takes nothing from the phone.
export function demoMachines(count, { answerDelayMs = 700, stopWindowMs = 10000 } = {}) {
  const at = (ms) => new Date(Date.now() - ms).toISOString();
  const machines = [];
  if (count >= 2) {
    machines.push(new DemoHost({
      revision: 1,
      sessions: [
        {
          id: 'laptop-copilot-1', agent: 'copilot', title: 'Tidy the laptop dotfiles', repository: 'danswett/dotfiles', branch: 'main', model: 'claude-sonnet-5',
          cwd: 'C:\\Users\\demo\\dotfiles', status: 'idle', statusSince: at(300000), startedAt: at(3600000), updatedAt: at(240000), processId: 7120,
          activity: { response: 'Moved the PowerShell profile under `profile/` and updated the links.', turnCount: 4 },
        },
        {
          id: 'laptop-ask-1', agent: 'copilot', title: 'Rotate the test certificates', repository: 'danswett/agent-bridge', branch: 'chore/certs', model: 'gpt-6-sol',
          cwd: 'C:\\Users\\demo\\repos\\agent-bridge', status: 'waiting', statusSince: at(60000), startedAt: at(900000), updatedAt: at(60000), processId: 7344,
          activity: { response: 'The new certificates are ready.', turnCount: 2 },
          question: {
            id: 'toolu_laptop_q1', kind: 'form', message: 'Replace the certificates **now**?', askedAt: at(60000),
            fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'now', label: 'Yes, now' }, { value: 'later', label: 'Later' }] }],
          },
        },
      ],
    }, { machineId: 'demo-laptop', machineName: 'Laptop', answerDelayMs, stopWindowMs }));
  }
  if (count >= 3) {
    machines.push(new DemoHost({
      revision: 1,
      sessions: [{
        id: 'shadow-ask-1', agent: 'copilot', title: 'Migrate the VM to the new host', repository: 'danswett/agent-ha-bridge', branch: 'main', model: 'claude-opus-5.5',
        cwd: 'C:\\Users\\demo\\repos\\agent-ha-bridge', status: 'waiting', statusSince: at(120000), startedAt: at(1800000), updatedAt: at(120000), processId: 9001,
        activity: { response: 'Ready to switch over.', turnCount: 6 },
        question: {
          id: 'toolu_shadow_q1', kind: 'form', message: 'Switch the VM over now?', askedAt: at(120000),
          fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'yes', label: 'Yes' }, { value: 'no', label: 'No' }] }],
        },
      }],
    }, {
      machineId: 'demo-shadow', machineName: 'DEV-VM1 (shadow)', answerDelayMs, stopWindowMs, answering: false,
      readOnly: 'agent-ha-bridge is running for this user (AgentBridgeDaemon); answer at the machine or through it',
    }));
  }
  return machines;
}

export function runScript(host, { tickMs = 4000 } = {}) {
  const steps = [
    () => {
      const s = host.get('6f1c2b7e-claude-example');
      if (!s) { return; }
      host.upsert({ ...s, updatedAt: now(), activity: { ...s.activity, currentTool: { id: `toolu_${Date.now()}`, name: 'Bash', summary: 'dotnet test --filter Relay', status: 'running', startedAt: now() } } });
    },
    () => host.upsert({
      id: 'demo-new-session', agent: 'copilot', title: 'Draft the release notes', repository: 'danswett/agent-bridge', branch: 'docs/release-notes',
      model: 'gpt-5.6-sol', status: 'working', statusSince: now(), startedAt: now(), updatedAt: now(),
      activity: { response: 'Reading the merged PRs since **v0.1.0**…', currentTool: { id: 'toolu_demo', name: 'gh', summary: 'gh pr list --state merged', status: 'running', startedAt: now() }, turnCount: 1 },
    }),
    () => {
      const s = host.get('019a1b2c-codex-example');
      if (!s) { return; }
      host.upsert({ ...s, status: 'working', statusSince: now(), updatedAt: now(), activity: { ...s.activity, response: 'Re-running the suite 20 times to be sure the fix holds.' } });
    },
    () => host.remove('demo-new-session'),
    () => {
      // One of the delegating session's background agents finishes, then another starts.
      const s = host.get('8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35');
      if (!s) { return; }
      const agents = s.activity?.background?.agents === 2 ? 1 : 2;
      host.upsert({ ...s, updatedAt: now(), activity: { ...s.activity, background: { agents, shells: 1 } } });
    },
    () => {
      const s = host.get('019a1b2c-codex-example');
      if (!s) { return; }
      host.upsert({ ...s, status: 'idle', statusSince: now(), updatedAt: now(), activity: { ...s.activity, response: 'Done. 20 of 20 runs passed.' } });
    },
  ];
  // An answered question gets a follow-up a little later, so the demo always has one to
  // answer: each kind of question in turn.
  let asked = 0;
  host.onTyped = (id) => setTimeout(() => {
    const s = host.get(id);
    if (!s || paused) { return; }
    const question = { ...DEMO_QUESTIONS[asked++ % DEMO_QUESTIONS.length], id: `toolu_demo_${Date.now()}`, askedAt: now() };
    host.upsert({ ...s, status: 'waiting', statusSince: now(), updatedAt: now(), question });
  }, tickMs * 1.5);
  // A reply gets an answer a little later, and the session is idle again.
  host.onReplied = (id, said) => setTimeout(() => {
    const s = host.get(id);
    if (!s || paused || s.question) { return; }
    host.upsert({ ...s, status: 'idle', statusSince: now(), updatedAt: now(), activity: { ...s.activity, response: `On it: *${said.length > 80 ? `${said.slice(0, 79)}…` : said}* Done.`, currentTool: null } });
  }, tickMs);
  let k = 0, timer = null, paused = false;
  const tick = () => {
    if (!paused) { steps[k++ % steps.length](); }
    timer = setTimeout(tick, tickMs);
  };
  timer = setTimeout(tick, tickMs);
  return { pause() { paused = true; }, resume() { paused = false; }, stop() { clearTimeout(timer); } };
}

// What the demo asks after an answer: an approval, a single choice answered on a tap, and
// a question that wants only text.
const DEMO_QUESTIONS = [
  {
    kind: 'approval',
    message: 'Run `git push --force-with-lease origin p1/answer-pwa`?',
    fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'yes', label: 'Yes' }, { value: 'always', label: 'Yes, and don\'t ask again for git push' }, { value: 'no', label: 'No, and tell the agent what to do instead' }] }],
  },
  {
    kind: 'form',
    message: 'The relay tests pass. **Open the pull request now?**',
    fields: [{ name: 'answer', type: 'choice', required: true, allowOther: true, options: [{ value: 'later', label: 'Wait for the E2E run' }, { value: 'open', label: 'Open it now (Recommended)' }] }],
  },
  {
    kind: 'form',
    message: 'What should the release notes lead with?',
    fields: [],
  },
];

// Usage as a host reports it, around the time the demo is opened, so pace and resets read
// sensibly on any day: a month of Copilot credits, Claude's session and week, and Codex's
// two windows as its last transcript saw them a couple of hours ago.
export function demoUsage(now = Date.now()) {
  const at = (ms) => new Date(now + ms).toISOString();
  const d = new Date(now);
  const month = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), 1)).toISOString();
  const next = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 1)).toISOString();
  const H = 3600000;
  return {
    agents: [
      {
        agent: 'copilot', label: 'GitHub Copilot', account: 'danswett', plan: 'enterprise', source: 'api', measuredAt: at(-60000),
        windows: [{ key: 'plan', label: 'Plan', percent: 78.9, used: 1183313, limit: 1500000, unit: 'AIC', startsAt: month, resetsAt: next }],
      },
      {
        agent: 'claude', label: 'Claude Code', accountKey: '3f9a2c71b0de', plan: 'max', source: 'api', measuredAt: at(-4 * 60000),
        windows: [
          { key: 'session', label: 'Session (5h)', percent: 12, resetsAt: at(3 * H) },
          { key: 'weekly_all', label: 'Weekly', percent: 41.3, startsAt: at(-2 * 24 * H), resetsAt: at(5 * 24 * H) },
        ],
      },
      {
        agent: 'codex', label: 'Codex', plan: 'plus', source: 'transcript', measuredAt: at(-2 * H),
        windows: [
          { key: 'primary', label: '5h limit', percent: 23.5, resetsAt: at(2 * H) },
          { key: 'secondary', label: 'Weekly', percent: 61, resetsAt: at(3 * 24 * H) },
        ],
      },
    ],
  };
}