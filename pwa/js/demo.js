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
//   scriptLaunches(...outcomes) for the next launches, host.launchOptions for what it offers
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
import { checkAnswer } from './answers.js';
import { checkReply, typedForm } from './replies.js';
import { readQuestion } from './protocol.js';
import { USAGE } from './usage.js';
import { LAUNCH, checkLaunch, flattenPrompt } from './launch.js';
import { TRANSFER, signTransfer } from './transfer.js';
import { P256KeyPair } from './noise-p256.js';
import { deviceIdOf } from './identity.js';
import { fromUtf8 } from './bytes.js';
import { blobIdOf, fromBase64url, openBlob, storedSize, toBase64url } from './blobs.js';

const BLOB_FILE_MAX = 100 * 1024 * 1024;

const clone = (v) => JSON.parse(JSON.stringify(v));
const now = () => new Date().toISOString();

// The transport HostClient expects, connected straight to a DemoHost.
class DemoTransport {
  onmessage = null;
  onclose = null;
  constructor(host) { this.host = host; }
  send(message) {
    // Asynchronous, like a real channel, so ordering bugs show up here too.
    setTimeout(() => this.host.receive(this, message), 0);
    return Promise.resolve();
  }
  deliver(message) { setTimeout(() => this.onmessage?.(clone(message)), 0); }
  close() { this.host.transports.delete(this); }
}

export class DemoHost {
  transports = new Set();
  calls = [];
  seq = 0;
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

  // answering: false plays a host that can't answer (agent-ha-bridge still running, say);
  // replying: false, one that can't reply; attaching: false, one that takes no files; large:
  // false, one that takes no files through the blob store;
  // stopping: false, one that can't end sessions; approving: false, one with approving
  // Copilot's permission prompts turned off. stopWindowMs is how long a first press of
  // a busy session waits for its second. usage: false plays a host from before usage.get;
  // launching: false, one that can't launch sessions; transferring: false, one that takes no
  // sessions from other machines. readOnly: why it takes nothing from the phone, as hello says.
  constructor(fixture, { readOnly = null, machineName = 'Demo machine', answerDelayMs = 700, answering = true, replying = answering, attaching = replying, large = attaching, stopping = answering, approving = answering, stopWindowMs = 10000, usage = true, launching = answering, transferring = launching } = {}) {
    this.machine = { id: 'demo', name: machineName, os: 'windows', hostVersion: '0.1.0', dev: true, ...(readOnly ? { readOnly } : {}) };
    this.usage = demoUsage();
    this.answerDelayMs = answerDelayMs;
    this.stopWindowMs = stopWindowMs;
    this.capabilities = ['sessions.read', ...(answering ? ['sessions.answer'] : []), ...(answering && approving ? ['sessions.approve'] : []), ...(replying ? ['sessions.reply'] : []), ...(replying && attaching ? ['sessions.attach'] : []), ...(replying && attaching && large ? ['sessions.attach.blob'] : []), ...(stopping ? ['sessions.stop'] : []), ...(usage ? [USAGE.capability] : []), ...(launching ? [LAUNCH.capability] : []), ...(launching && transferring ? [TRANSFER.capability] : [])];
    this.sessions = new Map();
    for (const s of fixture.sessions ?? []) { this.sessions.set(s.id, s); }
    this.revision = fixture.revision ?? 1;
  }

  connect() {
    const t = new DemoTransport(this);
    this.transports.add(t);
    return t;
  }

  receive(t, { id, op, data }) {
    this.calls.push(op);
    const reply = (result) => t.deliver({ re: id, ok: true, result });
    switch (op) {
      case 'hello':
        return reply({ protocol: 1, machine: { ...this.machine, sessions: this.sessions.size }, capabilities: this.capabilities });
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
        const s = this.sessions.get(data?.id);
        return reply(s ? withHistory(s) : { missing: true });
      }
      case 'sessions.subscribe': {
        t.subscribed = true;
        reply({ revision: this.revision, count: this.sessions.size });
        for (const s of this.sessions.values()) { this.#event(t, 'session.upsert', withoutHistory(s)); }
        this.#event(t, 'sessions.synced', { revision: this.revision, ...(this.held ? { discoveryHeld: this.held } : {}) });
        return undefined;
      }
      case 'session.answer':
        return this.#answer(data, reply);
      case 'session.reply':
        if (!this.capabilities.includes('sessions.reply')) { break; }
        return this.#reply(data, reply);
      case 'attachment.start':
      case 'attachment.chunk':
      case 'attachment.discard':
        if (!this.capabilities.includes('sessions.attach')) { break; }
        return this.#attachment(op, data).then(reply, (e) => t.deliver({ re: id, ok: false, error: e.message }));
      case 'session.stop':
        if (!this.capabilities.includes('sessions.stop')) { break; }
        return this.#stop(data, reply);
      case LAUNCH.options:
        if (!this.capabilities.includes(LAUNCH.capability)) { break; }
        return reply(clone(this.launchOptions));
      case LAUNCH.launch:
        if (!this.capabilities.includes(LAUNCH.capability)) { break; }
        return this.#launch(data, reply);
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

  #launch(data, reply) {
    this.launchLog.push(clone(data ?? null));
    const request = typeof data?.request === 'string' ? data.request : '';
    const known = this.launches.get(request);
    if (known) { known.result.then(reply); return; }
    const step = this.#launchScript.shift() ?? null;
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const result = new Promise((resolve) => setTimeout(() => resolve(this.#decideLaunch(data ?? {}, scripted)), this.answerDelayMs));
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
      return { outcome: scripted.outcome, reason: scripted.reason ?? reasons[scripted.outcome] ?? 'from a newer host' };
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
    const result = new Promise((resolve) => setTimeout(() => resolve(this.#decideStop(data, scripted)), this.answerDelayMs));
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

  #reply(data, reply) {
    this.replyLog.push(clone(data ?? null));
    const request = typeof data?.request === 'string' ? data.request : '';
    const known = this.replies.get(request);
    if (known) { known.result.then(reply); return; }
    const step = this.#replyScript.shift() ?? 'delivered';
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const result = new Promise((resolve) => setTimeout(() => resolve(this.#decideReply(data, scripted)), this.answerDelayMs));
    this.replies.set(request, { result });
    result.then((r) => { if (!scripted.drop) { reply(r); } });
  }

  #decideReply(data, scripted) {
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
    this.typedReplies.push({ id: s.id, request: data.request, said, files: files.map(({ name, type, size }) => ({ name, type, size })) });
    this.upsert({ ...s, status: 'working', statusSince: now(), updatedAt: now(), activity: { ...s.activity, userMessage: said } });
    this.onReplied?.(s.id, said);
    if (outcome === 'delivered') { return { outcome }; }
    return { outcome, reason: scripted.reason ?? (outcome === 'unconfirmed' ? 'the session did not record the reply within 60 seconds' : 'from a newer host') };
  }

  #answer(data, reply) {
    this.answerLog.push(clone(data ?? null));
    const request = typeof data?.request === 'string' ? data.request : '';
    // A repeat, even of one still being delivered, gets the first one's outcome.
    const known = this.answers.get(request);
    if (known) { known.result.then(reply); return; }
    const step = this.#script.shift() ?? 'delivered';
    const scripted = typeof step === 'string' ? { outcome: step } : step;
    const result = new Promise((resolve) => setTimeout(() => resolve(this.#decide(data, scripted)), this.answerDelayMs));
    this.answers.set(request, { result });
    // drop: the host did the work, but its response never reached the phone.
    result.then((r) => { if (!scripted.drop) { reply(r); } });
  }

  #decide(data, scripted) {
    const s = this.sessions.get(data?.id);
    const q = s?.question ? readQuestion(s.question) : null;
    if (!q || q.id !== data.question) { return { outcome: 'stale', reason: 'the session is no longer asking that question' }; }
    const problem = checkAnswer(q, data);
    if (problem) { return { outcome: 'refused', reason: problem }; }
    const { outcome } = scripted;
    if (outcome === 'stale') { return { outcome, reason: scripted.reason ?? 'answered at the machine' }; }
    if (outcome === 'busy') { return { outcome, reason: scripted.reason ?? 'another answer or reply to this session is being delivered' }; }
    if (outcome === 'refused') { return { outcome, reason: scripted.reason ?? 'the session is running elevated' }; }
    // Typed: the question goes, and the session works on the answer.
    const said = data.text ?? Object.entries(data.fields ?? {}).map(([k, v]) => `${k}: ${JSON.stringify(v)}`).join('; ');
    this.typed.push({ id: s.id, question: q.id, request: data.request, said });
    this.upsert({ ...s, status: 'working', statusSince: now(), updatedAt: now(), question: null, activity: { ...s.activity, userMessage: said } });
    this.onTyped?.(s.id);
    if (outcome === 'delivered') { return { outcome }; }
    return { outcome, reason: scripted.reason ?? (outcome === 'unconfirmed' ? 'the session stopped answering' : 'from a newer host') };
  }

  #event(t, ev, data) { t.deliver({ ev, seq: ++this.seq, data }); }

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

  // A frame exactly as given, for testing what the client does with odd ones. A frame
  // with a numeric seq moves this host's counter on to it.
  frame(f) {
    if (Number.isInteger(f.seq)) { this.seq = f.seq; }
    for (const t of this.transports) { t.deliver(f); }
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